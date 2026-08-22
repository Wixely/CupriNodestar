using CupriNet.Rites;
using Microsoft.AspNetCore.Http;

namespace CupriNet.Nodestar;

/// <summary>
/// Mode 2 — the L2→HTTP gateway. Turns an ordinary HTTP request into an Oracle consult and writes the answer back as
/// a normal HTTP response, so a browser with no WebRTC (behind a Cloudflare tunnel, or over an onion) still sees the
/// site. This is what makes the deployment matrix work everywhere.
///
/// <para><b>Why this calls the handler directly instead of running a loopback Pilgrimage.</b> The design sketched
/// Mode 2 as the node running a Pilgrim client against its own Shrine over loopback. In practice that would mean a
/// second <c>CupriNode</c> in-process (the Pilgrim is a node-level API), a TCP listener, and a Noise handshake per
/// visit — to reach an <see cref="IOracleHandler"/> this process already holds a reference to. The point of the
/// loopback framing was that <b>no third party sits in the middle</b>, and calling the handler satisfies that more
/// strongly: nothing reaches a wire at all. The content is byte-identical either way, because both paths end at the
/// same handler; only serialisation is skipped, and serialisation cannot change what the site said.</para>
///
/// <para>This holds <b>only for a Shrine this node hosts</b>. Gatewaying somebody else's Shrine genuinely does need a
/// Pilgrimage, and would also make this node a content-seeing proxy — which is why v1 scopes gateway mode to
/// own-hosted Shrines. When foreign-Shrine gatewaying arrives, that path gets a real Pilgrim; this one stays as it
/// is.</para>
/// </summary>
internal sealed class SiteGateway(SiteBuilder site)
{
    /// <summary>How long a snapshot request waits for a feed to produce its opening state before giving up.</summary>
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);

    public async Task HandleAsync(HttpContext context)
    {
        var method = context.Request.Method;
        if (method is not ("GET" or "HEAD"))
        {
            // Refuse explicitly rather than quietly treating everything as a GET. A site that grows write endpoints
            // should do so on purpose, with a decision about what a gateway is allowed to forward.
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        // The path (with query) is all that crosses. The visitor's headers deliberately do NOT: forwarding a browser's
        // User-Agent, Accept-Language and the rest would push a fingerprint into L2 for no gain, and on an onion
        // deployment that is exactly the kind of leak the onion exists to prevent.
        var path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        if (context.Request.QueryString.HasValue) path += context.Request.QueryString.Value;

        var response = await site.Handler
            .HandleAsync(OracleRequest.Get(path), context.RequestAborted)
            .ConfigureAwait(false);

        context.Response.StatusCode = (int)response.Status;
        if (response.ContentType is { Length: > 0 } contentType)
            context.Response.ContentType = contentType;

        // Mode 2 renders a point-in-time snapshot and cannot push updates — a finished HTML page has no channel back
        // that the no-WebSockets rule permits. Saying so in a header beats letting a stale page look live.
        context.Response.Headers["X-Nodestar-Mode"] = "gateway";
        context.Response.Headers.CacheControl = "no-store";

        if (method == "HEAD")
        {
            context.Response.ContentLength = response.Body.Length;
            return;
        }

        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a feed only far enough to capture its opening state, then stops it. This is the Mode-2 answer to live
    /// data: the same snapshot a Mode-1 visitor receives when they attend, served once instead of streamed — one
    /// state serialisation, two consumers.
    /// </summary>
    public async Task<byte[]?> SnapshotAsync(string feed, CancellationToken cancellationToken)
    {
        if (!site.Feeds.TryGetValue(feed, out var source)) return null;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(SnapshotTimeout);
        var capture = new FirstSnapshot(feed);

        // The source runs until cancelled; we only want its first message, so cancel as soon as it arrives. A source
        // that never snapshots hits the timeout and yields null rather than hanging the request.
        var running = Task.Run(async () =>
        {
            try { await source.EmanateAsync(capture, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected: we stopped it */ }
            catch (Exception ex) { capture.Fail(ex); }
        }, CancellationToken.None);

        var snapshot = await capture.WaitAsync(stop.Token).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        await running.ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>Captures the first message a feed emits and ignores the rest.</summary>
    private sealed class FirstSnapshot(string topic) : IAuspicePublisher
    {
        private readonly TaskCompletionSource<byte[]?> _first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Topic => topic;

        public Task SnapshotAsync(byte[] payload, CancellationToken cancellationToken = default)
        {
            _first.TrySetResult(payload);
            return Task.CompletedTask;
        }

        // A well-formed source sends its snapshot first, so an update arriving before one means the source is
        // malformed. Completing on it anyway keeps a misbehaving feed from stalling the gateway.
        public Task UpdateAsync(byte[] payload, CancellationToken cancellationToken = default)
        {
            _first.TrySetResult(payload);
            return Task.CompletedTask;
        }

        public void Fail(Exception ex) => _first.TrySetException(ex);

        public async Task<byte[]?> WaitAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _first.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;   // timed out, or the visitor left
            }
        }
    }
}
