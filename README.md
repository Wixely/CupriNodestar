# Nodestar

**A batteries-included package for hosting a [CupriNet](https://github.com/Wixely/CupriNet) node that also serves an L2
site — a CupriNode + a clearnet HTTP front + an L2 layer of your choosing, from a few lines of code.**

A Nodestar keeps every **Lodestar** duty (overlay keep-alive, WebRTC entry point, Tor, Ferryman) and adds what Lodestar
structurally lacks: it **hosts content on L2**. Add the package, plug in what you serve (static files, a request/response
handler, a live stream, or a raw session), and you get the node, the web front (an intonation page + a served on-ramp
client, HTTPS / reverse-proxy / IIS / Tor-aware), and the L2 hosting wired together.

```
dotnet new install CupriNet.Nodestar.Templates
dotnet new nodestar-site -n MySite --network my.network
cd MySite && dotnet run
```

That is a running site: a `cupri1…` address, a page served to browsers over WebRTC and to everything else over
HTTP, and a live feed bound into it. Open <http://localhost:8080/_nodestar> for the link and QR.

Or by hand:

```csharp
using CupriNet.Nodestar;

var builder = NodestarApplication.CreateBuilder(args);
builder.Node.Concordium = "example.chat";
builder.Site.ServeStaticFiles("l2-wwwroot");   // ...or .Serve(...) / .Feed(...) / .OnSession(...)
var app = builder.Build();
await app.RunAsync();
```

## How it works

The node serves one thing over HTTPS: a **web client** — the real CupriNet client stack compiled to **WebAssembly**,
seeded with this node's signed link — which dials back over **WebRTC** with no signalling server and opens an
authenticated L2 session. Because it's the same code the node runs, there's no second protocol implementation and no
drift. That bootstrap is the only HTTP the browser performs. The client then fetches the *actual* site over L2 and
renders it with [CupriFace](https://github.com/Wixely/CupriFace) — HTML + CSS painted to a canvas with **no browser
engine and no JavaScript engine**, so a hostile site has no script runtime to reach for. A site may also ship compiled
C# behaviour, run in a capability-scoped region. Live data arrives over CupriNet's
**Auspice** rite on the same DataChannel — attend a named feed, get a snapshot, then updates pushed indefinitely. Like
WebSockets, but entirely over WebRTC. **No WebSockets, no SSE, no polling.** For deployments where the browser can't
reach a WebRTC UDP endpoint (Cloudflare tunnel, Tor), the node renders a static snapshot of the site to plain HTML
server-side instead.

The split is deliberate: **the client is a clearnet asset** — an ordinary web page from an ordinary HTTP server, with no
overlay address, cacheable and swappable like any other static file — and **the L2 site is what arrives over WebRTC**.

It builds on CupriNet's core **Shrine** capability (serving content over L2, addressed by a self-authenticating
`cupri1…` key) — see [`design/nodestar.md`](design/nodestar.md) for the full design, and CupriNet's `design/shrines.md`
for the protocol.

## What a site may serve, and how big it may be

A site is served over L2, and **every message a rite carries is capped at 192 KiB**. That is not a Nodestar
preference — it is the size a browser's SCTP association will carry, and it applies wherever a browser can reach,
which for a site is everywhere.

| You are sending | Cap | If you exceed it |
|---|---|---|
| A page or any Oracle response | 192 KiB body | `StaticFileOracleHandler` refuses the file *before reading it*; a handler returning one gets a clear 500 |
| A feed message (`Feed`) | 192 KiB payload | the rite refuses it on encode, naming the limit |
| A raw session frame (`OnSession`) | `SiteSession.MaxFrameBytes` | the rite refuses it on encode |

**Read `MaxFrameBytes` rather than hard-coding 192 KiB.** It reports what *this* session will actually accept, and
that is not the same number on every path.

### Getting past it

The ceiling is on one message, never on the content.

- **Serve it as a relic.** The rite built for exactly this: a named blob travels chunk by chunk on its own stream,
  every chunk verified against a manifest and the whole file before any bytes are returned — so a client can prove
  a blob's integrity *before* running it. This is the answer for images, downloads and WASM payloads.
- **Chunk it yourself over a session.** For a protocol with its own framing, size your chunks against
  `MaxFrameBytes`. Sequencing and reassembly are yours; the Conduit deliberately offers no help, because
  `ProtocolId` is yours too.

A practical consequence worth knowing before you design a page: **a Document-tier site is one document.** It carries
its own `<style>`, because a linked stylesheet costs a second full round trip over a channel where each one is
expensive — and a large embedded image will meet the ceiling long before the markup does.

CupriNet's `design/transports-and-limits.md` is the reference for where each number comes from and why.

## Running one in a container

```sh
docker compose -f deploy/docker-compose.yml up
```

A node holding a site on L2 and an HTTP gateway in front of it, on <http://127.0.0.1:8080>. Put your own page in
`deploy/site` — edits are served on the next request, no rebuild. An onion variant is behind `--profile onion`.

See [`deploy/README.md`](deploy/README.md), which is also where the one setting a containerised node usually needs
is explained: the address visitors reach it at.

## Samples

A separate samples solution streams **Constellation** — a live view of the node's own overlay — to the browser entirely
over L2. Start a second node and watch it appear in the first one's page, live, over the very connection being
visualised. (Peers are **nodes**, not viewers: a browser visitor is a Pilgrim, which by design leaves no overlay
trace and so never appears in the map.)

Both show a **connection panel** naming the peer they are attached to — the `cupri1…` Signet, the transport, the session
— so moving between nodes is unmistakable rather than inferred.

- **`samples/Constellation`** — HTML, CSS and a live feed served over L2, rendered by the client. **Ships no code and
  references no CupriFace**: an author writes a website, and the client is what renders it.
- **`samples/Constellation.CupriFace`** — the same feed, but the site additionally ships compiled C# behaviour as a WASM
  blob, run in a capability-scoped region. The same C# source also runs as a desktop client.

**The server side stays CupriFace-free** — the node, the Shrine host and the server-rendered path pull no UI runtime, so
a Tor or Cloudflare deployment carries none of it. CupriFace ships only with the browser client, where it is the
renderer.

## Status

**Working end to end, not yet packaged.** Both modes run: a site served over plain HTTP anywhere (Mode 2), and a
browser client that dials the node over WebRTC, completes a Pilgrimage, fetches the site over L2 and renders it live
(Mode 1) — verified in real Chromium by an automated gate.

What it is not yet is *consumable*: nothing is published to a feed, there is no project template, and the container
image has never been built. See **[TODO.md](TODO.md)** for the honest list.

**Tor is wired but unproven.** `CupriNet.Nodestar.Tor` supplies an onion transport, `UseTor()` opts in, and setting
`TorFacePort` publishes the HTTP front as a second onion so a browser can reach the site through the Mode-2 gateway —
WebRTC is clearnet UDP and does not cross a Tor circuit, so onion delivery is snapshot delivery. None of it has ever
opened a circuit: the development machine has no Tor access, so the tests are structural and the first real run is
the first real test.

The CupriNet capability underneath — Shrines (Signet, Pilgrimage, Oracle) plus the **Auspice** live-feed rite, all
over WebRTC — is built and released in CupriNet **0.3.4**, so this is product work on a working protocol. See
[`design/nodestar.md`](design/nodestar.md) for the full design.

## License

MIT. Copyright © Wixely.
