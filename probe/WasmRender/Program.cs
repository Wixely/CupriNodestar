using CupriFace;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Rites;
using SkiaSharp;

// PHASE-2 PROBE — the browser client's two halves in one binary.
//
// CupriFace ships its own WebLlvm host, so the renderer is known to work alone; CupriNet's stack was proven by the
// two probes beside this one. The open question is the COMBINATION — and the number a visitor actually downloads.

var suite = new BouncyCastleSuite();
var signet = Signet.Generate(suite);
Console.WriteLine($"cuprinet: suite={suite.Name} signet={signet.Address[..10]}…");

// A rite codec round-trip, so the protocol half is genuinely reached and not trimmed as dead.
var frame = new AuspiceFrame { Kind = AuspiceFrameKind.Snapshot, Topic = "overlay", Payload = "{}"u8.ToArray() };
Console.WriteLine($"cuprinet: auspice roundtrip kind={AuspiceCodec.Decode(AuspiceCodec.Encode(frame)).Kind}");

// The renderer half: parse HTML, apply CSS, lay out, and paint to a real surface. This is the whole
// parse → style → layout → paint pipeline, which is what a Document-tier site needs.
const string Html = """
    <body>
      <h1 id="title">Constellation</h1>
      <p class="peer">cupri1abc… <span class="meta">1 endpoint</span></p>
      <p class="peer">cupri1def… <span class="meta">2 endpoints</span></p>
    </body>
    """;

const string Css = """
    body { background: #141416; color: #e8e8ea; font-size: 16px; padding: 12px; }
    h1 { font-size: 22px; color: #9d86ff; }
    .peer { padding: 6px 0; }
    .meta { color: #9a9aa2; font-size: 12px; }
    """;

var document = CupriDocument.Load(Html, Css);

using var surface = SKSurface.Create(new SKImageInfo(480, 240, SKColorType.Rgba8888, SKAlphaType.Premul));
document.Render(surface.Canvas, 480, 240);
surface.Canvas.Flush();

// Read a pixel back. Rendering that "succeeds" without touching the framebuffer would prove nothing, so this
// confirms the paint actually landed — the background colour from the stylesheet above.
using var image = surface.Snapshot();
using var pixels = image.PeekPixels();
var pixel = pixels.GetPixelColor(5, 5);
Console.WriteLine($"cupriface: painted pixel at (5,5) = #{pixel.Red:X2}{pixel.Green:X2}{pixel.Blue:X2}");

// Encoding exercises Skia's codec path, which is the part that needs SUPPORT_LONGJMP at link time.
using var png = image.Encode(SKEncodedImageFormat.Png, 90);
Console.WriteLine($"cupriface: encoded png bytes={png.Size}");

Console.WriteLine("PROBE OK");
