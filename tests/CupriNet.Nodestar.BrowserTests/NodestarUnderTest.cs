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
        builder.Site.Serve(_ => CupriNet.Rites.OracleResponse.Ok(
            Encoding.UTF8.GetBytes("""
                <html><head>
                <!-- The feed is NOT called "overlay", deliberately. The client used to attend that name and nothing
                     else, so a fixture that used it could not tell a client reading this declaration from one
                     ignoring it. Every feed assertion below now depends on the declaration being honoured. -->
                <meta name="cupri-feed" content="gate">
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
                  /* Something to scroll. Hybrid zoom scales a tall PAGE down to fit rather than clipping it, so a
                     long body would never scroll — an explicitly scrollable box is what the wheel can actually move,
                     and the colour bands make the movement visible in pixels rather than only in the engine. */
                  #scroller { height:120px; overflow:auto; border:1px solid #99a; }
                  #scroller div { height:90px; }
                </style></head>
                <body>
                  <h1>browser gate</h1>
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
