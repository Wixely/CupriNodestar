using Constellation;
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

// The page: ordinary HTML and CSS served over L2 through the Oracle rite. This project references no CupriFace and
// ships no compiled behaviour — an author writes a website, and the client is what renders it.
builder.Site.ServeStaticFiles(
    builder.Configuration["SiteRoot"] ?? Path.Combine(AppContext.BaseDirectory, "site"));

// Mode 1, in two deliberate halves. The transport accepts browser DataChannels and has no opinion about what runs
// in the browser; serving a client is a separate choice, made here rather than for you. CupriFace is this project's
// preference — swap the second line for ServeClient(...) and the first keeps working unchanged.
builder.UseWebRtc();
builder.ServeCupriFaceClient();

var app = builder.Build();

// Registered BEFORE the app starts, because the Shrine is handed its feeds when it is hosted. The node the feed
// reports on does not exist yet, which is why it is resolved through a delegate rather than captured.
var feed = new OverlayFeed(() => app.Node, builder.Node.Concordium, () => app.SiteAddress);
builder.Site.Feed("overlay", feed.EmanateAsync);

Console.WriteLine();
Console.WriteLine("  Constellation");
Console.WriteLine($"  Site      http://localhost:{builder.Node.WebPort}/            (Mode 2 — server-rendered)");
Console.WriteLine($"  Feed      http://localhost:{builder.Node.WebPort}/_nodestar/feed/overlay");
Console.WriteLine($"  Node      http://localhost:{builder.Node.WebPort}/_nodestar   (link + QR)");
Console.WriteLine($"  Client    http://localhost:{builder.Node.WebPort}/_nodestar/app (Mode 1 � WASM over WebRTC)");
Console.WriteLine();

await app.RunAsync();
