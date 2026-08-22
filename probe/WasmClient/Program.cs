using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;

// PHASE-2 PROBE — does the FULL client path survive NativeAOT-LLVM + full trimming for browser-wasm?
//
// WasmCrypto answered the crypto question. The open one is CupriNet.Hosting: the Pilgrim side is a node-level API, so
// it pulls in the Concordance overlay, Traversal and Persistence — machinery a browser client does not need but
// cannot currently sidestep.

var suite = new BouncyCastleSuite();
Console.WriteLine($"suite={suite.Name} secure={suite.IsSecure}");

// The rite codecs a client encodes and decodes on every visit.
var request = OracleRequest.Get("/index.html");
Console.WriteLine($"oracle request bytes={OracleCodec.EncodeRequest(request).Length}");

var attend = new AuspiceFrame { Kind = AuspiceFrameKind.Attend, Topic = "overlay" };
var attendBytes = AuspiceCodec.Encode(attend);
Console.WriteLine($"auspice attend bytes={attendBytes.Length} kind={AuspiceCodec.Decode(attendBytes).Kind}");

// A Signet is what a Pilgrim pins during the Pilgrimage, so its bech32 rendering is on the client path too.
var signet = Signet.Generate(suite);
Console.WriteLine($"signet address prefix={signet.Address[..6]}");

// The part the trimmer must be forced to keep. These calls are never executed — a browser has no sockets, and
// CupriNode.CreateAsync would bind one — but the condition depends on runtime input, so the trimmer cannot fold it
// away and prove the branch dead. That makes the Pilgrim path genuinely compiled, trimmed and linked, which is what
// this probe is asking about. Executing it is not the question; surviving the toolchain is.
if (args.Length > 99)
{
    var node = await CupriNode.CreateAsync(new CupriNodeOptions
    {
        Concordium = "probe",
        Suite = suite,
    });

    // The Pilgrim half: dial a Shrine over a vessel and pin its Signet, then both site rites.
    var vessel = await TcpVessel.ConnectAsync("127.0.0.1", 1, cancellationToken: default);
    await using var shrine = await node.PilgrimageOverVesselAsync(vessel, signet.Sigil);
    var page = await shrine.ConsultAsync(request);
    Console.WriteLine(page.Status);

    await foreach (var frame in shrine.AttendAsync("overlay"))
        Console.WriteLine(frame.Kind);

    // The host half too, since a Nodestar links both into one binary today.
    node.HostShrine(signet, new StaticFileOracleHandler("."));
    Console.WriteLine(node.ShrineAddress);
}

Console.WriteLine("PROBE OK");
