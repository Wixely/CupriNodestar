using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
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
BrowserLoop.Run(RunClientAsync);
return;

static async Task RunClientAsync()
{
    var suite = new BouncyCastleSuite();

    // The seed: the node inlined its own signed link when it served this page, which is what removes the
    // signalling server — the remote description is already here before a single packet is sent.
    var seed = BrowserDataChannel.Seed();
    if (!IntonationUri.TryParse(seed, out var intonation, out var reason))
    {
        Console.WriteLine($"[cupri] the seeded link is unusable: {reason}");
        return;
    }

    // A Pilgrim pins the Shrine's Signet. Without one in the link there is nothing to visit — a connection with
    // nothing to ask for.
    if (intonation.Shrine is not { } signet)
    {
        Console.WriteLine("[cupri] this node does not advertise a site in its link (AdvertiseSiteInLink is off).");
        return;
    }

    Console.WriteLine($"[cupri] dialling {intonation.Network} …");
    await using var channel = await BrowserDataChannel.ConnectAsync(intonation, CancellationToken.None);
    Console.WriteLine("[cupri] datachannel open");

    // From here down, nothing browser-specific remains: the DataChannel becomes a Vessel and the same protocol
    // code the node runs takes over — which is the entire reason to compile C# to wasm rather than reimplement it.
    var vessel = new DataChannelVessel(channel);
    await using var shrine = await BrowserPilgrim.PilgrimageAsync(
        vessel, signet, intonation.Network, suite, CancellationToken.None);
    Console.WriteLine("[cupri] pilgrimage complete — the Signet answered");

    // ONE consult for the whole document. An Oracle response is a single message over a channel where every fetch
    // costs a full round trip, so a site that links its stylesheet pays twice for one page — and CupriFace reads
    // <style> elements out of the DOM anyway, so a self-contained document is both cheaper and the natural shape.
    var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"));
    Console.WriteLine($"[cupri] site answered {page.Status} ({page.Body.Length} bytes, {page.ContentType})");

    BrowserRenderer.Show(page.AsText());
    Console.WriteLine("[cupri] painted");

    // Live data over the same session, on its own stream — the page fetch and the feed share one Pilgrimage.
    await foreach (var frame in shrine.AttendAsync("overlay"))
        Console.WriteLine($"[cupri] feed {frame.Kind} ({frame.Payload.Length} bytes)");

    Console.WriteLine("[cupri] feed ended");
}
