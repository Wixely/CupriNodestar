using System.Net;
using System.Net.Sockets;
using System.Text;
using CupriNet.Nodestar;
using CupriNet.Nodestar.Client.CupriFace;
using CupriNet.Nodestar.WebRtc;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CupriNet.Nodestar.BrowserTests;

/// <summary>
/// A real Nodestar, in-process: WebRTC endpoint, reference client, a site, and a feed whose updates are driven by
/// the test rather than by chance.
///
/// <para>In-process rather than a child process because the interesting assertions are about a specific
/// configuration — a site that advertises its Signet, a feed that emits on command — and shelling out would mean
/// reproducing all of that through command-line flags and then guessing when it was ready.</para>
///
/// <para><b>The site names its own feed.</b> This fixture once had to call its feed "overlay", because that was the
/// one name the client attended — discovered here by naming it something else and receiving nothing. A client that
/// renders whatever site it is pointed at should not know any site's feed names, so a page now declares its own
/// through <c>&lt;meta name="cupri-feed"&gt;</c>. The feed below is called "gate" precisely so that the declaration
/// is load-bearing: a client that ignored it would attend "overlay", receive nothing, and fail every feed
/// assertion.</para>
/// </summary>
public sealed class NodestarUnderTest : IAsyncLifetime
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "nodestar-browsertest-" + Guid.NewGuid().ToString("N")[..8]);

    private NodestarApplication? _app;
    private Task? _running;

    /// <summary>Set by the test to change what the feed publishes; the next update carries it.</summary>
    public volatile string FeedValue = "first";

    /// <summary>Raised by the test to force an update out, so liveness is asserted rather than waited for.</summary>
    private readonly SemaphoreSlim _publish = new(0);

    public int WebPort { get; private set; }

    public string AppUrl => $"http://localhost:{WebPort}/_nodestar/app";

    public void PublishUpdate() => _publish.Release();

    /// <summary>
    /// Everything the node logged.
    ///
    /// <para>Captured because the interesting failures are on the node side while the symptom is on the client side:
    /// "the browser never connected" is usually the node explaining, several seconds earlier, exactly why it could
    /// not offer a WebRTC endpoint. Without this the test reports the symptom and hides the cause.</para>
    /// </summary>
    public IReadOnlyList<string> NodeLog
    {
        get { lock (_nodeLog) return _nodeLog.ToArray(); }
    }

    private readonly List<string> _nodeLog = [];

    public async Task InitializeAsync()
    {
        WebPort = FreePort();

        var builder = NodestarApplication.CreateBuilder([]);
        builder.LoggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new CapturingLoggerProvider(line => { lock (_nodeLog) _nodeLog.Add(line); })));
        builder.Node.Concordium = "browsertest";
        builder.Node.Moniker = "gate";
        builder.Node.DataDirectory = _dataDirectory;
        builder.Node.WebPort = WebPort;
        builder.Node.ListenPort = FreePort();
        builder.Node.EnablePortMapping = false;
        builder.Node.EnableLanDiscovery = false;

        // Mode 1 requires the Signet in the link: a Pilgrim pins it, so a node without one cannot be visited at all.
        builder.Node.AdvertiseSiteInLink = true;

        // A document, not a page plus assets: one Oracle consult. The {{ }} binding is what the feed drives, and it
        // is deliberately a value the test can recognise in rendered pixels' worth of DOM.
        // TWO pages, switched on the path, because one page cannot show that navigation happened. The handler
        // ignored the request entirely before, which was fine while the client could only ask for /index.html.
        builder.Site.Serve(request => request.Path == "/second.html"
            ? CupriNet.Rites.OracleResponse.Ok(
                Encoding.UTF8.GetBytes("""
                    <html><head>
                    <!-- The SAME feed as the first page, so arriving here does not end the visit. A page that
                         declares none attends the default name, which this node does not serve, and the attend
                         ends the visit a moment later — the second page would flash past and the client would
                         reconnect to the first. -->
                    <meta name="cupri-feed" content="gate">
                    <meta name="cupri-design" content="800x600">
                    <style>
                      body { font:16px sans-serif; }
                      h1 { color:#204; }
                      /* The video lives on THIS page rather than the first one because the first is already full:
                         it declares an 800x600 design box, its content reaches the bottom of it, and the comments
                         there record that anything past the box is off-canvas and cannot be found by a sweep.
                         Adding a video would have pushed a target out of reach or shrunk every one of them.

                         It earns its place here anyway — reaching this page means a navigation, so the video is
                         opened on a document the engine built AFTER the client re-Inited, which is the case a
                         video that only ever worked on the first page would hide. */
                      cupri-video { display:block; width:320px; height:180px; margin-top:16px; }
                      /* Sized in CSS rather than left to the file's own 48x48, so the gate is looking for a
                         block of colour big enough to survive whatever zoom this page is fitted at. */
                      cupri-image { display:block; width:120px; height:120px; }
                    </style>
                    </head><body>
                      <h1>arrived elsewhere</h1>
                      <!-- Inline, as a data: URI, because THE CLIENT CANNOT FETCH A SUB-RESOURCE AT ALL: it
                           receives one document over the conduit and nothing else, so a relative src resolves to
                           nothing and never opens. Measured — a missing local source produces no VideoOpen and
                           the engine simply retries with a fresh id. Inline and absolute-remote are the two forms
                           that work, and only inline is self-contained enough to gate on.

                           Two seconds of flat red at 64x48, which is the smallest thing ffmpeg would produce that
                           a browser will still decode. Nothing about the test depends on what it shows. -->
                      <!-- MAGENTA, which appears nowhere else in this fixture's palette — so finding that colour
                           on the canvas is finding this image and cannot be finding anything else. The element is
                           <cupri-image>; a plain <img> renders nothing whatever its source, which cost an hour of
                           believing images were unsupported.

                           Inline for the same reason the video is: this client fetches one document over the
                           conduit and has no sub-resource path, and wasm has no filesystem behind a relative
                           path either. Inline is the only form a site on this network can rely on. -->
                      <cupri-image src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAADAAAAAwCAIAAADYYG7QAAAACXBIWXMAAAABAAAAAQBPJcTWAAAAQElEQVR4nO3OQQ0AIAwAsfn3DJkLjkeTCujcOV+ZfCAkJCRUD4SEhITqgZCQkFA9EBISEqoHQkJCQvVASEjosQWfdtQAAxvsSgAAAABJRU5ErkJggg=="></cupri-image>
                      <cupri-video controls muted src="data:video/webm;base64,GkXfowEAAAAAAAAfQoaBAUL3gQFC8oEEQvOBCEKChHdlYm1Ch4ECQoWBAhhTgGcBAAAAAAAEaxFNm3RAO027i1OrhBVJqWZTrIHlTbuMU6uEFlSua1OsggEjTbuMU6uEElTDZ1OsggFqTbuMU6uEHFO7a1OsggRO7AEAAAAAAACbAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmAQAAAAAAADIq17GDD0JATYCNTGF2ZjU4LjE3LjEwMVdBjUxhdmY1OC4xNy4xMDFEiYhAn0AAAAAAABZUrmsBAAAAAAAAO64BAAAAAAAAMteBAXPFgQGcgQAitZyDdW5khoVWX1ZQOIOBASPjg4QF9eEA4AEAAAAAAAAGsIFAuoEwElTDZwEAAAAAAAC/c3MBAAAAAAAALmPAAQAAAAAAAABnyAEAAAAAAAAaRaOHRU5DT0RFUkSHjUxhdmY1OC4xNy4xMDFzcwEAAAAAAAA5Y8ABAAAAAAAABGPFgQFnyAEAAAAAAAAhRaOHRU5DT0RFUkSHlExhdmM1OC4yMS4xMDQgbGlidnB4c3MBAAAAAAAAOmPAAQAAAAAAAARjxYEBZ8gBAAAAAAAAIkWjiERVUkFUSU9ORIeUMDA6MDA6MDIuMDAwMDAwMDAwAAAfQ7Z1AQAAAAAAAg3ngQCjwoEAAICQAwCdASpAADAAAEcIhYWIhYSIAgICdaoD+AP6Agc7eeLPMx5eAP79bvP/45k3MMT/jm3/8WE8DijI//FRAKOWgQBkANEBAAEQEAAYABhYL/QACIwAAKOWgQDIANEBAAEQEAAYABhYL/QACIwAAKOWgQEsANEBAAEQEAAYABhYL/QACIwAAKOWgQGQANEBAAEQEAAYABhYL/QACIwAAKOWgQH0ANEBAAEQEAAYABhYL/QACIwAAKOWgQJYANEBAAEQEAAYABhYL/QACIwAAKOVgQK8ALEBAAEQEBRgAGFgv9AAIjAAo5aBAyAA0QEAARAQABgAGFgv9AAIjAAAo5aBA4QA0QEAARAQABgAGFgv9AAIjAAAo5aBA+gA0QEAARAQABgAGFgv9AAIjAAAo5aBBEwA0QEAARAQABgAGFgv9AAIjAAAo5aBBLAA0QEAARAQABgAGFgv9AAIjAAAo5aBBRQA0QEAARAQABgAGFgv9AAIjAAAo5aBBXgA0QEAARAQABgAGFgv9AAIjAAAo5aBBdwA0QEAARAQABgAGFgv9AAIjAAAo5aBBkAA0QEAARAQABgAGFgv9AAIjAAAo5aBBqQA0QEAARAQABgAGFgv9AAIjAAAo5WBBwgAsQEAARAQFGAAYWC/0AAiMACjloEHbADRAQABEBAAGAAYWC/0AAiMAAAcU7trAQAAAAAAABG7j7OBALeK94EB8YICNfCBAw=="></cupri-video>
                    </body></html>
                    """),
                "text/html; charset=utf-8")
            : CupriNet.Rites.OracleResponse.Ok(
            Encoding.UTF8.GetBytes("""
                <html><head>
                <!-- The feed is NOT called "overlay", deliberately. The client used to attend that name and nothing
                     else, so a fixture that used it could not tell a client reading this declaration from one
                     ignoring it. Every feed assertion below now depends on the declaration being honoured. -->
                <meta name="cupri-feed" content="gate">
                <!-- A design size that is NOT the client's default, so the declaration is doing work. It also keeps
                     the painter and the hit test honest with each other: both derive from the same zoom, and if only
                     one of them read this the pointer test would start missing what it can plainly see. -->
                <meta name="cupri-design" content="800x600">
                <style>
                  /* NO body background, deliberately. A page that asks for nothing is the case the host's canvas
                     clear exists for — and a document that paints its own background hides that bug completely,
                     which an earlier version of this fixture did. */
                  body { margin:0; color:#101014; font: 16px sans-serif; padding: 20px; }
                  h1 { color:#2244cc; font-size: 28px; }
                  .value { font-size: 22px; font-weight: 700; }
                  /* cursor declared rather than inferred from the anchor: the gate is testing that a pointer
                     POSITION reaches the document and resolves a style there, not what the engine decides an <a>
                     ought to feel like. A real site would declare it too. */
                  a { color:#2244cc; cursor:pointer; }
                  /* Deliberately TALL. The gate sweeps the canvas rather than aiming, because where a thing lands
                     depends on the hybrid zoom the page was fitted at — and at the zoom a 60vh canvas produces, the
                     grid steps about 48 logical pixels at a time. A one-line link is ~19px tall and the sweep walks
                     straight over it, which looks exactly like input not arriving. */
                  .hand { cursor:pointer; background:#dde4ee; padding:70px 20px; margin-top:12px; }
                  /* A hover that actually CHANGES something. Every other rule here only declares a cursor, which
                     the page reports without repainting a pixel — so nothing on this canvas could produce a small
                     damage rectangle at all. It stays because a site whose hovers are inert is not a realistic
                     one, and because it is what `Every_repaint_currently_uploads_the_whole_surface` needs in order
                     to mean anything the day the engine starts narrowing damage under scale. */
                  .hand:hover { background:#b9c8de; }
                  /* Tall enough for the sweep to land on, short enough to leave the page's other targets
                     inside the 800x600 design box — anything past it is not on the canvas to be found. */
                  .inside { display:block; background:#cfe6cf; padding:26px 20px; }
                  /* Tall for the same reason .hand is: the gate sweeps rather than aims, and at the zoom this
                     page is fitted at a 44px field is thinner than the grid's step — the sweep walked over it and
                     the failure read as "no click focused a text field" rather than as "the test missed". */
                  .typing { display:block; width:60%; height:90px; margin-top:10px; background:#fff;
                            border:1px solid #99a; }
                  /* Something to scroll. Hybrid zoom scales a tall PAGE down to fit rather than clipping it, so a
                     long body would never scroll — an explicitly scrollable box is what the wheel can actually move,
                     and the colour bands make the movement visible in pixels rather than only in the engine. */
                  #scroller { height:120px; overflow:auto; border:1px solid #99a; }
                  #scroller div { height:90px; }
                </style></head>
                <body>
                  <h1>browser gate</h1>
                  <!-- A link WITHIN the site. A big target for the same reason .hand is one: the gate sweeps the
                       canvas, and a single line of text is easy to step over. Its LABEL deliberately shares no
                       words with the second page's heading — a marker that also appears on the page you start
                       from matches before anything is clicked, which is how the first version of this test passed
                       while proving nothing. -->
                  <a class="inside" href="/second.html">onwards</a>
                  <!-- Somewhere to type. Until CupriFace 0.12.0 a plain L2 document had nowhere to put a keystroke
                       at all — an <input> in ordinary markup was not focusable and DispatchKey answered false for
                       everything — so the client's key path existed and could not be exercised. A cupri-textfield
                       takes focus from a click and reports a caret, which is what an input method attaches to. -->
                  <cupri-textfield class="typing" value="{{ typed }}" placeholder="type here"></cupri-textfield>
                  <p class="value">{{ value }}</p>
                  <p><a href="cuprinet://intone/nowhere">a link to nowhere</a></p>
                  <div class="hand">a region that asks for a pointer cursor</div>
                  <div id="scroller">
                    <div style="background:#e33">one</div>
                    <div style="background:#3e3">two</div>
                    <div style="background:#33e">three</div>
                    <div style="background:#ee3">four</div>
                    <div style="background:#e3e">five</div>
                  </div>
                </body></html>
                """),
            "text/html; charset=utf-8"));

        // Named to match the page's own declaration rather than the old hard-coded default, which is what makes the
        // feed tests evidence that the declaration is read.
        builder.Site.Feed("gate", async (publisher, ct) =>
        {
            await publisher.SnapshotAsync(Payload(), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                await _publish.WaitAsync(ct).ConfigureAwait(false);
                await publisher.UpdateAsync(Payload(), ct).ConfigureAwait(false);
            }
        });

        builder.UseWebRtc();
        builder.ServeCupriFaceClient();

        _app = builder.Build();
        _running = _app.RunAsync(_stopping.Token);

        await WaitForWebFrontAsync().ConfigureAwait(false);
        await AssertClientIsStagedAsync().ConfigureAwait(false);
    }

    private byte[] Payload() =>
        Encoding.UTF8.GetBytes($$"""{"value":"{{FeedValue}}"}""");

    private async Task WaitForWebFrontAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await http.GetAsync($"http://localhost:{WebPort}/healthz").ConfigureAwait(false)).IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { /* not up yet */ }
            catch (TaskCanceledException) { /* not up yet */ }

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw new InvalidOperationException("the web front never came up");
    }

    /// <summary>
    /// Fails early and clearly when the wasm bundle has not been staged.
    ///
    /// <para>Without this the browser would load a 404, sit there, and every assertion would time out — reported as
    /// "the client never connected", which sends you looking at WebRTC instead of at a missing build step.</para>
    /// </summary>
    private async Task AssertClientIsStagedAsync()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"{AppUrl}/dotnet.native.wasm").ConfigureAwait(false);

        Assert.True(response.IsSuccessStatusCode,
            "the browser client is not staged — run the \"client: publish\" and \"client: stage into the CupriFace "
            + "client package\" tasks (or `dotnet publish clients/web` then copy its output into "
            + $"src/CupriNet.Nodestar.Client.CupriFace/client/). Got {(int)response.StatusCode} for the wasm module.");

        Assert.Equal("application/wasm", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// A port free for <b>both</b> TCP and UDP.
    ///
    /// <para>Asking a <c>TcpListener</c> for port 0 is the usual trick and it is not enough here: a node's
    /// <c>ListenPort</c> carries the TCP overlay listener <i>and</i> the UDP WebRTC endpoint. Windows reserves
    /// ranges for Hyper-V and WSL where a TCP bind succeeds and the matching UDP bind is refused — which surfaced as
    /// "this node's link carries no WebRTC endpoint" and sent us looking at the client instead of at the port.</para>
    /// </summary>
    private static int FreePort()
    {
        // Deliberately BELOW the ephemeral range, which on Windows starts at 49152 and is riddled with UDP exclusion
        // ranges reserved by Hyper-V and WSL. Asking TcpListener for port 0 draws from exactly that range, so it
        // reliably returns ports where the TCP bind succeeds and the UDP one is refused — which is not a port at all
        // for something that needs both.
        var next = Random.Shared.Next(20_000, 40_000);

        for (var attempt = 0; attempt < 500; attempt++)
        {
            var candidate = 20_000 + ((next + attempt) % 20_000);
            if (TryReserve(candidate)) return candidate;
        }

        throw new InvalidOperationException("could not find a port free for both TCP and UDP");
    }

    /// <summary>Both binds, or neither: a node's ListenPort carries the TCP overlay listener and the UDP WebRTC endpoint.</summary>
    private static bool TryReserve(int port)
    {
        try
        {
            using var tcp = new TcpListener(IPAddress.Loopback, port);
            tcp.Start();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_running is not null)
        {
            try { await _running.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* the normal way to stop */ }
        }

        if (_app is not null) await _app.DisposeAsync().ConfigureAwait(false);

        _stopping.Dispose();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
