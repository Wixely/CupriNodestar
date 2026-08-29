using CupriNet.Alembic;
using CupriNet.Rites;

namespace CupriNet.Nodestar;

/// <summary>
/// What this Nodestar serves on L2 — the one part the developer actually supplies.
///
/// <para>This is <b>plain naming over the CupriNet Lexicon</b>: a "site" is a Shrine, <see cref="Serve"/> maps onto the
/// Oracle rite, and <see cref="Feed"/> maps onto the Auspice rite. An author writes websites and never learns the
/// vocabulary; the mapping is one adapter deep, so anyone who wants the real names can drop to them.</para>
/// </summary>
public sealed class SiteBuilder
{
    private readonly Dictionary<string, IAuspiceSource> _feeds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _relics = new(StringComparer.Ordinal);
    private Func<ICryptoSuite, IRelicSource>? _relicSource;
    private string? _relicRoot;
    private IOracleHandler? _handler;
    private IConduitHandler? _conduit;

    /// <summary>The content handler, or a 404-everything placeholder when the author supplied none.</summary>
    internal IOracleHandler Handler => _handler ?? EmptySite;

    /// <summary>The named live feeds, keyed as a Pilgrim will attend them.</summary>
    internal IReadOnlyDictionary<string, IAuspiceSource> Feeds => _feeds;

    /// <summary>The raw-session handler, or null when this site serves none. Null is the "no conduit" answer the
    /// Shrine expects, so a visitor who opens one is sealed rather than left waiting.</summary>
    internal IConduitHandler? Conduit => _conduit;

    /// <summary>True once anything has been configured — used to warn about a node that serves nothing.</summary>
    internal bool IsConfigured =>
        _handler is not null || _feeds.Count > 0 || _conduit is not null || HasRelics;

    private bool HasRelics => _relicSource is not null || _relics.Count > 0 || _relicRoot is not null;

    /// <summary>
    /// Builds the relic source, or null when this site names none.
    ///
    /// <para>Deferred until the node starts because hashing a relic needs the crypto suite, and the suite is the
    /// node's rather than the builder's. It is the same shape a feed uses — the builder holds the intent, the
    /// application supplies what only it has.</para>
    /// </summary>
    internal IRelicSource? BuildRelics(ICryptoSuite suite)
    {
        if (_relicSource is not null) return _relicSource(suite);
        if (!HasRelics) return null;

        var source = new StaticRelicSource(suite);

        foreach (var (name, content) in _relics)
            source.Add(name, content);

        if (_relicRoot is not null)
        {
            var root = Path.GetFullPath(_relicRoot);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // Forward slashes and a path relative to the root, so a relic is named the way a visitor asks for it
                // rather than the way this filesystem happens to spell it.
                var name = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                source.Add(name, File.ReadAllBytes(file));
            }
        }

