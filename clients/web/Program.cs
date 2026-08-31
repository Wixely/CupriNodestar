using System.Diagnostics;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
// Pilgrimage and ShrineSession live in CupriNet.Hosting the NAMESPACE but ship in the CupriNet.Shrine PACKAGE —
// upstream kept the namespace when it split them out, so nothing downstream had to be renamed.
using CupriNet.Hosting;
using CupriNet.Nodestar.Client;
using CupriNet.Rites;
using CupriNet.Vessel;

// The served on-ramp client.
//
// The page you are reading this from is a CLEARNET asset: an ordinary file from an ordinary HTTP server, with no
// overlay address. Everything below happens over WebRTC, and the site it fetches is the thing that is actually
// addressed on the network.
//
// There is NO CupriNode here, deliberately. A node binds sockets, and a browser has none
// (PlatformNotSupportedException, measured in Chromium). A visitor is a Pilgrim: a throwaway per-visit identity and
// a pinned Signet — see BrowserPilgrim.

Console.WriteLine("[cupri] client starting");

// Fire and return. Main must not await: NativeAOT-LLVM wasm runs on the browser's only thread, and blocking it
// kills the very event loop that completes the work. The page's rAF pump drives the continuations.
BrowserLoop.Run(BrowseAsync);
return;

/// <summary>Visit a link, then wait for the address bar to hand over another. Forever.</summary>
static async Task BrowseAsync()
{
    var suite = new BouncyCastleSuite();

    // The node that served this page inlined its own signed link, so the first visit needs no input.
    string? link = BrowserDataChannel.Seed();

    // Whether the current link belongs to the node that served this page. Only that node can be reconnected to
    // automatically: reconnecting needs a FRESH link, the only way to get one is an HTTP fetch, and this page has an
    // HTTP relationship with its origin and with nowhere else. A pasted link names a node we can dial but never ask
    // for a new link — so when one of those goes away, the address bar is genuinely the only way back.
    var origin = true;

    // Where the visitor has been. Links only ever ARRIVED before — there was no way to return to a node except by
    // finding its link again, which for a pasted one meant having kept it.
    //
    // The origin flag travels with each entry rather than being recomputed. Going back to the serving node has to
    // restore the ability to auto-reconnect to it, and going back to a pasted link must not claim an HTTP
    // relationship this page does not have with it — so the two have to be remembered together.
    var history = new Stack<(string Link, bool Origin)>();

    while (link is not null)
    {
        BrowserNavigation.SetCanGoBack(history.Count > 0);

        var departure = Departure.Ended;
        try
        {
            departure = await VisitAsync(link, suite);
        }
        catch (Exception ex)
        {
            // A failed visit must leave the client usable: say so in the chrome and either reconnect or fall back to
            // the address bar, rather than ending the session and leaving a page that looks alive but is not.
            Console.WriteLine($"[cupri] visit failed: {ex.GetType().Name}: {ex.Message}");
            BrowserNavigation.Status($"visit failed — {ex.Message}");
        }

        if (departure.Back && history.TryPop(out var previous))
        {
            (link, origin) = previous;
            Console.WriteLine("[cupri] back");
            continue;
        }

        if (departure.Link is { } next)
        {
            // The visitor navigated. Where they were becomes where Back returns to, and from here the address bar
            // owns where we are rather than the seed.
            history.Push((link, origin));
            link = next;
            origin = false;
            continue;
        }

        if (origin)
        {
            // The visit ended without the visitor asking it to: the channel dropped, or the node restarted under us.
            // Left alone this is the state the client used to sit in forever — connected-looking, actually dead.
            //
            // NOT pushed onto the history: this is the same node coming back, not somewhere new. Pushing it would
            // fill the history with one entry per reconnect, and Back would walk through a node's outages instead of
            // through the places the visitor actually went.
            (link, origin) = await ReconnectToOriginAsync();
            continue;
        }

        // The link that just ended goes back into the address bar. This is the one case the client cannot recover
        // from by itself — see BrowserNavigation.SuggestLink — so the least it can do is keep the way back to hand
        // rather than leaving the visitor to find the link again.
        BrowserNavigation.SuggestLink(link);

        BrowserNavigation.Status(history.Count > 0
            ? "idle — send that link again when the node is back, or go back"
            : "idle — send that link again when the node is back, or paste another");

        var idle = await WaitForDepartureAsync();

        if (idle.Back && history.TryPop(out var earlier))
        {
            (link, origin) = earlier;
            continue;
        }

        if (idle.Link is { } pasted)
        {
            history.Push((link, origin));
            link = pasted;
            origin = false;
        }
    }
}

