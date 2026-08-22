using System.Net;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Persistence;
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
    private CupriNode? _node;

    internal NodestarApplication(NodestarOptions options, SiteBuilder site, ILoggerFactory loggerFactory)
    {
        _options = options;
        _site = site;
        _log = loggerFactory.CreateLogger("Nodestar");
    }

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

        _node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = _options.Concordium,
            ListenAddress = ParseAddress(_options.ListenAddress),
            ListenPort = _options.ListenPort,
            Suite = suite,
            SecretStore = store,
            Moniker = _options.Moniker,
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

        if (!_site.IsConfigured)
            _log.LogWarning("No site content configured — visitors will receive 404 for everything. Call builder.Site.ServeStaticFiles(...) or .Serve(...).");

        _node.HostShrine(signet, _site.Handler, _site.Feeds, _options.AdvertiseSiteInLink);

        await SeedAsync(cancellationToken).ConfigureAwait(false);

        _log.LogInformation("Nodestar online for network '{Network}'.", _options.Concordium);
        _log.LogInformation("Site address: {Address}", _node.ShrineAddress);
        if (_site.Feeds.Count > 0)
            _log.LogInformation("Live feeds: {Feeds}", string.Join(", ", _site.Feeds.Keys));
        if (_options.AdvertiseSiteInLink)
            _log.LogInformation("The site's Signet is stamped into this node's link — it is therefore linkable to the node's overlay identity.");
    }

    /// <summary>Starts, then runs until the process is asked to stop (Ctrl+C / SIGTERM) or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stopping.Cancel();

        await StartAsync(stopping.Token).ConfigureAwait(false);

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
                var front = new NodestarWebFront(links, _options, () => SiteAddress, gateway, _log);
                await front.RunAsync(stopping.Token).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The normal way to stop.
        }

        _log.LogInformation("Nodestar stopping.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null)
        {
            await _node.DisposeAsync().ConfigureAwait(false);
            _node = null;
        }
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