        return source;
    }

    // A site with no handler answers 404 rather than dropping the Pilgrimage: a visitor who reached us should learn
    // that there is nothing here, not sit waiting on a session that will never answer.
    private static readonly IOracleHandler EmptySite =
        new DelegateOracleHandler(_ => OracleResponse.NotFound());

    /// <summary>
    /// Serves files from <paramref name="rootDirectory"/>. Path traversal is refused by CupriNet's
    /// <c>FilePathGuard</c>, so a request can never escape the root.
    /// </summary>
    public SiteBuilder ServeStaticFiles(string rootDirectory, string defaultDocument = "index.html")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _handler = new StaticFileOracleHandler(rootDirectory, defaultDocument);
        return this;
    }

    /// <summary>Serves through a request/response delegate — the L2 equivalent of a minimal-API endpoint.</summary>
    public SiteBuilder Serve(Func<OracleRequest, CancellationToken, Task<OracleResponse>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = new DelegateOracleHandler(handler);
        return this;
    }

    /// <summary>Serves through a synchronous request/response delegate.</summary>
    public SiteBuilder Serve(Func<OracleRequest, OracleResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = new DelegateOracleHandler(handler);
        return this;
    }

    /// <summary>Serves through your own <see cref="IOracleHandler"/>.</summary>
    public SiteBuilder Serve(IOracleHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Publishes a named live feed. The delegate runs for the whole of one visitor's subscription: send a snapshot,
    /// then updates, until the token is cancelled because they departed or the session ended. Returning ends the feed.
    ///
    /// <para><b>Send the snapshot first.</b> A visitor who attends a feed already in progress has no state, so the
    /// opening message is what stops the view being connected-but-empty. Everything after it is incremental.</para>
    ///
    /// <para><b>Each message is capped at 192 KiB</b> (<c>AuspiceCodec.MaxPayloadBytes</c>) — a ceiling the browser
    /// sets, not us. Feeds carry deltas and state; for anything large, transfer it another way.</para>
    /// </summary>
    public SiteBuilder Feed(string name, Func<IAuspicePublisher, CancellationToken, Task> source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        _feeds[name] = new DelegateAuspiceSource((publisher, cancellationToken) =>
            EmanateAsync(source, publisher, cancellationToken));
        return this;
    }

    /// <summary>
    /// Publishes named relics: bulk content, chunked and hash-verified, fetched over the same visit.
    ///
    /// <para><b>This is the answer to the 192 KiB ceiling.</b> Every rite caps one message at that size, because
    /// that is what a browser's SCTP association carries — so an image, a download or a WASM payload cannot travel
    /// as a page or a feed message. A relic travels chunk by chunk on its own stream instead, which also keeps it
    /// from stalling the page fetch beside it.</para>
    ///
    /// <para><b>And it buys something a large response never could:</b> every chunk is verified against a manifest
    /// as it arrives, and the whole file before any bytes are returned. A visitor can prove a blob's integrity
    /// <i>before</i> running it — so a hostile host can fail a fetch but cannot corrupt one.</para>
    ///
    /// <para>Read at startup, not per request: relics are hashed into a manifest when the node starts, so a file
    /// changed on disk afterwards is not picked up until it restarts. That is what makes the manifest a promise
    /// rather than a guess.</para>
    /// </summary>
    /// <param name="rootDirectory">A directory whose files become relics, named by their path relative to it.</param>
    public SiteBuilder ServeRelics(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _relicRoot = rootDirectory;
        return this;
    }

    /// <summary>Publishes one relic from memory, under a name a visitor fetches it by.</summary>
    public SiteBuilder ServeRelic(string name, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        _relics[name] = content;
        return this;
    }

    /// <summary>Publishes relics from your own <see cref="IRelicSource"/>, in the rite's own names.</summary>
    public SiteBuilder ServeRelics(Func<ICryptoSuite, IRelicSource> source)
    {
        _relicSource = source ?? throw new ArgumentNullException(nameof(source));
        return this;
    }

    /// <summary>
    /// Serves a raw session: a duplex, message-framed pipe for the life of one visitor's connection. This is the
    /// fourth thing a site can serve, alongside files, request/response and feeds — and the one that lets a protocol
    /// that already exists move onto L2 without being rewritten as consults and topics.
    ///
    /// <para>The delegate runs once per visitor who opens a session and for as long as they stay. Send and receive
    /// in whatever order the protocol calls for; returning ends the session, and so does
    /// <see cref="SiteSession.ReceiveAsync"/> answering null.</para>
    ///
    /// <para><paramref name="protocolId"/> is yours to choose and is not registered anywhere — it exists so a peer
    /// that dialled a different protocol is told so rather than fed frames it cannot read. Frames arriving under any
    /// other id end the session with "unknown protocol"; that check is here rather than left to each author, so the
    /// same mistake fails the same way everywhere.</para>
    ///
    /// <para><b>Each frame is capped at 192 KiB</b> before padding (<see cref="SiteSession.MaxFrameBytes"/>) — the
    /// browser's ceiling, not ours. A protocol that needs to move more than that should chunk, or carry the bulk as
    /// a relic.</para>
    /// </summary>
    public SiteBuilder OnSession(uint protocolId, Func<SiteSession, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _conduit = new DelegateConduitHandler((conduit, cancellationToken) =>
        {
            var session = new SiteSession(conduit, protocolId);

            // Draining starts BEFORE the handler runs, not on its first receive. Frames arrive from the moment the
            // conduit is open, and anything that queues on the rite before someone reads is exactly what gets
            // dropped without a word.
            session.Start(cancellationToken);
            return AttendAsync(handler, session, cancellationToken);
        });
        return this;
    }

    /// <summary>Serves a raw session through your own <see cref="IConduitHandler"/>, in the rite's own names.</summary>
    public SiteBuilder OnSession(IConduitHandler handler)
    {
        _conduit = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Runs a session and treats the visitor leaving as the end of the session, not as a failure.
    ///
    /// <para>The same rule as a feed, for the same reason. A session is duplex, so a departure is discovered by
    /// whichever of a send or a receive happens to race the close — and left alone that surfaces as a warning with a
    /// stack trace for every visitor who ever closes a tab. <see cref="SiteSession.ReceiveAsync"/> already answers a
    /// clean close with null; this catches the case where the author was mid-send when it happened.</para>
    /// </summary>
    private static async Task AttendAsync(
        Func<SiteSession, CancellationToken, Task> handler,
        SiteSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            await handler(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDeparture(ex))
        {
            // Nothing to report and nothing to do: the other end is gone.
        }
    }

    /// <summary>
    /// Runs a feed and treats the visitor leaving as the end of the feed, not as a failure.
    ///
    /// <para>A feed publishes on its own schedule, so a visitor closing their tab is discovered by a send that
    /// races the close and throws. That is the most ordinary event in the system — someone stopped reading — and
    /// left alone it surfaces as a warning with a stack trace for every departure. Logs that shout on ordinary
    /// events teach operators to stop reading them, which costs more than the noise.</para>
    ///
    /// <para>Everything else still propagates, and deliberately: an over-ceiling payload, a bug in a projection, a
    /// null in a model are all real and all reported. Only the departure is quiet.</para>
    /// </summary>
    private static async Task EmanateAsync(
        Func<IAuspicePublisher, CancellationToken, Task> source,
        IAuspicePublisher publisher,
        CancellationToken cancellationToken)
    {
        try
        {
            await source(publisher, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDeparture(ex))
        {
            // Nothing to report and nothing to do: the other end is gone.
        }
    }

    /// <summary>
    /// Whether an exception means "the peer went away" rather than "something is wrong".
    ///
    /// <para>The transport's closed-vessel exception is matched by NAME rather than by type. It lives in a CupriNet
    /// assembly this package does not reference, and which has changed across versions — importing a type purely to
    /// write a catch clause would pin the base package to a transport version for no other reason. Matching the name
    /// costs a string comparison on a path that only runs when a feed has already stopped.</para>
    /// </summary>
    private static bool IsDeparture(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or ObjectDisposedException) return true;
            if (current.GetType().Name is "VesselClosedException") return true;
        }

        return false;
    }

    /// <remarks>
    /// Feeds may be registered right up until the application starts. The dictionary handed to the Shrine is this
    /// live one, but relying on that is fragile — a feed added after a visitor has already attended will not reach
    /// them, so register before <c>RunAsync</c>.
    /// </remarks>
    /// <summary>Publishes a named live feed backed by your own <see cref="IAuspiceSource"/>.</summary>
    public SiteBuilder Feed(string name, IAuspiceSource source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        _feeds[name] = source;
        return this;
    }
}
