using System.Diagnostics;
using System.Runtime.InteropServices;
using CupriFace;
using CupriFace.Interaction;
using CupriFace.Web;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// Shows an L2 site, by driving CupriFace's browser host.
///
/// <para><b>This used to do the rendering itself</b> — a Skia surface, a hybrid-zoom scale, a premultiplied-to-
/// straight readback and a blit, plus hit-testing that had to divide by the same zoom the paint had multiplied by.
/// All of that is now <c>WebHostCore</c>'s, which also brings what was never written here: the ARIA mirror a screen
/// reader reads, damage-rect painting, IME composition and touch.</para>
///
/// <para><b>What is deliberately NOT surrendered</b> is the loop. <c>WebHost.Run</c> would own the frame pump, and
/// this client's pump is also the async pump that NativeAOT-LLVM wasm has no event loop for (see
/// <see cref="BrowserLoop"/>). So the host is driven a frame at a time from the pump that already exists, through
/// <c>WebHostCore.Tick</c>, and everything the host wants to say to the page arrives at
/// <see cref="CupriFaceBridge"/>.</para>
///
/// <para><b>Coordinates are host pixels now, not logical ones.</b> Every dispatch below used to divide by the zoom
/// before handing a position to the document; the host does that itself, from the same <c>PresentInfo</c> it scaled
/// the canvas with. Checked rather than assumed — a pointer at device (120,40) on a 2x-scaled document highlighted
/// an element occupying logical (0,0)-(80,30), which only holds if the host is dividing.</para>
/// </summary>
internal static partial class BrowserRenderer
{
    private static readonly CupriFaceBridge Bridge = new();

    /// <summary>Whether a site has been shown, so input and frames have something to reach.</summary>
    private static bool _live;

    /// <summary>Whether a freshly loaded document is still waiting for its first feed message before being shown.</summary>
    private static bool _awaitingFirstBind;
    private static long _bindDeadline;

    /// <summary>
    /// Whether anything has been drawn for the current document yet, so the FIRST paint can be announced.
    ///
    /// <para>Announced because the first paint is not a consequence of loading the page — it waits for the feed to
    /// bind. Anything checking that a site rendered has to observe the moment it did rather than assume it followed
    /// the fetch, which is the assumption that made the browser gate racy.</para>
    /// </summary>
    private static bool _painted;

    [LibraryImport("js", EntryPoint = "cupri_canvas_width")]
    private static partial int CanvasWidth();

    [LibraryImport("js", EntryPoint = "cupri_canvas_height")]
    private static partial int CanvasHeight();

    [LibraryImport("js", EntryPoint = "cupri_canvas_scale")]
    private static partial float CanvasScale();

    /// <summary>
    /// A link the visitor followed inside the site, taken and cleared.
    ///
    /// <para>Reported by the document itself, through <c>CupriDocument.Navigated</c>. That is the engine's own
    /// answer for links and it carries every href a click resolved to, with <c>External</c> already separating one
    /// a host should open in a browser from one the app is expected to route.</para>
    ///
    /// <para><b>Not <c>OnClick("a", …)</c>, which never fires for an anchor</b> — the engine's link branch claims
    /// the click first — and not <c>IWebBridge.Navigate</c>, which a host raises only for the EXTERNAL subset, so
    /// relative and custom-scheme links appear to vanish. Both measured before this was written, and both are the
    /// wrong end of the same event.</para>
    ///
    /// <para>Fragment-only hrefs (<c>#…</c>) do not arrive here at all, which is correct: they move within a page
    /// rather than to another one.</para>
    /// </summary>
    public static string? TakeNavigation()
    {
        var followed = _followed;
        _followed = null;
        return followed;
    }

    private static string? _followed;

    /// <summary>
    /// Records a link the document resolved, for the visit loop to act on.
    ///
    /// <para>Recorded rather than followed here: navigating is a Pilgrimage or an Oracle consult, and neither can
    /// happen inside a paint. <c>Program</c> decides what the href is allowed to mean.</para>
    /// </summary>
    private static void OnNavigated(NavigateEvent e)
    {
        // External is the engine's own classification of an href a host should hand to a browser. This client has
        // no browser to hand it to and no business opening one, so it is refused here, by name, rather than
        // travelling further as an ordinary path.
        if (e.External)
        {
            Console.WriteLine($"[cupri] refused an off-network link: {e.Href}");
            return;
        }

        _followed = e.Href;
    }

