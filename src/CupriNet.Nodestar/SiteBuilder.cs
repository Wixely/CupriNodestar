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
    private IOracleHandler? _handler;

    /// <summary>The content handler, or a 404-everything placeholder when the author supplied none.</summary>
    internal IOracleHandler Handler => _handler ?? EmptySite;

    /// <summary>The named live feeds, keyed as a Pilgrim will attend them.</summary>
    internal IReadOnlyDictionary<string, IAuspiceSource> Feeds => _feeds;

    /// <summary>True once anything has been configured — used to warn about a node that serves nothing.</summary>
    internal bool IsConfigured => _handler is not null || _feeds.Count > 0;

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
        _feeds[name] = new DelegateAuspiceSource(source);
        return this;
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
