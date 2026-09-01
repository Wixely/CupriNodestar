using System.Runtime.InteropServices;
using CupriFace.Web;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// The seam between CupriFace's browser host and this client's own JavaScript.
///
/// <para><b>Why this exists rather than the host's own JS.</b> <c>CupriFace.Web.NativeAot</c> ships an
/// <c>imports.js</c>/<c>main.js</c> pair that owns the canvas, the input listeners and the frame loop. This client
/// cannot hand those over wholesale: its loop is also the async pump that NativeAOT-LLVM wasm has no event loop for
/// (see <see cref="BrowserLoop"/>), and its page carries a connection panel, a link bar and a back button that the
/// host knows nothing about. So the host is driven from managed code — <c>WebHostCore.Init</c> and
/// <c>WebHostCore.Tick</c> — and everything it wants to SAY to the page arrives here.</para>
///
/// <para>Which means the JS on the other side is the JS this project already had. <c>cupri_present</c>,
/// <c>cupri_set_cursor</c> and <c>cupri_suggest_link</c> were written for the hand-rolled renderer and are reached
/// unchanged; only <c>cupri_aria</c> is new, because nothing here ever published an accessibility tree.</para>
///
/// <para>The host's own <c>js_*</c> symbols are still linked — its <c>buildTransitive</c> targets add them — and
/// simply go uncalled. They do not collide with this project's <c>cupri_*</c> names.</para>
/// </summary>
internal sealed unsafe partial class CupriFaceBridge : IWebBridge
{
    [LibraryImport("js", EntryPoint = "cupri_present")]
    private static partial void PresentJs(IntPtr rgba, int width, int height, int dx, int dy, int dw, int dh);

    [LibraryImport("js", EntryPoint = "cupri_set_cursor", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void SetCursorJs(string cursor);

    [LibraryImport("js", EntryPoint = "cupri_aria", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void PublishAriaJs(string html);

    [LibraryImport("js", EntryPoint = "cupri_set_key_capture")]
    private static partial void SetKeyCaptureJs(int capture);

    [LibraryImport("js", EntryPoint = "cupri_set_text_input")]
    private static partial void SetTextInputJs(int focused, int numeric, int multiline, double x, double y);

    [LibraryImport("js", EntryPoint = "cupri_clipboard_write", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void ClipboardWriteJs(string text);

    [LibraryImport("js", EntryPoint = "cupri_clipboard_paste")]
    private static partial void ClipboardPasteJs();

    /// <summary>
    /// The painted frame, blitted to the canvas.
    ///
    /// <para><b><c>pixels</c> is the WHOLE surface, not the damaged part</b> — measured, by ticking a document with
    /// a small hover change and comparing <c>byteCount</c> against both products: an 82x34 damage region still
    /// arrived as 1,920,000 bytes for an 800x600 surface. So the rectangle says which part CHANGED, and a blit of
    /// the entire buffer is correct, merely wasteful.</para>
    ///
    /// <para>So the rectangle is handed straight through to <c>putImageData</c>'s dirty-rectangle arguments, which
    /// upload only the part that changed. A hover on one link repaints a few thousand pixels rather than the whole
    /// canvas — every frame, on every device. The page falls back to a full blit when the damage IS the surface,
    /// which is what a first paint, a resize and a navigation all are.</para>
    /// </summary>
    public void Present(IntPtr pixels, int byteCount, int width, int height, int dx, int dy, int dw, int dh)
        => PresentJs(pixels, width, height, dx, dy, dw, dh);

    /// <summary>
    /// The accessibility tree, as HTML for the page to mirror into a live region.
    ///
    /// <para>This client published nothing of the sort before, so a screen reader met a canvas and found an empty
    /// page. It is the single largest thing adopting the host buys, and it costs one JS function.</para>
    /// </summary>
    public void PublishAria(string html) => PublishAriaJs(html);

    public void SetCursor(string cssCursor) => SetCursorJs(cssCursor);

    /// <summary>
    /// A link the host decided belongs to a browser rather than to the app.
    ///
    /// <para>Ignored, and that is the whole of it: this client has no browser to hand an external URL to and no
    /// business opening one. It hears about EVERY link — including this one — through
    /// <c>CupriDocument.Navigated</c>, which is the engine's own event and carries relative and custom-scheme
    /// hrefs that never reach here at all. Acting on both would mean two paths to one decision.</para>
    /// </summary>
    public void Navigate(string href) { }

    /// <summary>
    /// Where the document's caret is, and whether it has one.
    ///
    /// <para>Two things happen here. The key capture flag decides whether the page claims keystrokes or leaves
    /// them to the browser. And the position moves an offscreen editable field to the caret, which is what lets an
    /// input method attach: composition needs a real element, and an IME reads that element's position to place
    /// its candidate list. A field left at the origin puts the candidates in the corner of the screen.</para>
    ///
    /// <para>The numeric hint reaches <c>inputmode</c>, so a phone offers digits for a numeric field rather than a
    /// full alphabet. <c>multiline</c> is carried but unused: the offscreen field is a textarea either way, and
    /// nothing about how this client submits depends on it.</para>
    /// </summary>
    public void SetTextInput(bool focused, bool numeric, bool multiline, double x, double y)
    {
        SetKeyCaptureJs(focused ? 1 : 0);
        SetTextInputJs(focused ? 1 : 0, numeric ? 1 : 0, multiline ? 1 : 0, x, y);
    }

    /// <summary>Puts text on the system clipboard. Asynchronous on the page's side; nothing here waits for it.</summary>
    public void ClipboardWrite(string text) => ClipboardWriteJs(text);

    /// <summary>
    /// The document asking for the clipboard's contents.
    ///
    /// <para>Raised by the engine's own paths — a context menu — rather than by Ctrl+V, which the offscreen field
    /// handles natively: it has focus, so the browser pastes into it, that raises an input event, and the text
    /// reaches the document the way all inserted text does. Reading the clipboard needs permission and pasting
    /// into a focused field does not, so the cheaper path is left to do the common case.</para>
    /// </summary>
    public void ClipboardPaste() => ClipboardPasteJs();

    // ---- Not wired yet, and silent on purpose --------------------------------------------------------------
    //
    // Every one of these is a capability this client did not have before the host arrived, so leaving them empty
    // loses nothing that worked. They are listed rather than thrown from: a document that opens a video must not
    // take the page down because the client has no video surface for it.

    public void SetFavicon(string dataUri) { }
    public void VideoClose(int id) { }
    public void VideoLoop(int id, bool loop) { }
    public void VideoMuted(int id, bool muted) { }
    public void VideoOpen(int id, string src) { }
    public void VideoOpenBytes(int id, byte[] bytes) { }
    public void VideoPause(int id) { }
    public void VideoPlay(int id) { }
    public void VideoRect(int id, double x, double y, double w, double h, double clipTop, double clipRight,
        double clipBottom, double clipLeft, bool visible, string fit,
        double a, double b, double c, double d, double e, double f) { }
    public void VideoSeek(int id, double seconds) { }
    public void VideoVolume(int id, double volume) { }
    public void WindowCommand(int command) { }
}