    /// <summary>
    /// Points the host at a freshly fetched document.
    ///
    /// <para>A re-<c>Init</c> rather than a mutation, because there is no API to re-point a live host and there does
    /// not need to be: <c>Init</c> builds a new document each time, which is what navigation means here.</para>
    /// </summary>
    public static void Show(string html)
    {
        var (designWidth, designHeight) = SiteManifest.DesignSize(html);
        var app = new SiteApp(html, designWidth, designHeight, CanvasScale);

        WebHostCore.Init(app, Configure, Bridge);
        _live = true;

        // NOT painted here, deliberately.
        //
        // A Document-tier page arrives as a TEMPLATE: its live values are {{ }} placeholders that only become text
        // when the first feed message binds. Painting on arrival therefore shows the raw template — "{{ node.site }}"
        // scattered across the page — until the snapshot lands a moment later. On a reconnect that is worse than
        // ugly, because the canvas already holds a perfectly good render of the same site and the flash replaces it
        // with something that looks broken.
        //
        // So the first paint waits for the first bind. The deadline is the safety net: a site with no feed at all
        // would otherwise never appear, so once it passes the template is painted as-is.
        _awaitingFirstBind = true;
        _painted = false;
        _bindDeadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 3 / 4);   // 750ms
    }

    /// <summary>
    /// Prepares a document the host has just built: the embedded faces, and the link hook.
    ///
    /// <para>Wasm has no system font list to fall back on, so without these text lays out and paints as nothing —
    /// which reads as a broken renderer rather than a missing asset. This is what the host's <c>configure</c>
    /// callback is for: the document exists but has not been laid out yet.</para>
    /// </summary>
    private static void Configure(CupriDocument document)
    {
        // Links, from the engine's own event. Subscribed here because the document is new on every visit and every
        // in-site navigation — a handler attached once to a document that gets replaced stops being called, which
        // would look exactly like links working and then quietly not.
        document.Navigated += OnNavigated;

        var assembly = typeof(BrowserRenderer).Assembly;
        foreach (var name in new[] { "fonts.NotoSans-Regular.ttf", "fonts.NotoSans-Bold.ttf" })
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                // Naming these is the difference between "fonts are broken" and "the LogicalName is not what I
                // assumed" — and under trimming it is also how you learn the resource was dropped entirely.
                Console.WriteLine($"[cupri] font resource missing: {name}; available: " +
                                  string.Join(", ", assembly.GetManifestResourceNames()));
                continue;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            document.LoadFont(memory.ToArray());
        }
    }

    /// <summary>
    /// Applies a feed message to the document.
    ///
    /// <para>This is what makes a Document-tier site live. It has no JavaScript engine to run, so a snapshot or
    /// update cannot be handled by the page itself — the client binds the payload and asks the engine to rebuild.</para>
    /// </summary>
    public static void Update(ReadOnlySpan<byte> payload)
    {
        if (!_live) return;

        var model = FeedModel.Parse(payload);
        if (model is null)
        {
            Console.WriteLine("[cupri] feed message was not a JSON object; ignored");
            return;
        }

        var document = WebHostCore.Document;
        document.Bind(model);
        document.Refresh();

        // The document now has real values in it, so it is safe to show. On a reconnect this is the moment the old
        // frame is replaced — by a complete one, rather than by a template mid-bind.
        _awaitingFirstBind = false;
        WebHostCore.MarkDirty();
    }

    /// <summary>
    /// One frame. Called from the pump.
    ///
    /// <para>The host decides whether anything needs painting: <c>Tick</c> answers false when nothing changed, so an
    /// idle page costs one call and no pixels. That was previously this client's own decision, made by asking the
    /// document whether its animation clock had moved anything — the host's answer also covers layout, input and
    /// media, which the old check did not.</para>
    ///
    /// <para>Resize needs no special handling any more either. The size is an argument to every tick, so a canvas
    /// that changed simply lays out at the new one; the old code had to notice a change itself and force a repaint.</para>
    /// </summary>
    public static void Animate(double seconds)
    {
        if (!_live) return;

        // The safety net for a document whose feed never arrives: show it anyway rather than leave the visitor on a
        // blank canvas — or, worse, on the previous site's page — indefinitely.
        if (_awaitingFirstBind)
        {
            if (Stopwatch.GetTimestamp() < _bindDeadline) return;

            Console.WriteLine("[cupri] no feed message within 750ms — painting the page unbound");
            _awaitingFirstBind = false;
        }

        var width = CanvasWidth();
        var height = CanvasHeight();
        if (width <= 0 || height <= 0) return;

        if (!WebHostCore.Tick(width, height, seconds * 1000.0)) return;

        if (!_painted)
        {
            _painted = true;
            Console.WriteLine("[cupri] painted");
        }
    }

    /// <summary>
    /// Whether the document is ready to be interacted with.
    ///
    /// <para>Input before the first paint is dropped rather than queued. The page is still a template at that point,
    /// so a click would hit-test against placeholder text and land somewhere the visitor never saw — and they cannot
    /// have aimed at something that was not on screen.</para>
    /// </summary>
    private static bool Ready => _live && !_awaitingFirstBind;

    /// <summary>
    /// A position in CSS pixels, in the host pixels the host expects.
    ///
    /// <para>The page reports pointer positions in CSS pixels; the surface the host is ticked with is in DEVICE
    /// pixels. Only the density separates them — the zoom is the host's own business, and dividing by it here (as
    /// the hand-rolled renderer had to) would now apply it twice.</para>
    /// </summary>
    private static (double X, double Y) Host(float cssX, float cssY)
    {
        var density = CanvasScale();
        if (density <= 0) density = 1f;
        return (cssX * density, cssY * density);
    }

    public static void PointerMove(float cssX, float cssY)
    {
        if (!Ready) return;
        var (x, y) = Host(cssX, cssY);
        WebHostCore.PointerMove(x, y);
    }

    /// <summary>
    /// A press, carrying the click count.
    ///
    /// <para>There is no separate click dispatch any more: the host raises one from the press and release, which
    /// was verified rather than assumed — a down and an up over an element with a click handler fired it exactly
    /// once. Forwarding the page's own click event as well would activate every link twice.</para>
    /// </summary>
    public static void PointerDown(float cssX, float cssY, int clicks)
    {
        if (!Ready) return;
        var (x, y) = Host(cssX, cssY);
        WebHostCore.PointerDown(x, y, clicks <= 0 ? 1 : clicks);
    }

    public static void PointerUp(float cssX, float cssY)
    {
        if (!Ready) return;
        var (x, y) = Host(cssX, cssY);
        WebHostCore.PointerUp(x, y);
    }

    /// <summary>Whether the engine has been told this is a touch device, so it is said once rather than per frame.</summary>
    private static bool _coarse;

    /// <summary>
    /// A finger, forwarded to the host's touch recogniser.
    ///
    /// <para><b>Not the pointer path.</b> The page drops the synthesised pointer events a touch also produces and
    /// sends these instead, because they carry what the pointer ones lose: an identity, so more than one finger can
    /// be followed, and a timestamp, so the recogniser can tell a flick from a slow drag. That recogniser is what
    /// turns a swipe into a fling with momentum, and it is the whole reason this exists — a tap already worked
    /// through the pointer path.</para>
    ///
    /// <para>The first touch also tells the engine the pointer is COARSE. It sizes hit targets differently for a
    /// finger than for a mouse, and a fingertip aimed at mouse-sized targets is the difference between a site that
    /// works on a phone and one that nearly does.</para>
    /// </summary>
    public static void TouchDown(int id, float cssX, float cssY, float timeMs)
    {
        if (!Ready) return;

        if (!_coarse)
        {
            _coarse = true;
            WebHostCore.SetCoarsePointer(true);
        }

        var (x, y) = Host(cssX, cssY);
        WebHostCore.TouchDown(id, x, y, timeMs);
    }

    public static void TouchMove(int id, float cssX, float cssY, float timeMs)
    {
        if (!Ready) return;
        var (x, y) = Host(cssX, cssY);
        WebHostCore.TouchMove(id, x, y, timeMs);
    }

    public static void TouchUp(int id, float cssX, float cssY, float timeMs)
    {
        if (!Ready) return;
        var (x, y) = Host(cssX, cssY);
        WebHostCore.TouchUp(id, x, y, timeMs);
    }

    /// <summary>
    /// A finger the browser took away — a call arriving, a gesture claimed by the system, a finger leaving the
    /// screen edge. It carries no position because there is no longer one; what matters is that the recogniser
    /// stops waiting for the rest of a gesture that is not coming.
    /// </summary>
    public static void TouchCancel(int id, float timeMs)
    {
        if (!Ready) return;
        WebHostCore.TouchCancel(id, timeMs);
    }

    /// <summary>
    /// Scrolling, which is what the wheel is for on a page taller than the canvas.
    ///
    /// <para>The delta is NOT scaled. It is a distance the visitor asked the content to move, not a position in it —
    /// scaling would make a page scroll further the more it had been shrunk to fit, which is backwards from how it
    /// feels to use.</para>
    /// </summary>
    public static void Wheel(float cssX, float cssY, float deltaY, float deltaX)
    {
        if (!Ready) return;
        var (x, y) = Host(cssX, cssY);
        WebHostCore.Wheel(x, y, deltaY);
    }

    /// <summary>
    /// Text an input method is still deciding about.
    ///
    /// <para>Sent on every keystroke of a composition — typing "nihon" towards 日本 produces one of these per
    /// letter. The document shows it underlined and replaces it wholesale each time, which is why this passes the
    /// running text rather than a delta.</para>
    /// </summary>
    public static void Composing(string text)
    {
        if (!Ready) return;
        WebHostCore.SetComposition(text);
    }

    /// <summary>
    /// What the input method settled on, which is the only part that becomes real text.
    ///
    /// <para>An empty commit is a CANCELLED composition — the visitor pressed Escape or clicked away — and has to
    /// be told apart from committing nothing, or the underlined draft stays on screen with no way to remove it.</para>
    /// </summary>
    public static void Composed(string text)
    {
        if (!Ready) return;

        if (string.IsNullOrEmpty(text)) WebHostCore.CancelComposition();
        else WebHostCore.CommitComposition(text);
    }

    /// <summary>
    /// Text that arrived already settled: ordinary typing, a paste, a phone keyboard completing a word.
    ///
    /// <para>Whole strings rather than characters, because that is how the browser delivers them — a paste is one
    /// event, and so is an autocorrected word replacing what was typed.</para>
    /// </summary>
    public static void Inserted(string text)
    {
        if (!Ready || string.IsNullOrEmpty(text)) return;
        WebHostCore.KeyChar(text);
    }

    /// <summary>
    /// Whether the document has a focused text field, and so whether a keystroke belongs to it.
    ///
    /// <para>Answered by the engine rather than assumed. The host also tells the page directly, through
    /// <c>IWebBridge.SetTextInput</c>; this remains because the input pump asks the question on its own schedule.</para>
    /// </summary>
    public static bool WantsKeys
    {
        get
        {
            if (!Ready) return false;

            try
            {
                return WebHostCore.Document.GetTextInputState().Focused;
            }
            catch (Exception)
            {
                // An engine that cannot answer is not an engine that wants the key.
                return false;
            }
        }
    }

    /// <summary>
    /// A keystroke.
    ///
    /// <para>Still dispatched on the document rather than through the host: <c>WebHostCore</c>'s key entry points
    /// are shaped for its own JavaScript, which hands text over through a shared buffer, and this client already has
    /// the string.</para>
    /// </summary>
    public static void Key(string text, int editKey, int mods)
    {
        if (!Ready) return;
        if (WebHostCore.Document.DispatchKey(text, (EditKey)editKey, (KeyMods)mods)) WebHostCore.MarkDirty();
    }
}
