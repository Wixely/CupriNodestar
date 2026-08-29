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

    /// <summary>Reused across frames: this runs at display rate, and a fresh array per frame is pure garbage.</summary>
    private static readonly byte[] Buffer = new byte[MaxInputBytes];

    [LibraryImport("js", EntryPoint = "cupri_take_input")]
    private static partial int TakeInput(IntPtr buffer, int capacity);

    [LibraryImport("js", EntryPoint = "cupri_set_cursor")]
    private static partial void SetCursorCore(IntPtr utf8);

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
                case Down: BrowserRenderer.PointerDown(x, y); break;
                case Up: BrowserRenderer.PointerUp(x, y); break;
                case Click: BrowserRenderer.Click(x, y, i0 <= 0 ? 1 : i0); break;
                case Wheel: BrowserRenderer.Wheel(x, y, a, b); break;
                case Key: BrowserRenderer.Key(text, i0, i1); break;
            }

            // The text is NUL-terminated on the wire and the whole record is padded to four bytes, which is what
            // keeps every field aligned for the reads above.
            offset += 32 + ((textBytes + 1 + 3) & ~3);
        }
    }

    /// <summary>Tells the page which cursor belongs under the pointer.</summary>
    public static void SetCursor(string css)
    {
        var utf8 = Encoding.UTF8.GetBytes(css + '\0');
        fixed (byte* pointer = utf8)
            SetCursorCore((IntPtr)pointer);
    }
}
