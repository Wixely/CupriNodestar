# Nodestar

**A batteries-included package for hosting a [CupriNet](https://github.com/Wixely/CupriNet) node that also serves an L2
site — a CupriNode + a clearnet HTTP front + an L2 layer of your choosing, from a few lines of code.**

A Nodestar keeps every **Lodestar** duty (overlay keep-alive, WebRTC entry point, Tor, Ferryman) and adds what Lodestar
structurally lacks: it **hosts content on L2**. Add the package, plug in what you serve (static files, a request/response
handler, a live stream, or a raw session), and you get the node, the web front (an intonation page + a served on-ramp
client, HTTPS / reverse-proxy / IIS / Tor-aware), and the L2 hosting wired together.

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

The CupriNet capability underneath — Shrines (Signet, Pilgrimage, Oracle) plus the **Auspice** live-feed rite, all
over WebRTC — is built and released in CupriNet **0.3.2**, so this is product work on a working protocol. See
[`design/nodestar.md`](design/nodestar.md) for the full design.

## License

MIT. Copyright © Wixely.
