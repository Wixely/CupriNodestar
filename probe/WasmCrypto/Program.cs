using CupriNet.Alembic;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Rites;

// Every operation below is REACHED and its result printed. That matters: a probe that only referenced the types would
// prove nothing, because TrimMode=full would strip the unreached ones and the link would succeed for the wrong reason.
// If this publishes, the trimmer kept the code paths; if it also runs, the primitives work under wasm.

var suite = new BouncyCastleSuite();
Console.WriteLine($"suite={suite.Name} secure={suite.IsSecure}");

// Identity: Ed25519 keygen + sign + verify — the Seal a Pilgrim mints per visit under Noise XX.
var seal = suite.GenerateSeal();
var signer = suite.CreateSigner(seal.PrivateKey);
var message = "the pilgrim attends the shrine"u8.ToArray();
var signature = signer.Sign(message);
var verified = suite.Verifier.Verify(message, signature, seal.PublicKey);
Console.WriteLine($"ed25519 sign+verify={verified}");

// Hash + KDF: the rest of the Noise/Veil pipeline. HKDF is what turns handshake output into session keys.
var digest = suite.Hash.Sha256(message);
var digest512 = suite.Hash.Sha512(message);
var derived = suite.Kdf.DeriveKey(digest, salt: digest512.AsSpan(0, 32), info: "nodestar-probe"u8, length: 32);
Console.WriteLine($"sha256={digest.Length}B sha512={digest512.Length}B hkdf={derived.Length}B");

// The AEAD and key-agreement providers a Pilgrimage leans on.
Console.WriteLine($"aead={suite.Aead.GetType().Name} agreement={suite.Agreement.GetType().Name}");

// A rite codec round-trip: the frame format the browser client must encode and decode.
var frame = new AuspiceFrame { Kind = AuspiceFrameKind.Snapshot, Topic = "overlay", Payload = digest };
var encoded = AuspiceCodec.Encode(frame);
var decoded = AuspiceCodec.Decode(encoded);
Console.WriteLine($"auspice roundtrip kind={decoded.Kind} topic={decoded.Topic} bytes={encoded.Length}");

var request = OracleRequest.Get("/index.html");
var oracleBytes = OracleCodec.EncodeRequest(request);
Console.WriteLine($"oracle request encoded bytes={oracleBytes.Length}");

Console.WriteLine("PROBE OK");
