using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// Carries the visitor's pointer, wheel and keyboard into the document.
///
/// <para><b>Why the client owns this at all.</b> An L2 site is painted to a canvas, so the browser has no DOM for it
/// to deliver events to — there is no element under the cursor as far as the page is concerned, only pixels. Without
/// this the site is a live picture: it renders, it updates, and nothing in it can be clicked, scrolled or typed
/// into. Everything interactive CupriFace supports was unreachable for exactly one missing piece, which is this.</para>
///
/// <para><b>Polled, not pushed.</b> Events queue in JavaScript and are drained once per frame, for the same reason
/// inbound frames are: calling into wasm from a DOM handler re-enters the runtime at an arbitrary point, and the
/// renderer is mid-frame often enough for that to matter. Draining between frames keeps the managed side in control
/// of when a click is handled.</para>
///
/// <para><b>Keys reach a document that can now accept them.</b> That was not true until recently: measured against
/// CupriFace 0.3.0 and 0.5.0, an <c>&lt;input&gt;</c> in ordinary markup was not focusable and <c>DispatchKey</c>
/// answered false for everything, so this path was correct and unreachable. As of 0.12.0 a
/// <c>&lt;cupri-textfield&gt;</c> takes focus from a click and reports a caret, which is also what an input method
/// needs to attach to. <see cref="SetKeyCapture"/> still gates whether the page claims a keystroke, so a document
/// with nothing focused leaves the browser's own key behaviour alone.</para>
/// </summary>
internal static unsafe partial class BrowserInput
{
    /// <summary>One frame's worth. A record is 32 bytes plus its text, and moves coalesce, so this is generous.</summary>
    private const int MaxInputBytes = 8192;

    private const int Move = 1;
    private const int Down = 2;
    private const int Up = 3;
    private const int Click = 4;
    private const int Wheel = 5;
    private const int Key = 6;

    // Touch, kept distinct from the pointer kinds because it is a different stream with different meaning: a
    // finger has an identity (so several can be followed at once) and a time (so the renderer's recogniser can
    // tell a flick from a drag). The page never sends both for one gesture — see the note on `pointerType` there.
    private const int TouchDown = 7;
    private const int TouchMove = 8;
    private const int TouchUp = 9;
    private const int TouchCancel = 10;

    // Text, as an input method produces it. Composition is not typing: a visitor picking Japanese characters sends
    // a running "this is what I have so far" (Composing) and then one "this is what I meant" (Composed), and only
    // the second is real text. Inserted covers everything that arrives already settled — ordinary typing through
    // the offscreen field, a paste, a phone keyboard completing a word.
    private const int Composing = 11;
    private const int Composed = 12;
    private const int Inserted = 13;

    // The page asking the document for its selection. Copy and cut are the client's job because the engine does
    // not claim the chords itself — measured: KeyChord("c", Ctrl) answers false and no bridge call follows.
    private const int Copy = 14;
    private const int Cut = 15;

    /// <summary>Reused across frames: this runs at display rate, and a fresh array per frame is pure garbage.</summary>
    private static readonly byte[] Buffer = new byte[MaxInputBytes];

    [LibraryImport("js", EntryPoint = "cupri_take_input")]
    private static partial int TakeInput(IntPtr buffer, int capacity);

    [LibraryImport("js", EntryPoint = "cupri_set_cursor")]
    private static partial void SetCursorCore(IntPtr utf8);

    [LibraryImport("js", EntryPoint = "cupri_set_key_capture")]
    private static partial void SetKeyCaptureCore(int capture);

    /// <summary>Last reported value, so an unchanged state costs nothing per frame.</summary>
    private static bool _keyCapture;

    /// <summary>
    /// Tells the page whether the document has somewhere to put a keystroke.
    ///
    /// <para>It decides whether the client claims a key or leaves it to the browser. Claiming keys a document does
    /// nothing with costs the visitor real behaviour — Tab stops moving focus out of the canvas, space stops
    /// scrolling — for no gain at all.</para>
    /// </summary>
    public static void SetKeyCapture(bool capture)
    {
        if (capture == _keyCapture) return;
        _keyCapture = capture;
        SetKeyCaptureCore(capture ? 1 : 0);
    }

