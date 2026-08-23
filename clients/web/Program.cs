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

    while (link is not null)
    {
        string? next = null;
        try
        {
            next = await VisitAsync(link, suite);
        }
        catch (Exception ex)
        {
            // A failed visit must leave the client usable: say so in the chrome and fall back to the address bar,
            // rather than ending the session and leaving a page that looks alive but is not.
            Console.WriteLine($"[cupri] visit failed: {ex.GetType().Name}: {ex.Message}");
            BrowserNavigation.Status($"visit failed — {ex.Message}");
        }

        if (next is null)
        {
            BrowserNavigation.Status("idle — paste an intonation link to visit another node");
            next = await WaitForLinkAsync();
        }

        link = next;
    }
}

/// <summary>
/// One visit: dial, pilgrimage, fetch, render, then stream until the visitor navigates away. Returns the link they
/// navigated to, or null if the visit simply ended.
/// </summary>
static async Task<string?> VisitAsync(string link, BouncyCastleSuite suite)
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

    // From here down, nothing browser-specific remains: the DataChannel becomes a Vessel and the same protocol
    // code the node runs takes over — which is the entire reason to compile C# to wasm rather than reimplement it.
    var vessel = new DataChannelVessel(channel);
    await using var shrine = await Pilgrimage.OverVesselAsync(
        vessel, signet, intonation.Network, suite, cancellationToken: CancellationToken.None);

    Console.WriteLine("[cupri] pilgrimage complete — the Signet answered");
    BrowserNavigation.Status($"connected — {Bech32.Fingerprint(signet)}");

    // ONE consult for the whole document. An Oracle response is a single message over a channel where every fetch
    // costs a full round trip, so a site that links its stylesheet pays twice for one page — and CupriFace reads
    // <style> elements out of the DOM anyway, so a self-contained document is both cheaper and the natural shape.
    var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"));
    Console.WriteLine($"[cupri] site answered {page.Status} ({page.Body.Length} bytes, {page.ContentType})");
    BrowserRenderer.Show(page.AsText());
    Console.WriteLine("[cupri] painted");

    // The visit ends when the visitor navigates. Watching runs alongside the feed rather than between messages: an
    // idle feed can be silent indefinitely, and an address bar that only responds when data happens to arrive would
    // feel broken exactly when the node is quiet.
    using var navigating = new CancellationTokenSource();
    var navigated = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = WatchForNavigationAsync(navigating, navigated);

    // Live data over the same session, on its own stream — the page fetch and the feed share one Pilgrimage. Each
    // message binds into the document and repaints: the site has no JavaScript engine, so keeping the view current
    // is the client's job, not the page's.
    try
    {
        await foreach (var frame in shrine.AttendAsync("overlay", navigating.Token))
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
        Console.WriteLine("[cupri] leaving for another node");
    }

    return navigated.Task.IsCompletedSuccessfully ? navigated.Task.Result : null;
}

/// <summary>Cancels the current visit the moment a link is submitted, and hands it back.</summary>
static async Task WatchForNavigationAsync(CancellationTokenSource navigating, TaskCompletionSource<string> navigated)
{
    while (!navigating.IsCancellationRequested)
    {
        await BrowserLoop.NextFrameAsync().ConfigureAwait(false);
        if (BrowserNavigation.TakePendingLink() is { } next)
        {
            navigated.TrySetResult(next);
            await navigating.CancelAsync().ConfigureAwait(false);
            return;
        }
    }
}

/// <summary>Waits for the address bar.</summary>
static async Task<string> WaitForLinkAsync()
{
    while (true)
    {
        await BrowserLoop.NextFrameAsync().ConfigureAwait(false);
        if (BrowserNavigation.TakePendingLink() is { } link) return link;
    }
}