/// <summary>
/// Waits for the serving node to come back and returns a freshly fetched link to it, or whatever the visitor pasted
/// while waiting — they should never be trapped watching a node that is not coming back.
/// </summary>
static async Task<(string Link, bool Origin)> ReconnectToOriginAsync()
{
    for (var attempt = 1; ; attempt++)
    {
        // 1, 2, 4, 8 seconds then flat. A restart is usually seconds, so the early retries are the ones that matter;
        // the cap keeps a node gone for the afternoon from being polled once a second until the tab is closed.
        var backoff = Math.Min(8, 1 << Math.Min(attempt - 1, 3));

        for (var remaining = backoff; remaining > 0; remaining--)
        {
            BrowserNavigation.Status($"connection lost — reconnecting in {remaining}s");
            if (await WaitASecondAsync() is { } pastedWhileWaiting) return (pastedWhileWaiting, false);
        }

        BrowserNavigation.Status("reconnecting …");

        // The HTTP fetch is the probe, and it is a much better one than a dial. It comes back in milliseconds when
        // the node is down (connection refused) where a dial to a dead endpoint costs an ICE timeout — about fifteen
        // seconds of looking like progress while nothing is happening.
        //
        // It also has to succeed before there is anything worth dialling: a restarted node regenerated its ICE
        // credentials and DTLS certificate, so the link held from before the restart names coordinates nobody is
        // listening on, and no amount of patience makes it work.
        var before = BrowserDataChannel.SeedSerial();
        BrowserDataChannel.RequestSeedRefresh();

        for (var i = 0; i < 3; i++)
        {
            if (await WaitASecondAsync() is { } pastedWhileFetching) return (pastedWhileFetching, false);
            if (BrowserDataChannel.SeedSerial() != before) return (BrowserDataChannel.Seed(), true);
        }
    }
}

/// <summary>
/// About a second, measured on a real clock and yielding a frame at a time — and abandoned early if the visitor
/// pastes a link, so the address bar stays responsive throughout a reconnect.
/// </summary>
/// <remarks>
/// Wall clock rather than a frame count: <c>requestAnimationFrame</c> is throttled hard in a background tab, so
/// "sixty frames" there can be a minute. There is no <c>Task.Delay</c> to use instead — this runtime has no timer
/// thread behind one, which is the same reason <see cref="BrowserLoop"/> exists at all.
/// </remarks>
static async Task<string?> WaitASecondAsync()
{
    var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

    while (Stopwatch.GetTimestamp() < deadline)
    {
        await BrowserLoop.NextFrameAsync().ConfigureAwait(false);
        if (BrowserNavigation.TakePendingLink() is { } link) return link;
    }

    return null;
}

