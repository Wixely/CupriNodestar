using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CupriFace;
using CupriFace.Interaction;
using SkiaSharp;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// Paints an L2 site into the page's canvas with CupriFace — HTML and CSS drawn by the engine itself, with no browser
/// engine and no JavaScript engine, so a hostile site has no script runtime to reach for.
/// </summary>
internal static unsafe partial class BrowserRenderer
{
    private static CupriDocument? _document;

    /// <summary>The canvas size the current pixels were drawn for, so a change can be noticed without a JS callback.</summary>
    private static int _paintedWidth;
    private static int _paintedHeight;

    /// <summary>Whether a freshly loaded document is still waiting for its first feed message before being shown.</summary>
    private static bool _awaitingFirstBind;
    private static long _bindDeadline;

    /// <summary>
    /// Whether anything has been drawn for the current document yet, so the FIRST paint can be announced.
    ///
    /// <para>Announced because the first paint is no longer a consequence of loading the page — it waits for the feed
    /// to bind. Anything checking that a site rendered has to be able to observe the moment it did rather than assume
    /// it followed the fetch, which is precisely the assumption that made the browser gate racy.</para>
    /// </summary>
    private static bool _painted;

    [LibraryImport("js", EntryPoint = "cupri_present")]
    private static partial void Present(IntPtr rgba, int width, int height);

    [LibraryImport("js", EntryPoint = "cupri_canvas_width")]
    private static partial int CanvasWidth();

    [LibraryImport("js", EntryPoint = "cupri_canvas_height")]
    private static partial int CanvasHeight();

    [LibraryImport("js", EntryPoint = "cupri_canvas_scale")]
    private static partial float CanvasScale();

    /// <summary>
    /// The size a site is authored for, and the reference hybrid zoom fits against.
    ///
    /// <para>A desktop CupriFace app declares this on its <c>CupriApp</c>; an arbitrary L2 site has no way to say
    /// it, so the client has to assume one. 1024×768 is the assumption — wide enough that a page laid out for a
    /// normal window is not scaled up, tall enough that fitting the height is a meaningful constraint rather than a
    /// permanent shrink. A site declaring its own would be better, and is worth a convention later.</para>
    /// </summary>
    private const float DesignWidth = 1024f;
    private const float DesignHeight = 768f;

    /// <summary>
    /// Loads the fetched document and paints it once.
    ///
    /// <para>No stylesheet parameter: an L2 document carries its own <c>&lt;style&gt;</c>, which CupriFace collects
    /// from the DOM. That keeps a page to a single Oracle consult — linking a stylesheet would cost a second full
    /// round trip for one page.</para>
    ///
    /// <para>Fonts are registered from embedded resources because wasm has no system font list to fall back on:
    /// without them text lays out and paints as nothing, which reads as a broken renderer rather than a missing
    /// asset.</para>
    /// </summary>
    public static void Show(string html)
    {
        _document = CupriDocument.Load(html);

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
            _document.LoadFont(memory.ToArray());
        }

