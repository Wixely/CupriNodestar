using Constellation;
using CupriNet.Rites;
using CupriNet.Nodestar;
using CupriNet.Nodestar.Client.CupriFace;
using CupriNet.Nodestar.WebRtc;

// Constellation — a Nodestar that serves a website whose content is the node's own view of the network.
//
// Self-demonstrating on purpose: run two of these, seed one from the other's link, and each appears in the other's
// graph — arriving over the very connection being visualised. That is why it needs no invented domain model and no
// synthetic data.
var builder = NodestarApplication.CreateBuilder(args);

builder.Node.Concordium = builder.Configuration["Concordium"] ?? "constellation-demo";

// What the node reports about itself. Created before the site so the handler below can count through it.
var telemetry = new NodeTelemetry();

// The page: ordinary HTML and CSS served over L2 through the Oracle rite. This project references no CupriFace and
// ships no compiled behaviour — an author writes a website, and the client is what renders it.
//
// Wrapped rather than passed straight to ServeStaticFiles, so every page served over L2 is counted and the figure
// on the page moves when you navigate. The wrapper delegates to the same upstream handler ServeStaticFiles would
// have used — including its 192 KiB refusal, which is the Oracle's ceiling and not this sample's rule.
var files = new StaticFileOracleHandler(
    builder.Configuration["SiteRoot"] ?? Path.Combine(AppContext.BaseDirectory, "site"));

builder.Site.Serve(async (request, cancellationToken) =>
{
    telemetry.CountOracleRequest();
    return await files.HandleAsync(request, cancellationToken);
});

// Mode 1, in two deliberate halves. The transport accepts browser DataChannels and has no opinion about what runs
// in the browser; serving a client is a separate choice, made here rather than for you. CupriFace is this project's
// preference — swap the second line for ServeClient(...) and the first keeps working unchanged.
builder.UseWebRtc();
builder.ServeCupriFaceClient();

var app = builder.Build();

// Registered BEFORE the app starts, because the Shrine is handed its feeds when it is hosted. The node the feed
// reports on does not exist yet, which is why it is resolved through a delegate rather than captured.
var feed = new OverlayFeed(() => app.Node, builder.Node.Concordium, () => app.SiteAddress, telemetry);
builder.Site.Feed("overlay", feed.EmanateAsync);

Console.WriteLine();
Console.WriteLine("  Constellation");
Console.WriteLine($"  Site      http://localhost:{builder.Node.WebPort}/            (Mode 2 — server-rendered)");
Console.WriteLine($"  Feed      http://localhost:{builder.Node.WebPort}/_nodestar/feed/overlay");
Console.WriteLine($"  Node      http://localhost:{builder.Node.WebPort}/_nodestar   (link + QR)");
// Only advertised when it can actually work. A Pilgrim pins the site's Signet, so a node that does not stamp one
// into its link cannot be visited over Mode 1 at all — the client dials, completes the handshake and then fails with
// "that node advertises no site in its link". Printing the URL regardless sends people to a dead end and makes the
// client look broken, which is the opposite of what a demo should do.
if (builder.Node.AdvertiseSiteInLink)
    Console.WriteLine($"  Client    http://localhost:{builder.Node.WebPort}/_nodestar/app (Mode 1 — WASM over WebRTC)");
else
    Console.WriteLine("  Client    unavailable — pass --AdvertiseSiteInLink true to allow Mode 1 visits.");

Console.WriteLine();

await app.RunAsync();
