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

## Verified in a real browser (headless Chromium, driven by Playwright)

- A NativeAOT-LLVM wasm module runs in the browser at all — the `WasmCrypto` probe prints `PROBE OK` with Ed25519,
  HKDF, ChaCha20-Poly1305 and X25519 all exercised. Previously this was only known under Node.
- The client bundle loads, `main` runs, and **`DllImport("js")` resolves in both directions**: `cupri_seed` returns
  the link the page fetched, and `cupri_connect` creates a real `RTCPeerConnection` and `RTCDataChannel`.
- The node serves every asset correctly, `application/wasm` included, and the seeded Intonation reaches the module.

The client currently gets as far as **dialling** and then stops. It does not crash.

## The blocker, and why it is not a bug in this code

**NativeAOT-LLVM wasm has no event-loop integration**: no synchronization context, and no timer thread behind
`Task.Delay`. An `await` on the browser's only thread does not yield — it blocks the loop that would complete it.

That was measured, not guessed. With a real seed the renderer process *crashed* immediately after
`createDataChannel`, which is precisely where managed code returned into an awaiting loop. Restructuring so `Main`
fires and returns rather than awaiting **fixed the crash** — the evidence for the diagnosis.

Two consequences:

- **`Task.Delay` is unusable here.** `BrowserLoop.NextFrameAsync` replaces it, resuming continuations from a pump.
- **Emscripten's usual remedy is unavailable.** `-sASYNCIFY` does not build: Binaryen's Asyncify pass hits an
  `UNREACHABLE` and `wasm-opt` fails outright.

So the architecture is the one CupriFace independently arrived at: **JavaScript drives, managed code is pumped**.
`BrowserLoop.Tick` is written and `requestAnimationFrame` calls it.

**What remains is one build-configuration problem:** exporting `cupri_tick` to JavaScript. Emscripten dead-strips it
without `-sEXPORTED_FUNCTIONS`, and *with* that flag the module's indirect-call table breaks (`RuntimeError: null
function` before `main` runs) — adding `_malloc`/`_free` did not help. The likely next thing to try is
`emscripten_set_main_loop` called *from* managed code, passing a function pointer, so the callback is rooted by having
its address taken and no export list is involved.
