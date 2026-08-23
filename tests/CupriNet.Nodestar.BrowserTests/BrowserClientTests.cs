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
        // 1. The module runs at all. A stripped export or a blocked event loop dies before this line.
        await ExpectLogAsync("client starting");

        // 2. WebRTC, dialled from the signed link inlined beside the bundle — no signalling server involved.
        await ExpectLogAsync("datachannel open");

        // 3. Toll + Noise pinning the Signet: only the holder of the site's key could have answered.
        await ExpectLogAsync("pilgrimage complete");

        // 4. The site itself, over L2.
        await ExpectLogAsync("site answered 200");

        // 5. Pixels. This is the assertion the others exist to reach — steps 1-4 have all passed before while the
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

    public async Task DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}
