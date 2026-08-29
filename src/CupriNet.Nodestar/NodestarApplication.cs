using System.Net;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Abstractions;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
using CupriNet.Vessel;
using Microsoft.Extensions.Logging;

namespace CupriNet.Nodestar;

/// <summary>
/// A running Nodestar: a CupriNode that also hosts an L2 site (a Shrine) at a self-authenticating <c>cupri1…</c>
/// address, plus — when enabled — a clearnet HTTP front.
/// </summary>
public sealed class NodestarApplication : IAsyncDisposable
{
    private readonly NodestarOptions _options;
    private readonly SiteBuilder _site;
    private readonly ILogger _log;
    private readonly Func<NodestarOptions, Action<string>, IWebRtcTransport?>? _transportFactory;
    private readonly Func<NodestarOptions, ISecretStore, Action<string>, CancellationToken, Task<IOnionTransport?>>? _onionFactory;
    private readonly Func<string, ClientAsset?>? _clientAssets;
    private readonly IReadOnlyList<Func<NodestarApplication, CancellationToken, Task>> _started;
    private CupriNode? _node;
    private IWebRtcTransport? _webRtc;
    private Signet? _signet;

    internal NodestarApplication(
        NodestarOptions options,
        SiteBuilder site,
        ILoggerFactory loggerFactory,
        Func<NodestarOptions, Action<string>, IWebRtcTransport?>? transportFactory = null,
        Func<NodestarOptions, ISecretStore, Action<string>, CancellationToken, Task<IOnionTransport?>>? onionFactory = null,
        Func<string, ClientAsset?>? clientAssets = null,
        IReadOnlyList<Func<NodestarApplication, CancellationToken, Task>>? startedCallbacks = null)
    {
        _options = options;
        _site = site;
        _log = loggerFactory.CreateLogger("Nodestar");
        _transportFactory = transportFactory;
        _onionFactory = onionFactory;
        _clientAssets = clientAssets;
        _started = startedCallbacks ?? [];
    }

    /// <summary>The host's logger, so an <c>OnStarted</c> callback reports through the same sink as the host.</summary>
    public ILogger Logger => _log;

    /// <summary>Creates a builder bound to <c>appsettings.json</c>, <c>CUPRINET_NODESTAR_*</c> and the command line.</summary>
    public static NodestarApplicationBuilder CreateBuilder(string[]? args = null) => new(args ?? []);

    /// <summary>The site's <c>cupri1…</c> address, available once <see cref="StartAsync"/> has returned.</summary>
    public string? SiteAddress => _node?.ShrineAddress;

    /// <summary>The running node, available once <see cref="StartAsync"/> has returned.</summary>
    public CupriNode Node => _node ?? throw new InvalidOperationException("The Nodestar has not been started yet.");

    /// <summary>
    /// Starts the node and begins hosting the site. Returns once both are up. Calling it twice is a no-op rather
    /// than an error, so <see cref="RunAsync"/> works whether or not the caller started the app itself.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_node is not null) return;

        var dataDir = Path.GetFullPath(_options.DataDirectory);
        Directory.CreateDirectory(dataDir);
        _log.LogInformation("Data directory: {Dir}", dataDir);

        // Everything durable lives behind one encrypted store: the node's own identity, the site's Signet, and the
        // known-peer cache. It is why an address survives a restart — and why losing this directory changes it.
        var suite = new BouncyCastleSuite();
        var masterKey = KeyFileMasterKey.LoadOrCreate(Path.Combine(dataDir, "master.key"));
        var store = new FileSecretStore(Path.Combine(dataDir, "secrets"), new AeadDataProtector(suite, masterKey));

        var onionOnly = _options.TorOnly;
        var torRequested = _options.EnableTor || onionOnly;

        // The onion transport, when the Tor package supplied one. Bootstrapping Tor is slow — minutes on a cold
        // start — so its progress is logged rather than swallowed.
        IOnionTransport? onion = null;
        if (torRequested)
        {
            if (_onionFactory is null)
                throw new InvalidOperationException(
                    "Tor was requested (EnableTor or TorOnly) but no onion transport is configured. Add the "
                    + "CupriNet.Nodestar.Tor package and call builder.UseTor(). Starting without it would serve this "
                    + "site over clearnet only, while the configuration says otherwise — which is the one failure "
                    + "mode an anonymity setting must never have.");

            _log.LogInformation(onionOnly
                ? "Tor (onion-only): building the onion transport — this takes a while."
                : "Tor (dual-stack: clearnet + onion): building the onion transport — this takes a while.");

            onion = await _onionFactory(_options, store, message => _log.LogInformation("Tor {Status}", message), cancellationToken)
                .ConfigureAwait(false);

            if (onion is null)
                throw new InvalidOperationException(
                    "The onion transport could not be created, and Tor was requested. Refusing to start: a node that "
                    + "silently falls back to clearnet is worse than one that does not start.");
        }

