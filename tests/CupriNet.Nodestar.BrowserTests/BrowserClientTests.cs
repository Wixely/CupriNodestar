using System.Diagnostics;
using System.Text.Json;
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
    /// The association carries a frame as large as the rites say is legal.
    ///
    /// <para>Two different numbers meet on this path and only one of them is a constant. A rite advertises 192 KiB
    /// everywhere, whatever it is running over; what a DataChannel will carry is whatever the two ends negotiated.
    /// <c>DataChannelVessel</c> emits one channel message per frame and never fragments, so nothing in between
    /// reconciles them — if the negotiated size were the smaller, a payload the rite called legal would be refused
    /// by the transport, and the ceiling a caller is told to read would be a lie on exactly this path.</para>
    ///
    /// <para>It holds here because the node offers <c>a=max-message-size:262144</c> and Chromium agrees to it. That
    /// is a property of this pairing rather than a guarantee, which is the whole of
    /// <see href="https://github.com/Wixely/CupriNodestar/issues/4">#4</see> — so it is asserted rather than
    /// assumed, and this fails the day either end negotiates lower.</para>
    /// </summary>
    [Fact]
    public async Task The_association_carries_a_full_size_rite_frame()
    {
        _diagnosticsName = "sctp-negotiated-size";
        await ExpectLogAsync("sctp negotiated max message");

        string? line;
        lock (_log) line = _log.LastOrDefault(l => l.Contains("sctp negotiated max message", StringComparison.Ordinal));
        Assert.NotNull(line);

        var digits = new string(line!.Where(char.IsDigit).ToArray());
        Assert.True(int.TryParse(digits, out var negotiated), $"could not read a size out of '{line}'");

        // 192 KiB — ConduitCodec.MaxPayloadBytes, and the same figure for the Oracle, Auspice and Relic.
        const int RiteCeiling = 196608;
        Assert.True(negotiated >= RiteCeiling,
            $"the association negotiated {negotiated} bytes but the rites advertise {RiteCeiling}. The vessel does "
            + "not fragment, so a legal frame would be refused by the transport — see #4.");

        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// The page repaints incrementally rather than uploading the whole canvas every frame.
    ///
    /// <para><b>What this asserts, exactly:</b> that the engine hands this client a damage rectangle smaller than
    /// the surface and that the client uses it. Nothing more. It catches the optimisation being silently switched
    /// off — by a regression here, or by an engine change like the one that kept it off for every scale except 1
    /// (CupriFace #99/#100) — which is otherwise invisible, because the pixels stay correct and only a phone gets
    /// warm.</para>
    ///
    /// <para><b>What it does NOT assert:</b> that the rectangle is RIGHT. A rectangle missing part of what changed
    /// narrows the upload just as well and leaves stale pixels behind it, which is the real risk in narrowing
    /// damage at all — and is why CupriFace declined to do it under scale until #100.</para>
    ///
    /// <para><b>That property IS covered, by a different test.</b> Halving the reported height leaves
    /// <c>The_client_dials_the_node_that_served_it_and_renders_its_site</c> looking at a canvas exactly half opaque
    /// (276,480 of 552,960) and it fails. Verified by mutation rather than assumed, and worth knowing because that
    /// test reads as a smoke test for rendering at all; it is also the suite's guard against a wrong blit.</para>
    ///
    /// <para>Four attempts to assert correctness inside THIS test all passed against that same deliberate bug, and
    /// the reasons are worth leaving here. Sampling after the sweep finds nothing: moving off the block repaints
    /// the same band and corrects the dirt by accident. Sampling while hovering needs a full repaint of identical
    /// content to compare against, and there is no way to force one — a feed message re-binds an unchanged value,
    /// so the engine correctly paints nothing (42 updates arrived and produced no frame), and a viewport resize did
    /// not produce one either. The invariant belongs upstream regardless, and is guarded there: their fix paints
    /// incrementally into a retained bitmap and requires the result to match a fresh full render.</para>
    ///
    [Fact]
    public async Task The_page_repaints_incrementally()
    {
        _diagnosticsName = "damage-rects";
        await ExpectLogAsync("painted");

        await _page!.EvaluateAsync(
            "() => { globalThis.__cupri.blits = { full: 0, partial: 0, pixels: 0, surface: 0 }; }");

        // Swept rather than aimed, like every other pointer test here: where the hovering block lands in canvas
        // pixels depends on the layout and the zoom the page was fitted at.
        var box = await ViewportAsync();
        for (var row = 0; row < 8; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                await _page.Mouse.MoveAsync(
                    (float)(box.X + box.Width * (col + 0.5) / 4),
                    (float)(box.Y + box.Height * (row + 0.5) / 8));
                await Task.Delay(45);
            }
        }

        var stats = await _page.EvaluateAsync<System.Text.Json.JsonElement>("() => globalThis.__cupri.blits");
        var partial = stats.GetProperty("partial").GetInt32();
        var full = stats.GetProperty("full").GetInt32();
        var pixels = stats.GetProperty("pixels").GetDouble();
        var surface = stats.GetProperty("surface").GetDouble();

        if (partial + full == 0) Assert.Fail(Diagnosis("nothing repainted while the pointer crossed the site"));

        if (partial == 0)
            Assert.Fail(Diagnosis($"all {full} repaints uploaded the whole surface — either the client stopped "
                                  + "passing the damage rectangle on, or the engine stopped narrowing it"));

        Assert.True(pixels < surface * 0.75,
            $"repaints uploaded {pixels:N0} of a possible {surface:N0} pixels ({pixels / surface:P1}) — the "
            + "rectangle is arriving but is barely narrowing anything");

        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// Following a link inside a site — a second page, over the connection already open.
    ///
    /// <para>This client could not do it at all before: its only navigation was a link a visitor typed into the
    /// chrome, which tears the WebRTC session down and dials again. An <c>&lt;a href&gt;</c> within a site is one
    /// Oracle round trip instead.</para>
    ///
    /// <para><b>The marker shares no words with anything on the first page.</b> The first version of this test
    /// looked for "second page", which was also the label of the link that reached it — so it matched before a
    /// single click and passed while proving nothing. A mutation run exposed that by pointing the link
    /// off-network, which the client must refuse, and watching it pass anyway.</para>
    ///
    /// <para>The sweep clicks every position offering a pointer cursor, which on this page is three different
    /// things: a decorative region that does nothing, a <c>cuprinet://</c> link the client must REFUSE, and the
    /// real one. Clicking all three is deliberate — a broken refusal would land somewhere else entirely.</para>
    /// </summary>
    [Fact]
    public async Task A_link_inside_the_site_is_followed()
    {
        _diagnosticsName = "in-site-link";
        await ExpectLogAsync("painted");

        var box = await ViewportAsync();
        var arrived = false;

        for (var row = 0; row < 10 && !arrived; row++)
        {
            for (var col = 0; col < 10 && !arrived; col++)
            {
                var x = (float)(box.X + box.Width * (col + 0.5) / 10);
                var y = (float)(box.Y + box.Height * (row + 0.5) / 10);

                await _page!.Mouse.MoveAsync(x, y);
                await Task.Delay(30);

                if (await _page.EvalOnSelectorAsync<string>("#viewport", "e => e.style.cursor || ''") != "pointer")
                    continue;

                await _page.Mouse.ClickAsync(x, y);
                arrived = await ArrivedAsync(5);
            }
        }

        // One last, longer wait. Following a link is not synchronous with the click: the press is queued for the
        // frame pump, the watcher notices on a later frame, the Oracle answers over the network, and the page that
        // comes back holds its own first paint until its feed binds it. A sweep can therefore move on several cells
        // before the page it asked for appears, which is exactly what made an earlier version of this fail while
        // the mirror it was looking for was, by the end, perfectly correct.
        arrived = arrived || await ArrivedAsync(30);

        if (!arrived)
        {
            var last = await _page!.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
            Assert.Fail(Diagnosis($"no clickable position reached the site's second page; mirror holds: {last}"));
        }

        // The off-network link must have been refused rather than followed: a client that followed it would have
        // left this node and failed the visit.
        lock (_log)
            Assert.DoesNotContain(_log, line => line.Contains("visit failed", StringComparison.Ordinal));

        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// The same, on a page this test made itself. Split out rather than parameterising the shared helper's field,
    /// because a test that quietly re-points <c>_page</c> breaks every other test in the class when it fails
    /// halfway.
    /// </summary>
    private static async Task<bool> ArrivedOnAsync(IPage page, int polls)
    {
        for (var i = 0; i < polls; i++)
        {
            await Task.Delay(100);
            var aria = await page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
            if (aria.Contains("arrived elsewhere", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether the second page's marker has reached the accessibility mirror yet.</summary>
    private async Task<bool> ArrivedAsync(int polls)
    {
        for (var i = 0; i < polls; i++)
        {
            await Task.Delay(100);
            var aria = await _page!.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
            if (aria.Contains("arrived elsewhere", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// A tap reaches the document, through the touch path alone.
    ///
    /// <para><b>This can only pass if touch is wired end to end.</b> A browser sends every touch twice — once as a
    /// real touch event and once as a synthesised pointer event — and the page now drops the synthesised half,
    /// because forwarding both delivers each gesture to the pointer path AND the touch recogniser, which is how one
    /// tap activates a link and then whatever the recogniser decides came next. So on a touch device the pointer
    /// path is deliberately dead, and a tap that still works proves the touch path carried it.</para>
    ///
    /// <para>It taps to FOLLOW A LINK rather than to hover, because a hover has no meaning for a finger and because
    /// arriving at the second page is unambiguous — the same marker the pointer-driven navigation test uses, which
    /// is absent from the page the tap starts on.</para>
    ///
    /// <para>A context of its own, since touch is a property of the context rather than of the page, and enabling
    /// it for the shared one would quietly change every other test here into a touch test.</para>
    /// </summary>
    [Fact]
    public async Task A_tap_reaches_the_document()
    {
        _diagnosticsName = "touch";

        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            HasTouch = true,
            IsMobile = false,   // mobile emulation also forces a viewport and a UA; only the touchscreen is wanted
        });

        var page = await context.NewPageAsync();
        var log = new List<string>();
        var errors = new List<string>();
        page.Console += (_, m) => { lock (log) log.Add(m.Text); };
        page.PageError += (_, e) => { lock (errors) errors.Add(e); };

        await page.GotoAsync(_node.AppUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });

        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            lock (log) if (log.Any(l => l.Contains("painted", StringComparison.Ordinal))) break;
            await Task.Delay(200);
        }

        var box = await page.EvalOnSelectorAsync<System.Text.Json.JsonElement>(
            "#viewport", "e => { const r = e.getBoundingClientRect(); return {x:r.x, y:r.y, w:r.width, h:r.height}; }");
        var bx = box.GetProperty("x").GetDouble();
        var by = box.GetProperty("y").GetDouble();
        var bw = box.GetProperty("w").GetDouble();
        var bh = box.GetProperty("h").GetDouble();

        var arrived = false;
        for (var row = 0; row < 8 && !arrived; row++)
        {
            for (var col = 0; col < 4 && !arrived; col++)
            {
                await page.Touchscreen.TapAsync(
                    (float)(bx + bw * (col + 0.5) / 4),
                    (float)(by + bh * (row + 0.5) / 8));
                await Task.Delay(120);

                var aria = await page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
                arrived = aria.Contains("arrived elsewhere", StringComparison.OrdinalIgnoreCase);
            }
        }

        for (var i = 0; i < 30 && !arrived; i++)
        {
            await Task.Delay(100);
            var aria = await page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
            arrived = aria.Contains("arrived elsewhere", StringComparison.OrdinalIgnoreCase);
        }

        string tail;
        lock (log) tail = string.Join(Environment.NewLine + "  ", log.TakeLast(12));

        Assert.True(arrived,
            "no tap reached the site's second page. Touch is the only input this context produces and the page "
            + "drops the synthesised pointer events, so nothing arrived at the document at all." + Environment.NewLine
            + "client log:" + Environment.NewLine + "  " + tail);

        lock (errors) Assert.Empty(errors);
    }

    /// <summary>
    /// Typing into a field in the site, and an input method composing into it.
    ///
    /// <para><b>This was unreachable until recently.</b> A plain L2 document had nowhere to put a keystroke — an
    /// ordinary <c>&lt;input&gt;</c> was not focusable and <c>DispatchKey</c> answered false for everything — so
    /// this client forwarded keys correctly to a document that could not accept them. CupriFace 0.12.0's
    /// <c>cupri-textfield</c> takes focus and reports a caret, which is what changes.</para>
    ///
    /// <para><b>Composition is the part that matters and the part that is easy to fake.</b> Typing Latin letters
    /// exercises almost nothing: they arrive settled. An input method instead sends a running DRAFT — "nihon"
    /// towards a Japanese word — which the document shows and replaces as it grows. That draft is what this
    /// asserts, driven through CDP's <c>Input.imeSetComposition</c>, and it is the one signal that separates
    /// composition from typing: mutation-tested, disabling <c>SetComposition</c> fails it.</para>
    ///
    /// <para><b>The COMMIT is carried but not asserted, which is worth knowing before trusting this test.</b> CDP's
    /// <c>Input.insertText</c> does not raise a <c>compositionend</c> — the text arrives as ordinary insertion — so
    /// the final Japanese below travels the same path as the Latin typing above, and disabling the commit does not
    /// fail anything here. A real input method does raise it. Verified instead against the engine directly, where
    /// <c>CommitComposition</c> ends the composition and leaves the committed text.</para>
    ///
    /// <para>Read back through the accessibility mirror, because the field is painted to a canvas: there is no DOM
    /// node to query for its value, and the mirror is the only text the page has.</para>
    /// </summary>
    [Fact]
    public async Task Typing_and_composing_reach_a_field_in_the_site()
    {
        _diagnosticsName = "text-input";
        await ExpectLogAsync("painted");

        // Focus the field by clicking it — but ONLY where the document says a text cursor belongs.
        //
        // An earlier version clicked every cell of the sweep and the first thing it hit was the in-site link, so
        // the page navigated to the second one, which has no field. The failure then read "no click focused a text
        // field", which was true and entirely misleading. The cursor is what tells a field from a link before
        // committing to a click.
        var box = await ViewportAsync();
        var focused = false;

        for (var row = 0; row < 12 && !focused; row++)
        {
            for (var col = 0; col < 6 && !focused; col++)
            {
                var x = (float)(box.X + box.Width * (col + 0.5) / 6);
                var y = (float)(box.Y + box.Height * (row + 0.5) / 12);

                await _page!.Mouse.MoveAsync(x, y);
                await Task.Delay(40);

                var cursor = await _page.EvalOnSelectorAsync<string>("#viewport", "e => e.style.cursor || ''");
                if (cursor != "text") continue;

                await _page.Mouse.ClickAsync(x, y);
                await Task.Delay(120);

                // The client moves an offscreen field to the caret and focuses it once the document has one, so
                // the browser's own focus is the honest signal that a field in the SITE took it.
                focused = await _page.EvaluateAsync<bool>(
                    "() => document.activeElement && document.activeElement.tagName === 'TEXTAREA'");
            }
        }

        if (!focused)
        {
            var made = await _page!.EvaluateAsync<bool>("() => !!document.querySelector('textarea')");
            var mirror = await _page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
            Assert.Fail(Diagnosis(
                "no click focused a text field in the site. The offscreen field the client creates for composition "
                + (made ? "EXISTS, so the document reported a caret and focus did not follow"
                        : "was never created, so the document never reported a focused field at all")
                + Environment.NewLine + "mirror: " + mirror));
        }

        await _page!.Keyboard.TypeAsync("cupri");
        await Task.Delay(400);

        var afterTyping = await _page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
        if (!afterTyping.Contains("cupri", StringComparison.Ordinal))
            Assert.Fail(Diagnosis($"typed text never reached the document; the mirror holds: {afterTyping}"));

        // Now compose, the way an input method does: a draft that is replaced as it grows, then one commit.
        var cdp = await _page.Context.NewCDPSessionAsync(_page);
        await cdp.SendAsync("Input.imeSetComposition", new Dictionary<string, object>
        {
            ["text"] = "nihon",
            ["selectionStart"] = 5,
            ["selectionEnd"] = 5,
        });
        await Task.Delay(300);

        var midComposition = await _page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");

        // THE DRAFT IS THE ASSERTION THAT DISTINGUISHES COMPOSITION FROM TYPING. A running draft only reaches the
        // document through SetComposition; if that path were dead the mirror would show the typed text alone and
        // everything after this would still pass, because the commit below arrives as ordinary inserted text.
        if (!midComposition.Contains("nihon", StringComparison.Ordinal))
            Assert.Fail(Diagnosis($"the composition draft never reached the document; the mirror holds: {midComposition}"));

        await cdp.SendAsync("Input.insertText", new Dictionary<string, object> { ["text"] = "日本" });
        await Task.Delay(400);

        var committed = await _page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");

        if (!committed.Contains("日本", StringComparison.Ordinal))
            Assert.Fail(Diagnosis(
                "the committed composition never reached the document. mid-composition the mirror held: "
                + midComposition + Environment.NewLine + "after commit: " + committed));

        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// A video in a site, decoded by the browser and underlaid beneath the document.
    ///
    /// <para><b>The engine does not decode.</b> It lays the video out, punches a TRANSPARENT HOLE in the frame
    /// where the element shows, and paints its own controls on top; a real <c>&lt;video&gt;</c> sits behind the
    /// canvas and shows through. So the assertion is that such an element exists, that it is positioned where the
    /// engine said the hole is, and that it decoded something — a client that created the element and left it at
    /// the origin produces a video somewhere else on the screen from the frame it belongs in.</para>
    ///
    /// <para><b>The conversion is the part most likely to be wrong.</b> This canvas is sized in DEVICE pixels so a
    /// site renders at the display's real resolution, and everything the engine says is in canvas pixels — so on
    /// any screen with a scale factor the rect is larger than the box it belongs in by exactly that factor. The
    /// element's width is compared against the canvas's own CSS box for that reason.</para>
    ///
    /// <para>The video lives on the SECOND page, so reaching it means a navigation and the document it opens on
    /// was built after the client re-Inited the host. The first page had no room: it declares an 800x600 design
    /// box that its content already fills.</para>
    /// </summary>
    [Fact]
    public async Task A_video_in_the_site_is_underlaid_and_decoded()
    {
        _diagnosticsName = "video";

        // ITS OWN CONTEXT, AT 2x. The shared one runs at a device pixel ratio of 1, where the conversion this test
        // exists to check is the identity — a client that never divided by the density passed it just as well, and
        // a mutation run proved that by surviving. At 2x an unconverted rect is twice the size it should be.
        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            DeviceScaleFactor = 2,
        });

        var page = await context.NewPageAsync();
        page.Console += (_, m) => { lock (_log) _log.Add(m.Text); };
        page.PageError += (_, e) => { lock (_pageErrors) _pageErrors.Add(e); };
        await page.GotoAsync(_node.AppUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });

        var painted = false;
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline && !painted)
        {
            lock (_log) painted = _log.Any(l => l.Contains("painted", StringComparison.Ordinal));
            if (!painted) await Task.Delay(200);
        }
        Assert.True(painted, Diagnosis("the client never painted the site's first page"));

        var box = (await page.EvalOnSelectorAsync<JsonElement>(
            "#viewport", "e => { const r = e.getBoundingClientRect(); return {x:r.x, y:r.y, w:r.width, h:r.height}; }"));
        var boxX = box.GetProperty("x").GetDouble();
        var boxY = box.GetProperty("y").GetDouble();
        var boxW = box.GetProperty("w").GetDouble();
        var boxH = box.GetProperty("h").GetDouble();

        var arrived = false;

        for (var row = 0; row < 10 && !arrived; row++)
        {
            for (var col = 0; col < 10 && !arrived; col++)
            {
                var x = (float)(boxX + boxW * (col + 0.5) / 10);
                var y = (float)(boxY + boxH * (row + 0.5) / 10);

                await page.Mouse.MoveAsync(x, y);
                await Task.Delay(30);
                if (await page.EvalOnSelectorAsync<string>("#viewport", "e => e.style.cursor || ''") != "pointer")
                    continue;

                await page.Mouse.ClickAsync(x, y);
                arrived = await ArrivedOnAsync(page, 5);
            }
        }

        arrived = arrived || await ArrivedOnAsync(page, 30);
        Assert.True(arrived, Diagnosis("never reached the site's second page, so the video was never laid out"));

        // Polled, because opening one is not synchronous with the page arriving: the engine lays out, asks for an
        // open, and reports the rect on a later frame.
        JsonElement placed = default;
        for (var poll = 0; poll < 40; poll++)
        {
            await Task.Delay(150);
            placed = await page.EvaluateAsync<JsonElement>(VideoProbe);

            if (placed.GetProperty("present").GetBoolean()
                && placed.GetProperty("shown").GetBoolean()
                && placed.GetProperty("width").GetDouble() > 0
                && placed.GetProperty("readyState").GetInt32() > 0) break;
        }

        Assert.True(placed.GetProperty("present").GetBoolean(),
            Diagnosis("the site's video never produced a <video> element on the page"));
        Assert.True(placed.GetProperty("shown").GetBoolean(),
            Diagnosis("the video element was created and left hidden, so no rect ever reached it"));

        // It decoded. readyState 0 is HAVE_NOTHING — an element with a source the browser could not read.
        Assert.True(placed.GetProperty("readyState").GetInt32() > 0,
            Diagnosis("the browser decoded nothing: the inline source never became a playable blob"));

        // Behind the canvas, which is what makes the hole a hole rather than a video over the top of the site.
        Assert.Equal("0", placed.GetProperty("zIndex").GetString());
        Assert.Equal("1", placed.GetProperty("canvasZ").GetString());

        // The canvas gave up its opaque CSS background, or the hole is filled in with grey behind the video.
        var background = placed.GetProperty("canvasBackground").GetString() ?? "";
        Assert.True(background.Contains("rgba(0, 0, 0, 0)", StringComparison.Ordinal)
                    || background.Contains("transparent", StringComparison.Ordinal),
            Diagnosis($"the canvas kept an opaque background ({background}), so the punched hole shows nothing"));

        var left = placed.GetProperty("left").GetDouble();
        var top = placed.GetProperty("top").GetDouble();
        var width = placed.GetProperty("width").GetDouble();
        var canvasLeft = placed.GetProperty("canvasLeft").GetDouble();
        var canvasTop = placed.GetProperty("canvasTop").GetDouble();
        var canvasWidth = placed.GetProperty("canvasWidth").GetDouble();
        var canvasHeight = placed.GetProperty("canvasHeight").GetDouble();

        Assert.True(left >= canvasLeft - 1 && left <= canvasLeft + canvasWidth,
            Diagnosis($"the video sits at x={left}, outside the canvas box [{canvasLeft}, {canvasLeft + canvasWidth}]"));
        Assert.True(top >= canvasTop - 1 && top <= canvasTop + canvasHeight,
            Diagnosis($"the video sits at y={top}, outside the canvas box [{canvasTop}, {canvasTop + canvasHeight}]"));

        // THE CONVERSION ITSELF, against the engine's own number rather than against a guess at the layout.
        //
        // The rect arrives in canvas pixels and the element is laid out in CSS ones, so the element must come out
        // exactly `density` times narrower. Checking it any other way means predicting where the hybrid zoom put a
        // 320-pixel element, and the loose version of this assertion — "it fits inside the canvas" — was measured
        // to pass with the division removed, because at this page's zoom even a doubled element still fits.
        var ratio = placed.GetProperty("ratio").GetDouble();
        var asked = placed.GetProperty("askedWidth").GetDouble();

        Assert.True(ratio > 1.5,
            Diagnosis($"this context reports {ratio} device pixels per CSS pixel, so the conversion under test is "
                      + "the identity and the assertion below proves nothing"));
        Assert.True(asked > 0, Diagnosis("no rect was ever recorded, so there is nothing to compare against"));

        Assert.True(Math.Abs(width - asked / ratio) <= 1.0,
            Diagnosis($"the engine asked for {asked} canvas pixels at {ratio}x, so the element should be "
                      + $"{asked / ratio}px wide; it is {width}px. The engine's canvas pixels reached the page "
                      + "without being converted to CSS ones."));

        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// Everything the video assertions need, read in one round trip: the element, its box, the canvas's box, and
    /// the stacking that decides whether it is underlaid or painted over.
    /// </summary>
    private const string VideoProbe = """
        () => {
          const v = document.querySelector('video');
          const c = document.getElementById('viewport');
          if (!v || !c) return { present: false, shown: false, width: 0, height: 0, left: 0, top: 0,
                                 canvasLeft: 0, canvasTop: 0, canvasWidth: 0, canvasHeight: 0,
                                 readyState: 0, zIndex: '', canvasZ: '', canvasBackground: '' };
          const vb = v.getBoundingClientRect(), cb = c.getBoundingClientRect();
          return {
            present: true,
            shown: v.style.display !== 'none',
            width: vb.width, height: vb.height, left: vb.left, top: vb.top,
            canvasLeft: cb.left, canvasTop: cb.top, canvasWidth: cb.width, canvasHeight: cb.height,
            readyState: v.readyState,
            zIndex: getComputedStyle(v).zIndex,
            canvasZ: getComputedStyle(c).zIndex,
            canvasBackground: getComputedStyle(c).backgroundColor,
            askedWidth: (globalThis.__cupri.lastVideoRect || {}).w || 0,
            askedHeight: (globalThis.__cupri.lastVideoRect || {}).h || 0,
            ratio: c.clientWidth ? c.width / c.clientWidth : 0
          };
        }
        """;

    /// <summary>
    /// A right-click reaching the document, which answers with a menu of its own.
    ///
    /// <para><b>The browser's menu is always the wrong one here.</b> Over a canvas it offers "Save image as" for a
    /// picture of somebody's site; over a field in that site it offers none of the editing the document can
    /// actually do. So the page swallows it and the document paints its own — measured against the host directly,
    /// <c>DispatchContextMenu</c> on a text field returns true and puts <c>menuitem</c> entries into the ARIA
    /// mirror, which is also what makes this assertable: the menu is a screen reader's to read.</para>
    ///
    /// <para>Asserted on the MIRROR rather than on the return value, because the return value is always true — it
    /// says the event was claimed, not that a menu opened. On bare background it claims the click and adds
    /// nothing, so only the mirror separates a menu from a dismissal.</para>
    /// </summary>
    [Fact]
    public async Task A_right_click_opens_the_document_s_own_menu()
    {
        _diagnosticsName = "context-menu";
        await ExpectLogAsync("painted");

        var box = await ViewportAsync();
        var opened = false;

        // Aimed by cursor, like every other test that has to find something on this canvas: the field is what has
        // a menu worth opening, and where it lands depends on the zoom the page was fitted at.
        for (var row = 0; row < 12 && !opened; row++)
        {
            for (var col = 0; col < 6 && !opened; col++)
            {
                var x = (float)(box.X + box.Width * (col + 0.5) / 6);
                var y = (float)(box.Y + box.Height * (row + 0.5) / 12);

                await _page!.Mouse.MoveAsync(x, y);
                await Task.Delay(40);
                if (await _page.EvalOnSelectorAsync<string>("#viewport", "e => e.style.cursor || ''") != "text")
                    continue;

                await _page.Mouse.ClickAsync(x, y, new MouseClickOptions { Button = MouseButton.Right });

                for (var poll = 0; poll < 12 && !opened; poll++)
                {
                    await Task.Delay(100);
                    var mirror = await _page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
                    opened = mirror.Contains("menuitem", StringComparison.Ordinal);
                }
            }
        }

        if (!opened)
        {
            var last = await _page!.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
            Assert.Fail(Diagnosis($"no right-click opened a menu in the document; mirror holds: {last}"));
        }

        Assert.Empty(_pageErrors);
    }

    /// <summary>
    /// Undo and redo over a history the engine keeps and the browser knows nothing about.
    ///
    /// <para>The chords are handled on the OFFSCREEN FIELD as well as on the canvas, because that field holds the
    /// browser's focus whenever a field in the site has the document's — so the canvas handler never sees a
    /// keystroke typed into a site, which is exactly when undo is wanted.</para>
    ///
    /// <para>Both directions are asserted, and both matter: an undo that clears the field would also pass a test
    /// that only checked the text was gone, and it is the redo that separates a history from a delete.</para>
    /// </summary>
    [Fact]
    public async Task Typing_can_be_undone_and_redone()
    {
        _diagnosticsName = "undo";
        await ExpectLogAsync("painted");

        var typed = "undoable";
        var focused = await TypeIntoTheSitesFieldAsync(typed);
        Assert.True(focused, Diagnosis("no click focused a text field in the site"));

        var afterTyping = await _page!.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
        Assert.True(afterTyping.Contains(typed, StringComparison.Ordinal),
            Diagnosis($"the typed text never reached the document; mirror holds: {afterTyping}"));

        await _page.Keyboard.PressAsync("Control+Z");
        var undone = await MirrorSettlesAsync(mirror => !mirror.Contains(typed, StringComparison.Ordinal));
        Assert.True(undone, Diagnosis("Ctrl+Z left the typed text in the document"));

        await _page.Keyboard.PressAsync("Control+Y");
        var redone = await MirrorSettlesAsync(mirror => mirror.Contains(typed, StringComparison.Ordinal));
        Assert.True(redone, Diagnosis("Ctrl+Y did not bring the undone text back"));

        Assert.Empty(_pageErrors);
    }

    /// <summary>Polls the accessibility mirror until it satisfies <paramref name="settled"/>, or gives up.</summary>
    private async Task<bool> MirrorSettlesAsync(Func<string, bool> settled, int polls = 15)
    {
        for (var i = 0; i < polls; i++)
        {
            await Task.Delay(120);
            if (settled(await _page!.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''"))) return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the site's text field by cursor, clicks it and types. Shared because three tests need a field with
    /// something in it, and because finding one is the fiddly part: sweeping and clicking everything hits the
    /// in-site link first and navigates away, which reads as "no field" and is entirely misleading.
    /// </summary>
    private async Task<bool> TypeIntoTheSitesFieldAsync(string text)
    {
        var box = await ViewportAsync();

        for (var row = 0; row < 12; row++)
        {
            for (var col = 0; col < 6; col++)
            {
                var x = (float)(box.X + box.Width * (col + 0.5) / 6);
                var y = (float)(box.Y + box.Height * (row + 0.5) / 12);

                await _page!.Mouse.MoveAsync(x, y);
                await Task.Delay(40);
                if (await _page.EvalOnSelectorAsync<string>("#viewport", "e => e.style.cursor || ''") != "text")
                    continue;

                await _page.Mouse.ClickAsync(x, y);
                await Task.Delay(120);

                var onField = await _page.EvaluateAsync<bool>(
                    "() => document.activeElement && document.activeElement.tagName === 'TEXTAREA'");
                if (!onField) continue;

                await _page.Keyboard.TypeAsync(text);
                await Task.Delay(400);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copying out of a field in the site, onto the real system clipboard.
    ///
    /// <para><b>The client has to do this; the engine does not.</b> Measured against the host directly:
    /// <c>KeyChord("c", Ctrl)</c> answers false and raises nothing on the bridge, because those chords are for
    /// shortcuts an app registered. What the engine offers is <c>CopySelection()</c>, so the page intercepts the
    /// browser's own copy event, asks for the document's selection a frame later, and writes that.</para>
    ///
    /// <para>Asserted by reading the clipboard back through the browser, which is the only place the answer
    /// actually matters — a test that checked the client had CALLED a write would pass just as well if the write
    /// never reached the system.</para>
    /// </summary>
    [Fact]
    public async Task Copying_from_a_field_reaches_the_system_clipboard()
    {
        _diagnosticsName = "clipboard";

        await using var context = await _browser!.NewContextAsync();
        await context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);

        var page = await context.NewPageAsync();
        var log = new List<string>();
        page.Console += (_, m) => { lock (log) log.Add(m.Text); };
        await page.GotoAsync(_node.AppUrl, new PageGotoOptions { WaitUntil = WaitUntilState.Load, Timeout = 30_000 });

        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            lock (log) if (log.Any(l => l.Contains("painted", StringComparison.Ordinal))) break;
            await Task.Delay(200);
        }

        // Something the clipboard cannot already contain, so reading it back proves this copy rather than a
        // leftover from the machine the test happens to run on.
        var secret = "cupri-clip-" + Guid.NewGuid().ToString("N")[..8];
        await page.EvaluateAsync("() => navigator.clipboard.writeText('nothing-copied-yet')");

        var box = await page.EvalOnSelectorAsync<System.Text.Json.JsonElement>(
            "#viewport", "e => { const r = e.getBoundingClientRect(); return {x:r.x, y:r.y, w:r.width, h:r.height}; }");
        var bx = box.GetProperty("x").GetDouble();
        var by = box.GetProperty("y").GetDouble();
        var bw = box.GetProperty("w").GetDouble();
        var bh = box.GetProperty("h").GetDouble();

        var focused = false;
        for (var row = 0; row < 12 && !focused; row++)
        {
            for (var col = 0; col < 6 && !focused; col++)
            {
                var x = (float)(bx + bw * (col + 0.5) / 6);
                var y = (float)(by + bh * (row + 0.5) / 12);

                await page.Mouse.MoveAsync(x, y);
                await Task.Delay(40);
                if (await page.EvalOnSelectorAsync<string>("#viewport", "e => e.style.cursor || ''") != "text") continue;

                await page.Mouse.ClickAsync(x, y);
                await Task.Delay(120);
                focused = await page.EvaluateAsync<bool>(
                    "() => document.activeElement && document.activeElement.tagName === 'TEXTAREA'");
            }
        }

        Assert.True(focused, "no click focused a text field in the site");

        await page.Keyboard.TypeAsync(secret);
        await Task.Delay(400);

        await page.Keyboard.PressAsync("Control+A");
        await Task.Delay(200);
        await page.Keyboard.PressAsync("Control+C");
        await Task.Delay(600);

        var clipboard = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");

        Assert.True(clipboard.Contains(secret, StringComparison.Ordinal),
            $"the clipboard holds '{clipboard}' rather than the text typed into the site's field ('{secret}'). "
            + "Either the copy event never reached the client, or the document's selection was empty when it "
            + "answered.");

        // Cut, which is the same read plus a deletion, and the half that can lose text: CutSelection both returns
        // and removes, so writing before cutting would be the ordering where a refused clipboard destroys the
        // visitor's text. Asserted from both ends — the clipboard has it, the document no longer does.
        await page.EvaluateAsync("() => navigator.clipboard.writeText('nothing-cut-yet')");
        await page.Keyboard.PressAsync("Control+A");
        await Task.Delay(200);
        await page.Keyboard.PressAsync("Control+X");
        await Task.Delay(600);

        var cut = await page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
        Assert.True(cut.Contains(secret, StringComparison.Ordinal),
            $"a cut left '{cut}' on the clipboard rather than '{secret}'.");

        var aria = await page.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");
        Assert.False(aria.Contains(secret, StringComparison.Ordinal),
            "the cut text is still in the document — the selection was copied but never removed.");
    }

    /// <summary>
    /// The site being readable by a screen reader.
    ///
    /// <para><b>Why this is a gate test and not a unit test.</b> A canvas announces itself and nothing inside it, so
    /// for anyone using a screen reader every site this client rendered was a blank page. CupriFace's host builds an
    /// ARIA tree from the layout it painted and the client mirrors it into a hidden element — but "the managed code
    /// called PublishAria" is not the claim worth pinning. The claim is that a real browser ends up with real
    /// accessible nodes in its real DOM, which is only observable here.</para>
    ///
    /// <para>Asserted on <c>role=</c> rather than on any particular text: what the sample site says is the sample's
    /// business and will change, whereas a tree with no roles in it is not an accessibility tree at all.</para>
    /// </summary>
    [Fact]
    public async Task The_site_is_exposed_to_a_screen_reader()
    {
        _diagnosticsName = "aria-mirror";
        await ExpectLogAsync("painted");

        // The mirror is published on the same frame as the paint, so one frame of slack rather than a poll.
        await Task.Delay(120);

        var aria = await _page!.EvalOnSelectorAsync<string>("#aria", "e => e.innerHTML || ''");

        if (string.IsNullOrWhiteSpace(aria))
            Assert.Fail(Diagnosis("the accessibility mirror was empty, so the site is a blank page to a screen reader"));

        if (!aria.Contains("role=", StringComparison.Ordinal))
            Assert.Fail(Diagnosis($"the mirror carried no roles, so there is nothing to navigate: {aria}"));

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

            // Page errors are printed with the rest rather than only by the assertion at the end of each test:
            // a failure that stops a test early never reaches that assertion, and an exception thrown inside the
            // module is exactly the kind of failure that stops one early.
            string errors;
            lock (_pageErrors)
                errors = _pageErrors.Count > 0 ? string.Join(Indent, _pageErrors) : "(none)";

            return $"{headline}\n\npage errors:{Indent}{errors}\n\nclient log:{Indent}{client}\n\nnode log:{Indent}{node}";
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
    /// <summary>How many incremental blits the page has done, for spotting which positions actually repaint.</summary>
    private async Task<int> PartialBlitsAsync()
    {
        var s = await _page!.EvaluateAsync<System.Text.Json.JsonElement>("() => globalThis.__cupri.blits");
        return s.GetProperty("partial").GetInt32();
    }

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