    /// <summary>Drains a frame's input into the document. Called from the pump, before the frame is drawn.</summary>
    public static void Pump()
    {
        int length;
        fixed (byte* pointer = Buffer)
            length = TakeInput((IntPtr)pointer, Buffer.Length);

        // 0 is the common case by a wide margin — most frames have no input at all — and -1 means the queue did not
        // fit and has been kept, so the next frame gets it rather than the visitor losing a click.
        if (length <= 0) return;

        var span = Buffer.AsSpan(0, length);
        var offset = 0;

        while (offset + 32 <= span.Length)
        {
            var record = span[offset..];
            var kind = BinaryPrimitives.ReadInt32LittleEndian(record);
            var i0 = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            var i1 = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            var x = BinaryPrimitives.ReadSingleLittleEndian(record[12..]);
            var y = BinaryPrimitives.ReadSingleLittleEndian(record[16..]);
            var a = BinaryPrimitives.ReadSingleLittleEndian(record[20..]);
            var b = BinaryPrimitives.ReadSingleLittleEndian(record[24..]);
            var textBytes = BinaryPrimitives.ReadInt32LittleEndian(record[28..]);

            // A record claiming more text than arrived means the two sides disagree about the layout. Stopping is
            // the honest response: continuing would read the next record from the middle of this one and deliver
            // convincing nonsense to the document.
            if (textBytes < 0 || 32 + textBytes > record.Length)
            {
                Console.WriteLine("[cupri] malformed input record; the rest of the frame is dropped");
                return;
            }

            var text = textBytes > 0 ? Encoding.UTF8.GetString(record.Slice(32, textBytes)) : string.Empty;

            switch (kind)
            {
                case Move: BrowserRenderer.PointerMove(x, y); break;

                // The click count rides on the PRESS now. CupriFace's host raises a click from the press and the
                // release itself — verified, exactly once — so the page no longer sends a separate click event and
                // there is no Click case here. Forwarding one as well would activate every link twice.
                case Down: BrowserRenderer.PointerDown(x, y, i0); break;
                case Up: BrowserRenderer.PointerUp(x, y); break;
                case Wheel: BrowserRenderer.Wheel(x, y, a, b); break;
                case Key: BrowserRenderer.Key(text, i0, i1); break;

                // `a` carries the event's own timestamp rather than the clock read on arrival: the recogniser
                // measures velocity with it, and the difference between when a finger moved and when this frame
                // drained the queue is exactly the error that would make a flick look like a drag.
                case TouchDown: BrowserRenderer.TouchDown(i0, x, y, a); break;
                case TouchMove: BrowserRenderer.TouchMove(i0, x, y, a); break;
                case TouchUp: BrowserRenderer.TouchUp(i0, x, y, a); break;
                case TouchCancel: BrowserRenderer.TouchCancel(i0, a); break;

                case Composing: BrowserRenderer.Composing(text); break;
                case Composed: BrowserRenderer.Composed(text); break;
                case Inserted: BrowserRenderer.Inserted(text); break;
                case Copy: BrowserRenderer.Copy(); break;
                case Cut: BrowserRenderer.Cut(); break;
            }

            // The text is NUL-terminated on the wire and the whole record is padded to four bytes, which is what
            // keeps every field aligned for the reads above.
            offset += 32 + ((textBytes + 1 + 3) & ~3);
        }

        // Asked after the frame's input, because a click is what focuses a field in the first place.
        SetKeyCapture(BrowserRenderer.WantsKeys);
    }

    /// <summary>Tells the page which cursor belongs under the pointer.</summary>
    public static void SetCursor(string css)
    {
        var utf8 = Encoding.UTF8.GetBytes(css + '\0');
        fixed (byte* pointer = utf8)
            SetCursorCore((IntPtr)pointer);
    }
}