        // The browser on-ramp, when the WebRtc package supplied one. Its ICE credentials and DTLS fingerprint are
        // stamped into every Intonation this node mints, which is what lets a browser dial back with no signalling.
        //
        // Skipped entirely in onion-only mode, and that is not an optimisation: WebRTC is a clearnet UDP transport,
        // so offering it would publish the very IP the onion exists to hide and drag a visitor off their own Tor
        // path. The two are mutually exclusive by nature, not by policy.
        _webRtc = _transportFactory?.Invoke(_options, message => _log.LogInformation("{Message}", message));

        _node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = _options.Concordium,
            ListenAddress = ParseAddress(_options.ListenAddress),
            ListenPort = _options.ListenPort,
            Suite = suite,
            SecretStore = store,
            Moniker = _options.Moniker,
            WebRtcTransport = _webRtc,
            OnionTransport = onion,
            // Standard + an OnionTransport is dual-stack (clearnet AND onion); TorOnly enforces onion-only.
            Mode = onionOnly ? ReachabilityMode.TorOnly : ReachabilityMode.Standard,

            // Lodestar-grade defaults: a Nodestar is expected to be reachable and to stay warm, because it is
            // something other people visit rather than something that dials out occasionally.
            PersistOverlay = true,
            EnableOverlayGossip = true,
            OverlayGossipIntervalSeconds = _options.GossipIntervalSeconds,
            OverlayGossipFanout = _options.GossipFanout,
            EnableLanDiscovery = !onionOnly && _options.EnableLanDiscovery,
            EnablePortMapping = !onionOnly && _options.EnablePortMapping,
            EnableFerryman = !onionOnly && _options.EnableFerryman,
            Power = PowerProfile.Unmetered,
        }, cancellationToken).ConfigureAwait(false);

        // The Signet is the site's identity and its URL at once. It is persisted under a name, so the address is
        // stable across restarts and redeploys as long as the data directory is.
        var signet = await new SignetStore(store)
            .LoadOrCreateAsync(suite, _options.SiteName, cancellationToken)
            .ConfigureAwait(false);

        // Kept so AcceptPilgrimageAsync can serve this site over a vessel the caller supplies.
        _signet = signet;

        if (!_site.IsConfigured)
            _log.LogWarning("No site content configured — visitors will receive 404 for everything. Call builder.Site.ServeStaticFiles(...) or .Serve(...).");

        // Relics are not wired up yet (see TODO.md) and the conduit is null unless the site called OnSession. Null
        // is the meaningful answer in both cases rather than an omission: it is what lets the Shrine seal a visitor
        // who opens a session this site does not serve, instead of leaving them waiting on a reply.
        _node.HostShrine(signet, _site.Handler, _site.Feeds, null, _site.Conduit, _options.AdvertiseSiteInLink);

        await SeedAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("Nodestar online for network '{Network}'.", _options.Concordium);
        _log.LogInformation("Site address: {Address}", _node.ShrineAddress);
        if (_site.Feeds.Count > 0)
            _log.LogInformation("Live feeds: {Feeds}", string.Join(", ", _site.Feeds.Keys));
        if (_site.Conduit is not null)
            _log.LogInformation("Raw sessions: served.");
        if (_options.AdvertiseSiteInLink)
            _log.LogInformation("The site's Signet is stamped into this node's link — it is therefore linkable to the node's overlay identity.");

        // Post-start work that needed a live transport. A failure here is fatal on purpose: the only caller today is
        // the Tor package publishing the face onion, and a node that came up without the .onion an operator asked for
        // — while logging a healthy startup — is the silent-clearnet failure again, wearing a different hat.
        foreach (var callback in _started)
            await callback(this, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts, then runs until the process is asked to stop (Ctrl+C / SIGTERM) or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Named handlers, unsubscribed in the finally below, rather than lambdas left attached.
        //
        // As anonymous handlers these OUTLIVED the token source they captured: `stopping` is disposed the moment
        // this method returns, ProcessExit then fires during shutdown and calls Cancel on a disposed object, and the
        // process ends with an ObjectDisposedException after a completely successful run. Every clean shutdown hit
        // it — Ctrl+C included — and it went unnoticed because the nodes in testing were killed rather than stopped.
        // Found by running a host built from the published packages and letting it exit normally.
        //
        // Unsubscribing also stops a second RunAsync from stacking another pair onto process-wide events.
        void Stop()
        {
            // The unsubscribe below closes the window this guards, but not atomically: a signal arriving between
            // dispose and unsubscribe would still land here.
            try { stopping.Cancel(); } catch (ObjectDisposedException) { }
        }

        ConsoleCancelEventHandler onCancelKey = (_, e) => { e.Cancel = true; Stop(); };
        EventHandler onProcessExit = (_, _) => Stop();

        Console.CancelKeyPress += onCancelKey;
        AppDomain.CurrentDomain.ProcessExit += onProcessExit;

        try
        {
            await StartAsync(stopping.Token).ConfigureAwait(false);
            await RunFrontAsync(stopping.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancelKey;
            AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
        }

        _log.LogInformation("Nodestar stopping.");
    }

    /// <summary>Serves the clearnet front until cancelled, or simply waits when there is no front to serve.</summary>
    private async Task RunFrontAsync(CancellationToken stopping)
    {
        try
        {
            if (_options.EnableWebFront)
            {
                var links = new NodestarLinkProvider(
                    Node,
                    TimeSpan.FromMinutes(_options.LinkLifetimeMinutes),
                    TimeSpan.FromSeconds(_options.LinkRefreshSeconds));

                // siteAddress is read through a delegate rather than captured: it is only known after the Shrine is
                // hosted, and a later multi-Shrine node may change it while the front is running.
                var gateway = _options.EnableGateway ? new SiteGateway(_site) : null;
                var front = new NodestarWebFront(links, _options, () => SiteAddress, gateway, _clientAssets, _log);
                await front.RunAsync(stopping).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The normal way to stop.
        }
    }

    /// <summary>
    /// Serves this site to one Pilgrim over a vessel you supply, for as long as they stay.
    ///
    /// <para><b>Why this exists.</b> A visitor reaches a site over WebRTC because an accepted DataChannel routes into
    /// the Pilgrimage on its own. Nothing else does. A TCP connection to this node's listen port reaches the NODE —
    /// it completes a node-to-node handshake presenting the node's own Sigil — so pinning the site's Signet against
    /// it fails, and pinning the node's Sigil instead succeeds into a session with no Shrine behind it. That second
    /// outcome is the dangerous one: the handshake reports success and every rite then answers with a closed stream,
    /// which reads as "the rite is broken" rather than "you are not talking to a site". Reported as #2.</para>
    ///
    /// <para>So this is the seam for every transport that is not WebRTC: a test harness pairing two vessels in
    /// process, a desktop client over TCP, anything that can produce an <see cref="IVessel"/>. The caller owns
    /// accepting the connection; this owns what the site answers with. Pin <see cref="SiteAddress"/> from the other
    /// end — the Signet, not the node's Sigil — because this time it is genuinely the site that answers.</para>
    ///
    /// <para><b>This is the intended path, not a workaround.</b> CupriNet's <c>design/transports-and-limits.md</c>
    /// now says so directly: a Shrine accepts any vessel, but the vessel has to be handed to
    /// <c>AcceptPilgrimageOverVesselAsync</c> by a caller who owns it, and a node's L1 listen port does not serve
    /// Shrines. It also names the symptom above — a visit that pairs you as an overlay peer while every rite goes
    /// quiet — as the single most misread symptom in the stack, which is exactly how it reached us.</para>
    ///
    /// <para>Returns when that Pilgrim's visit ends. Run it per accepted vessel; it does not loop.</para>
    /// </summary>
    public Task AcceptPilgrimageAsync(IVessel vessel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);

        if (_node is null || _signet is null)
            throw new InvalidOperationException(
                "The Nodestar has not been started yet, so it has no Signet to answer with. Call StartAsync first.");

        // Relics stay null until they are wired (TODO.md); the conduit is null unless the site called OnSession, and
        // null is what lets the Shrine seal a visitor who opens a session this site does not serve.
        return _node.AcceptPilgrimageOverVesselAsync(
            vessel, _signet, _site.Handler, _site.Feeds, null, _site.Conduit, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync().ConfigureAwait(false);
            _node = null;
        }

        // NOT disposed here: handing the transport to CupriNodeOptions transfers ownership, and CupriNode disposes it
        // (DisposeWebRtcAsync). Disposing it again throws ObjectDisposedException out of DisposeAsync — which any
        // host would hit on a clean shutdown, and which surfaced first as a test-cleanup failure.
        _webRtc = null;
    }

    /// <summary>
    /// Bootstraps the overlay from configured seed links. Each is an L1 pairing that exchanges peer records and is
    /// then dropped — a Nodestar learns who else exists, it does not hold a channel open to them.
    ///
    /// <para>Every failure is warned about and swallowed. A node whose seed is offline is not a broken node: it is a
    /// node that has not met anyone yet, and it must still come up and serve its own site.</para>
    /// </summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (_options.SeedLinks.Count == 0 || _node is null) return;

        _log.LogInformation("Bootstrapping from {Count} seed link(s)…", _options.SeedLinks.Count);

        foreach (var seed in _options.SeedLinks)
        {
            if (string.IsNullOrWhiteSpace(seed)) continue;
            try
            {
                if (!IntonationUri.TryParse(seed, out var intonation, out _))
                {
                    _log.LogWarning("Malformed seed link ignored.");
                    continue;
                }

                var peer = await _node.ConjoinAsync(intonation, DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
                await peer.DisposeAsync().ConfigureAwait(false);
                _log.LogInformation("Seeded from a peer.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Seed link failed: {Reason}", ex.Message);
            }
        }
    }

    private static IPAddress ParseAddress(string value)
        => IPAddress.TryParse(value, out var address)
            ? address
            : throw new ArgumentException($"'{value}' is not a valid IP address.", nameof(value));
}
