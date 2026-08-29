using CupriNet.Nodestar;
using CupriNet.Nodestar.WebRtc;
//#if (tor)
using CupriNet.Nodestar.Tor;
//#endif

var builder = NodestarApplication.CreateBuilder(args);

// The network this node joins. Nodes only ever meet others using the same name, so this is the whole of who you
// share an overlay with — pick something specific rather than a default everyone else also has.
builder.Node.Concordium = "NETWORK-NAME";
builder.Node.Moniker = "NODE-MONIKER";

// The site's address survives restarts only as long as this directory does. It holds the Signet — which IS the
// cupri1… URL people link to — so treat it like a TLS private key, not like a cache.
builder.Node.DataDirectory = ".nodestar";

// Put the site's address in this node's link, so a browser that has the link can visit the site rather than merely
// reach the node. Without it there is nothing for a visitor to pin.
builder.Node.AdvertiseSiteInLink = true;

// The browser on-ramp: a served client that dials back over WebRTC with no signalling server.
builder.UseWebRtc();
//#if (tor)
builder.UseTor();
//#endif

// What this site serves. A single self-contained document is the natural shape: every fetch costs a round trip, and
// the renderer reads <style> out of the document anyway.
builder.Site.ServeStaticFiles("l2-wwwroot");

// A live feed. The page's {{ }} placeholders bind to whatever this publishes — send a snapshot first, because a
// visitor who arrives mid-stream has no state, then updates for as long as they stay.
builder.Site.Feed("site", async (publisher, cancellationToken) =>
{
    var visits = 0;
    await publisher.SnapshotAsync(Payload(++visits), cancellationToken);

    while (!cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        await publisher.UpdateAsync(Payload(++visits), cancellationToken);
    }

    static byte[] Payload(int ticks) =>
        System.Text.Encoding.UTF8.GetBytes($$"""{"ticks":{{ticks}},"greeting":"served over L2"}""");
});

// Runs once the node is up and the site is hosted, so the address it prints is a real one.
//
// The port is read back rather than written in: everything on builder.Node can be overridden by configuration —
// CUPRINET_NODESTAR_WebPort, appsettings.json, or the command line — and a message that hard-codes a number tells
// you where the site ISN'T the moment anyone does.
builder.OnStarted((app, cancellationToken) =>
{
    var port = builder.Node.WebPort;
    Console.WriteLine($"  site address : {app.SiteAddress}");
    Console.WriteLine($"  link and QR  : http://localhost:{port}/_nodestar");
    Console.WriteLine($"  the site     : http://localhost:{port}/");
    return Task.CompletedTask;
});

await builder.Build().RunAsync();
