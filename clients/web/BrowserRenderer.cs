using System.Reflection;
using System.Runtime.InteropServices;
using CupriFace;
using SkiaSharp;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// Paints an L2 site into the page's canvas with CupriFace — HTML and CSS drawn by the engine itself, with no browser
/// engine and no JavaScript engine, so a hostile site has no script runtime to reach for.
/// </summary>
internal static unsafe partial class BrowserRenderer
{
    private static CupriDocument? _document;

    [LibraryImport("js", EntryPoint = "cupri_present")]
    private static partial void Present(IntPtr rgba, int width, int height);

    [LibraryImport("js", EntryPoint = "cupri_canvas_width")]
    private static partial int CanvasWidth();

    [LibraryImport("js", EntryPoint = "cupri_canvas_height")]
    private static partial int CanvasHeight();

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

        Paint();
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
        Paint();
    }

    /// <summary>Renders the current document to the canvas at its present size.</summary>
    public static void Paint()
    {
        if (_document is null) return;

        var width = CanvasWidth();
        var height = CanvasHeight();
        if (width <= 0 || height <= 0) return;

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

        _document.Render(surface.Canvas, width, height);
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
    }
}