        // NOT painted here, deliberately.
        //
        // A Document-tier page arrives as a TEMPLATE: its live values are {{ }} placeholders that only become text
        // when the first feed message binds. Painting on arrival therefore shows the raw template — "{{ node.site }}"
        // scattered across the page — until the snapshot lands a moment later. On a reconnect that is worse than
        // ugly, because the canvas already holds a perfectly good render of the same site and the flash replaces it
        // with something that looks broken.
        //
        // So the first paint waits for the first bind. The deadline is the safety net: a site with no feed at all
        // would otherwise never appear, so once it passes the template is painted as-is — which is exactly the old
        // behaviour, just no longer the common case.
        _awaitingFirstBind = true;
        _painted = false;
        _bindDeadline = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 3 / 4);   // 750ms
    }

    /// <summary>
    /// Applies a feed message to the document and repaints.
    ///
    /// <para>This is what makes a Document-tier site live. It has no JavaScript engine to run, so a snapshot or
    /// update cannot be handled by the page itself — the client binds the payload and asks the engine to rebuild.</para>
    /// </summary>
    public static void Update(ReadOnlySpan<byte> payload)
    {
        if (_document is null) return;

        var model = FeedModel.Parse(payload);
        if (model is null)
        {
            Console.WriteLine("[cupri] feed message was not a JSON object; ignored");
            return;
        }

        _document.Bind(model);
        _document.Refresh();

        // The document now has real values in it, so it is safe to show. On a reconnect this is the moment the old
        // frame is replaced — by a complete one, rather than by a template mid-bind.
        _awaitingFirstBind = false;
        Paint();
    }

    /// <summary>
    /// Advances the document's animation clock and repaints if anything moved. Called once per frame from the pump.
    ///
    /// <para>Without this, CSS animations and transitions never run: the engine does not animate on its own, it
    /// animates when a host tells it what time it is. A site whose markup declares <c>@keyframes</c> would render
    /// its first frame and then sit frozen — which is worse than having no animation, because a page that looks
    /// alive and is not is exactly how a stalled feed disguises itself.</para>
    ///
    /// <para>The repaint is conditional on the engine's own answer rather than unconditional. A full document
    /// repaint at 60 Hz on a phone is real battery for nothing when the page is static, and the common case for a
    /// document-tier site <i>is</i> static — so an idle page costs one cheap call per frame and no pixels.</para>
    /// </summary>
    public static void Animate(double seconds)
    {
        if (_document is null) return;

        // A resize is a repaint reason in its own right, and it is checked here because the frame pump is already
        // the one thing running every frame — no JS-to-managed signal needed, and no listener that can fire while
        // the module is still booting.
        //
        // Without it the page does not re-render at all when the window changes: the canvas keeps the backing store
        // it was given at boot and the browser simply SCALES that bitmap to the new element size, so text and charts
        // stretch and blur. CupriFace itself re-lays-out at whatever size Render is handed — verified by rendering
        // the same document at two widths and watching the cards rewrap — so this was never an engine limit, only a
        // host that never told it the size had changed.
        var width = CanvasWidth();
        var height = CanvasHeight();
        var resized = width != _paintedWidth || height != _paintedHeight;

        // The safety net for a document whose feed never arrives: show it anyway rather than leave the visitor on a
        // blank canvas — or, worse, on the previous site's page — indefinitely.
        if (_awaitingFirstBind)
        {
            if (Stopwatch.GetTimestamp() < _bindDeadline) return;

            Console.WriteLine("[cupri] no feed message within 750ms — painting the page unbound");
            _awaitingFirstBind = false;
            Paint();
            return;
        }

        if (_document.Animate(seconds) || resized) Paint();
    }

    /// <summary>Renders the current document to the canvas at its present size.</summary>
    public static void Paint()
    {
        if (_document is null) return;

        var width = CanvasWidth();
        var height = CanvasHeight();
        if (width <= 0 || height <= 0) return;

        // Recorded before the work, not after: a mid-resize failure must not leave this claiming the new size was
        // painted, or the next frame sees no change and the canvas stays stale until something else moves.
        _paintedWidth = width;
        _paintedHeight = height;

        // Skia composites in PREMULTIPLIED alpha; the browser's ImageData wants STRAIGHT alpha. Handing premultiplied
        // pixels straight to putImageData is the classic way to get a picture that is subtly, inexplicably wrong on
        // anything translucent — so the read-back converts.
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

        // The HOST clears the page background — CupriFace paints the document onto whatever it is given, and a fresh
        // Skia surface is transparent. Without this the site composited onto the client's own dark chrome: dark text
        // on a dark backdrop, technically rendered and practically unreadable.
        //
        // White because that is what a browser shows a page that asks for nothing. A site that wants otherwise says
        // so in its own CSS and paints over this.
        surface.Canvas.Clear(SKColors.White);

        // HYBRID ZOOM, the same policy CupriFace's own Showcase calls by that name: fit the tighter axis and let the
        // longer one reflow. The document is laid out at a LOGICAL size and drawn at a scale, rather than laid out
        // at whatever pixel size the canvas happens to be.
        //
        // Two things are folded into one scale here, and both matter:
        //
        //   devicePixelRatio — without it the document lays out in DEVICE pixels, so on a 2x display a page reflows
        //   as though the window were twice as wide and every glyph ends up half its intended physical size. It has
        //   been wrong since the client was written and stayed invisible because headless Chromium reports 1.
        //
        //   the zoom itself — min(cssWidth/DesignWidth, cssHeight/DesignHeight), so a viewport smaller than the
        //   design size scales the page down to fit instead of clipping it, and a larger one scales it up.
        var density = CanvasScale();
        if (density <= 0) density = 1f;

        var present = density * Zoom();
        surface.Canvas.Scale(present);

        // Render in LOGICAL units. Skia maps them onto the device pixels above, so text is laid out at the size the
        // author meant and still painted at the display's real resolution.
        _document.Render(surface.Canvas, width / present, height / present);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        var straight = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[straight.BytesSize];

        fixed (byte* buffer = pixels)
        {
            if (!image.ReadPixels(straight, (IntPtr)buffer, straight.RowBytes, 0, 0))
            {
                Console.WriteLine("[cupri] could not read back the rendered pixels");
                return;
            }

            Present((IntPtr)buffer, width, height);
        }

        // Once per document, after pixels have actually reached the canvas. Not on every frame: this runs at display
        // rate while anything is animating, and a per-frame line would bury everything else the client says.
        if (!_painted)
        {
            _painted = true;
            Console.WriteLine("[cupri] painted");
        }
    }

    /// <summary>
    /// The hybrid-zoom factor the document is currently laid out at: fit the tighter axis, let the longer reflow.
    ///
    /// <para>Shared with the input path deliberately. The document is laid out in LOGICAL units and drawn at a
    /// scale, so a pointer position in CSS pixels means nothing to it until it is divided by this. Computing it in
    /// two places would let painting and hit-testing drift apart — and the symptom of that is the worst kind: a
    /// page that looks right and answers clicks a few pixels away from where they landed.</para>
    /// </summary>
    private static float Zoom()
    {
        var density = CanvasScale();
        if (density <= 0) density = 1f;

        var cssWidth = CanvasWidth() / density;
        var cssHeight = CanvasHeight() / density;
        if (cssWidth <= 0 || cssHeight <= 0) return 1f;

        return Math.Clamp(Math.Min(cssWidth / DesignWidth, cssHeight / DesignHeight), 0.25f, 4f);
    }

    /// <summary>
    /// Whether the document is ready to be interacted with.
    ///
    /// <para>Input before the first paint is dropped rather than queued. The page is still a template at that point,
    /// so a click would hit-test against placeholder text and land somewhere the visitor never saw — and they cannot
    /// have aimed at something that was not on screen.</para>
    /// </summary>
    private static bool Ready => _document is not null && !_awaitingFirstBind;

    /// <summary>Repaints if the document says the frame changed. Every dispatch below funnels through here.</summary>
    private static void Settle(bool changed)
    {
        if (changed) Paint();
    }

    public static void PointerMove(float cssX, float cssY)
    {
        if (!Ready) return;

        var zoom = Zoom();
        float x = cssX / zoom, y = cssY / zoom;

        Settle(_document!.DispatchPointerMove(x, y));

        // The cursor is the only affordance a canvas-painted site has. Without it a link is indistinguishable from
        // a paragraph until you click it, which is the difference between a page that feels interactive and one
        // that feels like a screenshot.
        try
        {
            BrowserInput.SetCursor(CupriDocument.CursorCss(_document.CursorAt(x, y)));
        }
        catch (Exception ex)
        {
            // Never fatal: a cursor is a nicety, and a hit test that throws must not stop the pointer working.
            Console.WriteLine($"[cupri] cursor: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void PointerDown(float cssX, float cssY)
    {
        if (!Ready) return;
        var zoom = Zoom();
        Settle(_document!.DispatchPointer(0, PointerPhase.Down, cssX / zoom, cssY / zoom));
    }

    public static void PointerUp(float cssX, float cssY)
    {
        if (!Ready) return;
        var zoom = Zoom();
        Settle(_document!.DispatchPointerUp(cssX / zoom, cssY / zoom));
    }

    public static void Click(float cssX, float cssY, int clickCount)
    {
        if (!Ready) return;
        var zoom = Zoom();
        Settle(_document!.DispatchClick(cssX / zoom, cssY / zoom, clickCount));
    }

    /// <summary>
    /// Scrolling, which is what the wheel is for on a page taller than the canvas.
    ///
    /// <para>The deltas are NOT divided by the zoom. They are a distance the visitor asked the content to move, not
    /// a position in it — dividing would make a page scroll further the more it had been shrunk to fit, which is
    /// exactly backwards from how it feels to use.</para>
    /// </summary>
    public static void Wheel(float cssX, float cssY, float deltaY, float deltaX)
    {
        if (!Ready) return;
        var zoom = Zoom();
        Settle(_document!.DispatchWheel(cssX / zoom, cssY / zoom, deltaY, deltaX));
    }

    public static void Key(string text, int editKey, int mods)
    {
        if (!Ready) return;
        Settle(_document!.DispatchKey(text, (EditKey)editKey, (KeyMods)mods));
    }
}
