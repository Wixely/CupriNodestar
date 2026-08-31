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

    /// <summary>A link the document followed, for <see cref="BrowserNavigation"/> to make a Pilgrimage to.</summary>
    public static string? TakeNavigation() => Bridge.TakeNavigation();

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

        WebHostCore.Init(app, LoadFonts, Bridge);
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
    /// Registers the embedded faces on a document the host has just built.
    ///
    /// <para>Wasm has no system font list to fall back on, so without these text lays out and paints as nothing —
    /// which reads as a broken renderer rather than a missing asset. This is what the host's <c>configure</c>
    /// callback is for: the document exists but has not been laid out yet.</para>
    /// </summary>
    private static void LoadFonts(CupriDocument document)
    {
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
