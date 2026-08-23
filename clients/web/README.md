# The served on-ramp client

The real CupriNet client stack compiled to WebAssembly, with CupriFace as its renderer and a small JS shim for
`RTCPeerConnection`. The page it lives on is a **clearnet asset**; everything it fetches afterwards arrives over
WebRTC.

## Running it from VS Code

Press **Run → "Constellation + client (Mode 1)"**. That publishes the client, stages it into the WebRtc package,
builds the server, runs it, and opens the browser once the front is actually listening.

The other configurations:

| Configuration | What it does |
|---|---|
| **Constellation + client (Mode 1)** | The full browser path — WASM over WebRTC |
| **Constellation (Mode 2 — gateway only)** | Server-rendered; works in every deployment row, no client |
| **Constellation: beta (second node)** | A second node, prompts for alpha's link |
| **Two nodes (alpha + beta)** | Both at once, so each appears in the other's graph |

Mode 1 passes `--AdvertiseSiteInLink true`, because a Pilgrim pins the site's Signet and without it in the link the
client has a connection but nothing to ask for. It is off by default for a real deployment, since it ties the site to
the node's overlay identity.

## Building by hand

```bash
dotnet publish clients/web/CupriNet.Nodestar.Client.csproj -c Release
cp clients/web/bin/Release/net10.0/browser-wasm/publish/* src/CupriNet.Nodestar.WebRtc/client/
```

The copy is what makes a server run serve the *current* client. Skip it and the host embeds whatever was staged last
time, which is a confusing way to debug a client change that appears to do nothing.

## How it fits together

```
GET /_nodestar/app/          the page          (clearnet, the only HTTP the browser does)
GET .../intonation.json      the node's SIGNED link, inlined by the host
RTCPeerConnection            dialled from that link — no signalling server exists to use
DataChannel → IDataChannel → DataChannelVessel → Pilgrimage → Oracle / Auspice
```

`BrowserDataChannel` is the **only** browser-specific code. Above it runs the same C# the node runs — which is the
entire reason to compile to wasm rather than reimplement the protocol in JavaScript.

## Things that are load-bearing and non-obvious

- **`DllImport("js")`, not `[JSImport]`.** NativeAOT-LLVM has no Mono runtime, so interop is an Emscripten JS library
  (`wwwroot/imports.js`) linked in with `--js-library`. If the shim is missing, the emcc link fails outright — which
  is the failure you want, rather than a stub that fails at runtime.
- **No `HttpClient`.** Without Mono there is no browser HTTP handler behind it, so it compiles and then fails when
  used. The seed link is fetched by the page and handed across through the shim instead.
- **Inbound messages are queued and polled**, not pushed via a callback. Calling *into* wasm from a JS event handler
  re-enters the runtime at an arbitrary point; polling keeps the managed side in control of when it runs.
- **`await`, never a spin loop.** Wasm runs on the browser's single thread, so spinning would block the very event
  loop that completes the handshake.
- **`application/wasm`.** Served as `octet-stream` the browser refuses to stream-instantiate and the client fails to
  start with no obvious cause. The host sets it explicitly.
- **A `<base>` tag is injected at serve time** so the bundle works whether it is reached at `/app` or `/app/`. A
  redirect cannot do this job: ASP.NET treats the two as one route and redirects to itself forever.

## Verified end to end, in a real browser

The complete Mode-1 chain runs in headless Chromium against a live node:

```
[cupri] seed received (404 chars)          ← the only clearnet HTTP
[cupri] dialling constellation-demo …
[cupri] datachannel open                   ← ICE-lite → DTLS 1.3 → SCTP → DCEP, no signalling server
[cupri] pilgrimage complete — the Signet answered
[cupri] site answered 200 (4014 bytes, text/html)   ← the site, over L2
[cupri] feed Snapshot (275 bytes)          ← Auspice: snapshot on attend…
[cupri] feed Update  (…)                   ← …updates only when the overlay actually changes
```

No WebSockets, no SSE, no polling. The page is clearnet; everything after it rides the DataChannel.

## Three findings that cost real time, so they are recorded

- **`DotNetJsApi=true` is load-bearing, not optional.** It flips `IlcExportUnmanagedEntrypoints` (so
  `[UnmanagedCallersOnly]` exports reach the wasm export table as `Module._name`), and its dotnet.js loader ships the
  event-loop glue the runtime's scheduler dispatches through. The standalone build crashed the renderer the moment
  managed code awaited — no synchronization context, no timer thread — and `-sASYNCIFY` does not build (Binaryen hits
  `UNREACHABLE`). Every by-hand `-sEXPORTED_FUNCTIONS` variant broke the module's indirect-call table before `main`.
  CupriFace's WebLlvm host uses exactly this recipe and CI-gates it; diverging from it was the mistake.
- **`Main` fires and returns; `BrowserLoop` pumps.** Awaiting in `Main` blocks the browser's only thread. The page's
  `requestAnimationFrame` loop calls `Module._cupri_tick`, which drains queued continuations — only what was queued
  at entry, so a continuation that queues more work cannot starve the frame.
- **A browser Pilgrim must not construct a `CupriNode`.** The node binds sockets;
  `PlatformNotSupportedException: System.Net.Sockets`, measured. `BrowserPilgrim` is a faithful transcription of
  `CupriNode.PilgrimageOverVesselAsync` over public upstream types (Toll → Noise pinning the Signet → the exact
  `ShrineSession` mux wiring). **The right home for it is CupriNet** — a static, node-free Pilgrim entry, by
  upstream's own "the Pilgrim half runs inside the client stack" reasoning; the one thing blocking plain reuse is
  `ShrineSession`'s `internal` constructor. Until then this file must move in lockstep with `CupriNode.Shrine.cs`.

## What remains

Rendering. The markup arrives and is counted, not yet painted — wiring CupriFace to the canvas is the next step, and
the bundle will grow toward the probe's 4.5 MB gzipped when it lands. And the connection panel, which today is a log.