/// <summary>
/// One visit: dial, pilgrimage, fetch, render, then stream until the visitor navigates away. Returns the link they
/// navigated to, or null if the visit simply ended.
/// </summary>
static async Task<Departure> VisitAsync(string link, BouncyCastleSuite suite)
{
    if (!IntonationUri.TryParse(link, out var intonation, out var reason))
        throw new InvalidOperationException($"that link is unusable: {reason}");

    // A Pilgrim pins the Shrine's Signet. Without one in the link there is nothing to visit — a connection with
    // nothing to ask for.
    if (intonation.Shrine is not { } signet)
        throw new InvalidOperationException("that node advertises no site in its link");

    BrowserNavigation.Status($"dialling {intonation.Network} …");
    Console.WriteLine($"[cupri] dialling {intonation.Network} …");

    await using var channel = await BrowserDataChannel.ConnectAsync(intonation, CancellationToken.None);
    Console.WriteLine("[cupri] datachannel open");
    Console.WriteLine($"[cupri] sctp negotiated max message {BrowserDataChannel.NegotiatedMaxMessageBytes} bytes");

    // From here down, nothing browser-specific remains: the DataChannel becomes a Vessel and the same protocol
    // code the node runs takes over — which is the entire reason to compile C# to wasm rather than reimplement it.
    var vessel = new DataChannelVessel(channel);
    await using var shrine = await Pilgrimage.OverVesselAsync(
        vessel, signet, intonation.Network, suite, cancellationToken: CancellationToken.None);

    Console.WriteLine("[cupri] pilgrimage complete — the Signet answered");
    BrowserNavigation.Status($"connected — {Bech32.Fingerprint(signet)}");

    // The PAGE loop, inside the one Pilgrimage.
    //
    // A link inside a site changes the document and nothing else: the connection, the handshake and the pinned
    // Signet all stay as they are, and the new page is one Oracle round trip away. Treating it as a departure
    // would tear down a WebRTC session and dial the same node again for a page already reachable.
    var path = "/index.html";
    while (true)
    {
        // ONE consult for the whole document. An Oracle response is a single message over a channel where every fetch
        // costs a full round trip, so a site that links its stylesheet pays twice for one page — and CupriFace reads
        // <style> elements out of the DOM anyway, so a self-contained document is both cheaper and the natural shape.
        var page = await shrine.ConsultAsync(OracleRequest.Get(path));
        Console.WriteLine($"[cupri] site answered {page.Status} ({page.Body.Length} bytes, {page.ContentType})");
        // Loaded, not yet shown: the page is a template until the first feed message binds it, so the renderer holds
        // the first paint back rather than flashing "{{ node.site }}" at the visitor.
        var html = page.AsText();
        BrowserRenderer.Show(html);
        Console.WriteLine("[cupri] document loaded — holding the first paint until the feed binds it");

        // Which feed this page is about, according to the page. Read before attending, because attending is the next
        // thing that happens — and a client that renders whatever site it is pointed at has no business knowing any
        // particular site's feed names.
        var feed = SiteManifest.FeedName(html);
        if (feed != SiteManifest.DefaultFeed) Console.WriteLine($"[cupri] the site declares its feed as '{feed}'");

        // The visit ends when the visitor navigates. Watching runs alongside the feed rather than between messages: an
        // idle feed can be silent indefinitely, and an address bar that only responds when data happens to arrive would
        // feel broken exactly when the node is quiet.
        using var navigating = new CancellationTokenSource();
        var navigated = new TaskCompletionSource<Departure>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = WatchForNavigationAsync(navigating, navigated);

        // Live data over the same session, on its own stream — the page fetch and the feed share one Pilgrimage. Each
        // message binds into the document and repaints: the site has no JavaScript engine, so keeping the view current
        // is the client's job, not the page's.
        try
        {
            await foreach (var frame in shrine.AttendAsync(feed, navigating.Token))
            {
                Console.WriteLine($"[cupri] feed {frame.Kind} ({frame.Payload.Length} bytes)");
                if (frame.Kind is AuspiceFrameKind.Snapshot or AuspiceFrameKind.Update)
                    BrowserRenderer.Update(frame.Payload);
                else if (frame.Kind is AuspiceFrameKind.Sealed)
                    Console.WriteLine($"[cupri] feed sealed: {frame.AsText()}");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the watcher cancels the attend the moment the visitor goes anywhere, including to another page
            // of this same site.
        }

        var departure = navigated.Task.IsCompletedSuccessfully ? navigated.Task.Result : Departure.Ended;

        // Another page of the same site keeps the Pilgrimage and goes round again. Everything else ends the visit.
        if (departure.Path is not { } nextPath)
        {
            if (departure.Link is not null) Console.WriteLine("[cupri] leaving for another node");
            return departure;
        }

        Console.WriteLine($"[cupri] following a link within the site to {nextPath}");
        BrowserNavigation.Status($"loading {nextPath} …");
        path = nextPath;
        }
    }

    /// <summary>Ends the current visit the moment the visitor asks to go somewhere, and says where.</summary>
    static async Task WatchForNavigationAsync(CancellationTokenSource navigating, TaskCompletionSource<Departure> navigated)
    {
        while (!navigating.IsCancellationRequested)
        {
            await BrowserLoop.NextFrameAsync().ConfigureAwait(false);

            var departure =
                BrowserNavigation.TakePendingLink() is { } next ? Departure.To(next)
                : BrowserNavigation.TakeBackRequest() ? Departure.Backwards
                : FollowedLink() is { } inside ? inside
                : (Departure?)null;

            if (departure is null) continue;

            navigated.TrySetResult(departure.Value);
            await navigating.CancelAsync().ConfigureAwait(false);
            return;
        }
    }

    /// <summary>
    /// A link the SITE followed, resolved into something this client is willing to act on.
    ///
    /// <para><b>This is a security boundary, not routing.</b> The href comes out of a document a remote node served, so
    /// it is the one place a site influences where the client goes. Exactly two answers are allowed: another page of
    /// the same site, fetched over the Pilgrimage already open, or another CupriNet node named by a <c>cupri1…</c>
    /// link. Anything else — <c>http:</c>, <c>javascript:</c>, <c>data:</c>, a protocol handler — is refused and said
    /// so, because a site that could send the browser to an arbitrary URL could send a visitor somewhere this client's
    /// whole design exists to avoid.</para>
    ///
    /// <para>Returning null is the common case: nothing was followed this frame.</para>
    /// </summary>
    static Departure? FollowedLink()
    {
        if (BrowserRenderer.TakeNavigation() is not { } href || string.IsNullOrWhiteSpace(href)) return null;

        var target = href.Trim();

        // Another node, named the only way a node can be named.
        if (IntonationUri.TryParse(target, out _, out _)) return Departure.To(target);

        // A scheme of any kind is a site trying to leave CupriNet. There are no off-network links here.
        if (target.Contains("://", StringComparison.Ordinal) ||
            target.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[cupri] refused an off-network link: {target}");
            return null;
        }

        // Everything else is a path on the site being visited, rooted so a relative href cannot climb anywhere
        // unexpected — the Oracle takes a path, and this client has no notion of a current directory.
        return Departure.Within(target.StartsWith('/') ? target : "/" + target);
    }

    /// <summary>Waits for the address bar, or for Back — the two ways out of an idle client.</summary>
    static async Task<Departure> WaitForDepartureAsync()
    {
        while (true)
        {
            await BrowserLoop.NextFrameAsync().ConfigureAwait(false);
            if (BrowserNavigation.TakePendingLink() is { } link) return Departure.To(link);
            if (BrowserNavigation.TakeBackRequest()) return Departure.Backwards;
    }
}

/// <summary>
/// How a visit ended: the visitor went somewhere, went back, or it simply stopped.
///
/// <para>Three outcomes rather than a nullable link, because "went back" and "stopped on its own" need entirely
/// different answers — one pops the history, the other tries to reconnect — and a null cannot tell them apart.</para>
/// </summary>
internal readonly record struct Departure(string? Link, bool Back, string? Path)
{
    /// <summary>The visit ended without the visitor asking: the channel dropped, or the node went away.</summary>
    public static Departure Ended => new(null, false, null);

    public static Departure To(string link) => new(link, false, null);

    public static Departure Backwards => new(null, true, null);

    /// <summary>
    /// Another page of the SAME site, reached by a link inside it.
    ///
    /// <para>Not a departure at all: the Pilgrimage stays open and only the document changes. Collapsing this into
    /// <see cref="To"/> would make every in-site link tear down a WebRTC session and dial the same node again for a
    /// page that was one round trip away.</para>
    /// </summary>
    public static Departure Within(string path) => new(null, false, path);
}
