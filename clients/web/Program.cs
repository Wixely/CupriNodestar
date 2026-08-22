using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Nodestar.Client;
using CupriNet.Rites;
using CupriNet.Vessel;

// The served on-ramp client.
//
// The page you are reading this from is a CLEARNET asset: an ordinary file from an ordinary HTTP server, with no
// overlay address. Everything below happens over WebRTC, and the site it fetches is the thing that is actually
// addressed on the network.

Console.WriteLine("[cupri] client starting");

// Fire and return. The rAF pump in the host page drives everything from here; awaiting in Main would block
// the browser's only thread and kill the tab.
BrowserLoop.Run(RunClientAsync);
return;

async Task RunClientAsync()
{

var suite = new BouncyCastleSuite();

// The seed. The node inlined its own signed link when it served this page, which is what removes the signalling
// server: the remote description is already here, before a single packet is sent.
var seed = BrowserDataChannel.Seed();
if (!IntonationUri.TryParse(seed, out var intonation, out var reason))
{
    Console.WriteLine($"[cupri] the seeded link is unusable: {reason}");
    return;
}

Console.WriteLine($"[cupri] dialling {intonation.Network} …");

Console.WriteLine("[cupri] stage: seed parsed, dialling");

await using var channel = await BrowserDataChannel.ConnectAsync(intonation, CancellationToken.None);
Console.WriteLine("[cupri] stage: datachannel OPEN");

// From here down there is nothing browser-specific left. The DataChannel becomes a Vessel and the real stack runs:
// Toll, Noise, Pilgrimage, then the site rites — the same code the node executes, which is the whole reason to
// compile C# to wasm rather than reimplement the protocol in JavaScript.
// Isolated deliberately: a CupriNode normally binds a TCP listener, and a browser has no sockets. If this is what
// kills the tab then the Pilgrim path needs a listener-free construction, which is a finding worth having early.
Console.WriteLine("[cupri] stage: creating node (binds sockets on a server � the suspect in a browser)");
CupriNode node;
try
{
    node = await CupriNode.CreateAsync(new CupriNodeOptions
    {
        Concordium = intonation.Network.ToString(),
        Suite = suite,
    });
    Console.WriteLine("[cupri] stage: node created");
}
catch (Exception ex)
{
    Console.WriteLine($"[cupri] node creation FAILED: {ex.GetType().Name}: {ex.Message}");
    return;
}
await using var _node = node;

var vessel = new DataChannelVessel(channel);

// A Pilgrim pins the Shrine's Signet, which the node advertises in its link when it opts in. Without one there is
// nothing to visit — the browser would have a connection but no site to ask for.
if (intonation.Shrine is not { } signet)
{
    Console.WriteLine("[cupri] this node does not advertise a site in its link (AdvertiseSiteInLink is off).");
    return;
}

await using var shrine = await node.PilgrimageOverVesselAsync(vessel, signet);
Console.WriteLine("[cupri] pilgrimage complete — fetching the site");

var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"));
Console.WriteLine($"[cupri] site answered {page.Status} ({page.Body.Length} bytes, {page.ContentType})");

Render(page);

// Live data over the same session, on its own stream: the page fetch and the feed share one Pilgrimage.
_ = Task.Run(async () =>
{
    try
    {
        await foreach (var frame in shrine.AttendAsync("overlay"))
            Console.WriteLine($"[cupri] feed {frame.Kind} ({frame.Payload.Length} bytes)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[cupri] feed ended: {ex.Message}");
    }
});

// Keep the module alive; the browser event loop drives everything from here.
// Nothing to wait for: the pump keeps the module alive.
}

static void Render(OracleResponse page)
{
    // CupriFace renders the fetched markup — HTML and CSS to a canvas, with no browser engine and no JavaScript
    // engine, so a hostile site has no script runtime to reach for. Wiring the canvas is the next step; for now the
    // fetch is proven and the payload is reported.
    Console.WriteLine($"[cupri] {page.AsText().Length} characters of markup ready to render");
}
