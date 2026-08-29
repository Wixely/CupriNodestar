using System.Diagnostics;
using Microsoft.Playwright;
using Xunit;

namespace CupriNet.Nodestar.BrowserTests;

/// <summary>
/// Mode 1, end to end, in a real browser: the served client dials the node that served it, completes a Pilgrimage,
/// fetches the site over L2, renders it, and repaints when the feed updates.
///
/// <para>Every assertion here corresponds to a failure that has actually happened during development and produced
/// <b>no error of any kind</b> — a page that loads and shows nothing. That is why the test insists on pixels and on
/// specific log lines rather than on "it didn't throw".</para>
/// </summary>
public sealed class BrowserClientTests : IClassFixture<NodestarUnderTest>, IAsyncLifetime
{
    /// <summary>Generous: a cold wasm module is several megabytes and a Noise handshake follows it.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private readonly NodestarUnderTest _node;
    private readonly List<string> _log = [];
    private readonly List<string> _pageErrors = [];

    /// <summary>Names the diagnostics files. Set by each test so a CI artifact says which one produced it.</summary>
    private string _diagnosticsName = "browser";

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public BrowserClientTests(NodestarUnderTest node) => _node = node;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();

        var options = new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--no-sandbox", "--disable-gpu"],
        };

        // CI installs the browser Playwright expects. A developer machine often already has a different build, so an
        // override avoids a second multi-hundred-megabyte download just to run this once.
        if (Environment.GetEnvironmentVariable("CUPRI_CHROMIUM") is { Length: > 0 } chromium)
            options.ExecutablePath = chromium;

        _browser = await _playwright.Chromium.LaunchAsync(options);
        _page = await _browser.NewPageAsync();

        // The client reports progress to stdout, which surfaces here. It is the only view into a chain that is
        // otherwise entirely inside a wasm module.
        _page.Console += (_, m) => { lock (_log) _log.Add(m.Text); };
        _page.PageError += (_, e) => { lock (_pageErrors) _pageErrors.Add(e); };
        _page.Crash += (_, _) => { lock (_pageErrors) _pageErrors.Add("the renderer process crashed"); };

        await _page.GotoAsync(_node.AppUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });
    }

    [Fact]
    public async Task The_client_dials_the_node_that_served_it_and_renders_its_site()
    {
        _diagnosticsName = "renders-its-site";

        // 1. The module runs at all. A stripped export or a blocked event loop dies before this line.
        await ExpectLogAsync("client starting");

        // 2. WebRTC, dialled from the signed link inlined beside the bundle — no signalling server involved.
        await ExpectLogAsync("datachannel open");

        // 3. Toll + Noise pinning the Signet: only the holder of the site's key could have answered.
        await ExpectLogAsync("pilgrimage complete");

        // 4. The site itself, over L2.
        await ExpectLogAsync("site answered 200");

        // 5. The paint itself, which is NOT a consequence of step 4 and must not be assumed to follow it. A
        // Document-tier page is a template: the client deliberately holds its first paint until the feed binds the
        // {{ }} placeholders, so that a visitor never sees the raw template. Sampling the canvas straight after
        // "site answered 200" therefore races the feed — it passed on a warm machine and failed on a cold CI runner,
        // reporting 0/552960 opaque pixels for a client that was working perfectly and simply had not painted yet.
        await ExpectLogAsync("painted");

        // 6. Pixels. This is the assertion the others exist to reach — steps 1-5 have all passed before while the
        // canvas stayed blank, because fonts were stripped or the background was never cleared.
        var painted = await CanvasAsync();
        Assert.True(painted.Opaque > painted.Total / 2,
            $"the canvas is mostly transparent ({painted.Opaque}/{painted.Total} opaque) — the host is not clearing "
            + "the background, or the document never laid out");
        Assert.True(painted.Distinct > 4,
            $"the canvas has only {painted.Distinct} distinct colours — a flat fill means text did not render, which "
            + "is what a missing embedded font looks like");

        Assert.Empty(_pageErrors);
    }

    [Fact]
    public async Task A_feed_update_repaints_the_page()
    {
        _diagnosticsName = "feed-repaints";

        // The Document tier has no JavaScript engine, so nothing in the page can react to anything: keeping the view
        // current is the client's job. If binding is trimmed away this still logs a Snapshot and renders nothing.
        await ExpectLogAsync("feed Snapshot");

        var before = await CanvasAsync();

        _node.FeedValue = "SECOND-VALUE";

        // Published on every poll rather than once. Each visit runs its own source, and a previous test's source can
        // still be draining the same signal as it winds down — so a single release is not guaranteed to reach the
        // attendance this test is watching. Repeating costs nothing and makes the assertion about whether an update
        // can reach the renderer, not about which source happened to wake up.
        await ExpectLogAsync("feed Update", beforeEachPoll: _node.PublishUpdate);

        // The repaint must change the picture. Asserting on the log alone would pass even if Update never reached
        // the renderer — which is exactly what a trimmed binder does.
        var after = await WaitForCanvasChangeAsync(before);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);

        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// The pointer reaching the document, observed through the cursor.
    ///
    /// <para>The cursor is the cleanest evidence available. A canvas has no DOM for the site, so the browser cannot
    /// tell anyone what is under the mouse — the only thing that can is the client, by hit-testing the document and
    /// saying so. If the pointer never arrives, no position on the canvas produces a pointer cursor, and this fails
    /// wherever it looks.</para>
    ///
    /// <para>Swept rather than aimed. Where the link lands in canvas pixels depends on the layout, the device pixel
    /// ratio and the hybrid zoom the page was fitted at; computing that here would be reimplementing the renderer's
    /// arithmetic in the test, and getting it subtly wrong would look exactly like the feature being broken.</para>
    /// </summary>
    [Fact]
    public async Task The_pointer_reaches_the_document()
    {
        _diagnosticsName = "pointer-reaches-document";
        await ExpectLogAsync("painted");

        var box = await ViewportAsync();
        string cursor = string.Empty;

        for (var row = 0; row < 16 && cursor != "pointer"; row++)
        {
            for (var col = 0; col < 16 && cursor != "pointer"; col++)
            {
                await _page!.Mouse.MoveAsync(
                    (float)(box.X + box.Width * (col + 0.5) / 16),
                    (float)(box.Y + box.Height * (row + 0.5) / 16));

                // A frame for the pump to drain the queue and hit-test.
                await Task.Delay(35);
                cursor = await _page.EvalOnSelectorAsync<string>("#viewport", "e => e.style.cursor || ''");
            }
        }

        if (cursor != "pointer") Assert.Fail(Diagnosis($"the cursor stayed '{cursor}' across the whole canvas"));
        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// The wheel reaching the document, observed in pixels.
    ///
    /// <para>Asserting on the canvas rather than on any engine state, for the same reason the feed test does: the
    /// document could scroll internally and still paint nothing, which is indistinguishable from the wheel never
    /// arriving as far as a visitor is concerned. The fixture's page carries an explicitly scrollable box of colour
    /// bands so that scrolling it has to change the picture.</para>
    ///
    /// <para>The feed is quiet unless asked, so nothing else is repainting while this runs — a changed canvas here
    /// means the wheel did it.</para>
    /// </summary>
    [Fact]
    public async Task The_wheel_scrolls_the_document()
    {
        _diagnosticsName = "wheel-scrolls-document";
        await ExpectLogAsync("painted");

        var before = await CanvasAsync();
        var box = await ViewportAsync();

        // Swept for the same reason as the cursor: the scrollable box's position on the canvas is the renderer's
        // arithmetic, not the test's. Each column is tried from the top down, wheeling several notches so a short
        // scroll still moves a whole colour band.
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 8; col++)
            {
                await _page!.Mouse.MoveAsync(
                    (float)(box.X + box.Width * (col + 0.5) / 8),
                    (float)(box.Y + box.Height * (row + 0.5) / 8));

                for (var notch = 0; notch < 4; notch++)
                {
                    await _page.Mouse.WheelAsync(0, 120);
                    await Task.Delay(30);
                }

                var now = await CanvasAsync();
                if (now.Fingerprint != before.Fingerprint)
                {
                    Assert.Empty(_pageErrors);
                    return;
                }
            }
        }

        Assert.Fail("the wheel never changed the canvas — either the event is not reaching the document, or the "
                    + "scrollable region did not move. The page's #scroller is the thing that should have scrolled.");
    }

    /// <summary>The canvas's position on the page, which is where synthetic input has to be aimed.</summary>
    private async Task<Microsoft.Playwright.LocatorBoundingBoxResult> ViewportAsync()
    {
        var box = await _page!.Locator("#viewport").BoundingBoxAsync();
        Assert.NotNull(box);
        return box!;
    }

    /// <summary>Waits for a line, then fails with the whole client log — which is the useful thing to read.</summary>
    private async Task ExpectLogAsync(string fragment, Action? beforeEachPoll = null)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            beforeEachPoll?.Invoke();

            lock (_log)
            {
                if (_log.Any(l => l.Contains(fragment, StringComparison.Ordinal))) return;
                if (_pageErrors.Count > 0)
                    Assert.Fail(Diagnosis(
                        $"the page failed before '{fragment}': {string.Join("; ", _pageErrors)}"));
            }

            await Task.Delay(250);
        }

        Assert.Fail(Diagnosis($"timed out waiting for '{fragment}'"));
    }

    /// <summary>
    /// Both sides of the story. The client log says what the browser saw; the node log usually says why — a WebRTC
    /// endpoint that could not bind is reported by the node seconds before the client reports not connecting.
    /// </summary>
    private string Diagnosis(string headline)
    {
        const string Indent = "\n  ";
        lock (_log)
        {
            var client = _log.Count > 0 ? string.Join(Indent, _log) : "(nothing — the module never ran)";
            var node = _node.NodeLog.Count > 0 ? string.Join(Indent, _node.NodeLog) : "(nothing)";
            return $"{headline}\n\nclient log:{Indent}{client}\n\nnode log:{Indent}{node}";
        }
    }

    private async Task<CanvasState> WaitForCanvasChangeAsync(CanvasState before)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var now = await CanvasAsync();
            if (now.Fingerprint != before.Fingerprint) return now;
            await Task.Delay(250);
        }

        Assert.Fail("the feed updated but the canvas never changed — the binder resolved nothing, or the repaint "
                    + "is not wired to the update");
        throw new UnreachableException();
    }

    /// <summary>Reads the canvas back: how much is painted, how varied, and a cheap fingerprint for change detection.</summary>
    private async Task<CanvasState> CanvasAsync()
    {
        var raw = await _page!.EvaluateAsync<string>("""
            () => {
              const c = document.getElementById('viewport');
              if (!c) return '0|0|0|none';
              const d = c.getContext('2d').getImageData(0, 0, c.width, c.height).data;
              const seen = new Set();
              let opaque = 0, hash = 0;
              for (let i = 0; i < d.length; i += 4) {
                if (d[i + 3] !== 0) opaque++;
                if (seen.size < 64) seen.add(d[i] + ',' + d[i + 1] + ',' + d[i + 2]);
                hash = (hash * 31 + d[i] + d[i + 1] * 3 + d[i + 2] * 7) | 0;
              }
              return [d.length / 4, opaque, seen.size, hash].join('|');
            }
            """);

        var parts = raw.Split('|');
        return new CanvasState(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), parts[3]);
    }

    private readonly record struct CanvasState(int Total, int Opaque, int Distinct, string Fingerprint);

    /// <summary>
    /// Writes everything needed to diagnose this run from somewhere else.
    ///
    /// <para>The whole chain lives inside a wasm module in a headless browser, so a failure two machines away is
    /// otherwise a bare assertion message. A screenshot plus both logs is the difference between "the gate failed"
    /// and knowing whether the module started, whether the handshake completed, and what the page actually looked
    /// like when it gave up. CI uploads this directory whatever the outcome.</para>
    /// </summary>
    private async Task CaptureDiagnosticsAsync()
    {
        var directory = Environment.GetEnvironmentVariable("CUPRI_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(directory) || _page is null) return;

        var name = _diagnosticsName;
        Directory.CreateDirectory(directory);

        try
        {
            await _page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(directory, $"{name}.png"),
                FullPage = true,
            });

            var canvas = await CanvasAsync();
            await File.WriteAllTextAsync(Path.Combine(directory, $"{name}.log"), Diagnosis(
                $"canvas: {canvas.Total} px, {canvas.Opaque} opaque, {canvas.Distinct} distinct colours"));
        }
        catch (Exception ex)
        {
            // A page that already died cannot be screenshotted; say so rather than losing the logs with it.
            await File.WriteAllTextAsync(Path.Combine(directory, $"{name}.log"),
                Diagnosis($"could not capture the page ({ex.GetType().Name}: {ex.Message.Split('\n')[0]})"));
        }
    }

    public async Task DisposeAsync()
    {
        await CaptureDiagnosticsAsync();
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
