# Nodestar — design

**A batteries-included package for hosting a CupriNode + a clearnet HTTP front + an L2 layer of your choosing, from a
few lines of code.** Add the package, plug in what you serve on L2 (static files, a request/response handler, or a raw
session), and you get the node (Lodestar-grade), the web front (an intonation page + a served on-ramp client,
HTTPS/proxy/IIS/Tor-aware), and the L2 hosting wired together. A thin reference host (`cuprinet-nodestar`) and a
`dotnet new nodestar-site` template ship on top for turnkey deployment.

> Status: design. This is the **product/SDK half** of the plan. The core L2 protocol it consumes — **Shrines** (the
> `Oracle` request/response rite, the `Signet` address, the `Pilgrimage` handshake) — lives in the **CupriNet** repo
> (`design/shrines.md`) and is **built and released in CupriNet 0.3.0**: Oracle + handlers, Signet + `SignetStore`,
> `HostShrine` / `PilgrimageOverVesselAsync`, the opt-in Intonation Signet field, and `CupriNet.WebRtc` published to
> the Wixely feed. A browser DataChannel arriving at a Shrine-hosting node already routes into the Pilgrimage with no
> extra wiring. Nodestar keeps **plain** naming ("site") for approachability and maps it onto a CupriNet Shrine.
>
> **Upstream state:** CupriNet **`0.3.2`** consuming CupriWebRTC `0.3.1`. **Target 0.3.2 as the floor** — 0.3.2 adds a
> version byte to the Oracle frame, a deliberate wire break against 0.3.0, and the `v0.3.1` tag published nothing (its
> CI failed on a test race) and is inert.
>
> **The streaming rite now exists upstream: [`Auspice`](#live-data-the-auspice-rite).** The question of who owned it is
> settled in CupriNet's favour, for the reason this doc anticipated — the Pilgrim half runs inside the client stack,
> including compiled to WASM. Nodestar re-exposes it as sugar and implements no protocol.
>
> **Client:** WASM via **NativeAOT-LLVM only** (no Mono, no Blazor), with **CupriFace 0.2.10+** as its renderer —
> CupriFace's own `WebLlvm` host proves the toolchain and is CI-gated there.

## Relationship to CupriNet

Nodestar is a **downstream product**, consuming from the Wixely feed:

- **CupriNet** core — the overlay node, `Arcanum` / `Rites`, and the new **Shrine** / **Oracle** / **Signet** support.
- **CupriNet.WebRtc** → **CupriWebRTC** — the browser on-ramp transport.

**Where [CupriFace](https://github.com/Wixely/CupriFace) sits — the line moved, and it is worth stating precisely.**
CupriFace is the **browser client's renderer**: it draws L2 site content to a canvas, so the client depends on it.
Everything *server-side* stays CupriFace-free:

| | Depends on CupriFace? |
|---|---|
| `CupriNet.Nodestar` — node, Kestrel front, Shrine host, feeds, SSR | **No** |
| `cuprinet-nodestar` reference host + `dotnet new` template | **No** |
| The served **browser client** (shipped by `.WebRtc`) | **Yes** — it is the renderer |

The original constraint is preserved where it mattered: **a node hosting a site over Tor or Cloudflare drags in no UI
runtime**, because Mode 2 renders server-side and never serves a client. What changed is that the *browser* half is no
longer trying to avoid the one component built to render HTML without a browser engine.

A Nodestar **"site"** *is* a CupriNet **Shrine**; **`ISiteHandler`** maps onto the **Oracle** rite; a site's `cupri1…`
address is its **Signet**. Authors work in plain terms and never touch the Lexicon.

## The shape: a package, not a monolith

Nodestar is primarily a **library you add**, with a thin reference host on top — `WebApplication.CreateBuilder`, but for
a CupriNode that also fronts clearnet HTTP and hosts L2:

```csharp
using CupriNet.Nodestar;

var builder = NodestarApplication.CreateBuilder(args);   // binds appsettings + CUPRINET_NODESTAR_*

builder.Node.Concordium = "example.chat";                // CupriNode, preset to Lodestar-grade defaults
                                                         // (WebRTC on, warm start, Ferryman; Tor opt-in)

builder.Site.ServeStaticFiles("l2-wwwroot");             // the L2 layer — here, a static site
//   .Serve((req, ct) => new SiteResponse(200, …))       //   ...or a request/response handler
//   .OnSession((session, ct) => { … })                  //   ...or raw ArcanumSession (any L2 protocol)

builder.Site.Feed("ticks", async (feed, ct) =>           // live data — sugar over CupriNet's Auspice rite
{
    await feed.SnapshotAsync(CurrentState(), ct);        //   sent once, on attend
    await foreach (var change in Changes(ct))
        await feed.UpdateAsync(change, ct);              //   then pushed until the visitor departs
});

var app = builder.Build();          // wires node + Kestrel web front + L2 Shrine host; serves the
await app.RunAsync();               // intonation page + the on-ramp client (seeded with this node's link)
```

Everything the developer did *not* write is provided: the overlay node, the WebRTC on-ramp, the clearnet HTTP host
(HTTPS / reverse-proxy / IIS / onion by config), the intonation page, the served on-ramp client (auto-seeded with this
node's Intonation so it dials back with no user input), the `cupri1…` Signet, and the Pilgrimage + Oracle exchange. The
developer supplies only **what runs on L2**.

**The L2 layer is pluggable — "anything" means anything.** The common case is an `ISiteHandler` (`SiteRequest →
SiteResponse`, mapped onto the Oracle rite) with two built-ins: static files (`FilePathGuard`) and a delegate. Alongside
request/response, a site may expose named **feeds** (`Site.Feed`) — thin sugar over CupriNet's `IAuspiceSource`, and how
live data reaches a page. The escape hatch `OnSession` hands you the raw `ArcanumSession` (Epistles/Conduits) to run a
completely custom L2 protocol — you still get node + web + client + identity for free and own only the wire.

**Package family** (the base stays feed-light; opt into the heavy bits):

| Package | Gives you | Pulls |
|---|---|---|
| `CupriNet.Nodestar` | builder, node host, Kestrel web front, intonation page, L2 Shrine host + static/delegate handlers + Auspice feeds, **Mode-2 SSR** | CupriNet core (feed) + ASP.NET Core |
| `CupriNet.Nodestar.WebRtc` | **Mode-1** browser on-ramp: serves the client, accepts DataChannels | `CupriNet.WebRtc` → CupriWebRTC. **No CupriFace package reference** — the renderer is compiled *into* the embedded wasm bundle, so it is a build-time dependency of `clients/web`, not a NuGet dependency of anything a host restores. |
| `cuprinet-nodestar` + `dotnet new nodestar-site` | run turnkey / scaffold a site, config-only | the above |

**CupriFace never rides in as a package at all.** It is compiled into the client bundle that `.WebRtc` embeds, so no server-side project restores it — a stronger separation than the one originally planned. A pure
static-or-SSR site over Tor needs only the **base** package and stays CupriFace-free — so a CupriFace regression cannot
break the deployments that never serve a client.

## Two serving modes

The browser's only P2P primitive is WebRTC, and WebRTC needs the browser to reach a **clearnet UDP endpoint** on the
node. That's fine for a public node but breaks two delivery targets — a **Cloudflare tunnel** carries HTTP(S)/TCP, not
inbound WebRTC UDP, and **Tor** delivery would expose the node's clearnet IP (killing the anonymity) and bypass the
user's own Tor path. So Nodestar supports **two** modes, and the deployment matrix *requires* both:

```
Mode 1 — DIRECT ON-RAMP (E2E)                        Mode 2 — L2→HTTP GATEWAY (server-side rendering)
  browser ──HTTPS──► Nodestar (serves WASM client)     browser ──HTTP(S)/onion──► Nodestar
  browser ──WebRTC─► Shrine host (client in-tab)        Nodestar runs the Pilgrim client IN-PROCESS (loopback)
  Noise + Oracle run end-to-end in the browser          renders the Shrine to ordinary HTML, serves it
  needs: reachable clearnet UDP endpoint                needs: only an HTTP port (works everywhere)
```

- **Mode 1 (direct on-ramp)** — the served page *is* a CupriNode Pilgrim; it opens its own DataChannel back and speaks
  the real protocol. Direct, sandboxed, live. **Public / clearnet-reachable hosts.**
> **Implementation note — Mode 2 calls the handler directly, not a loopback Pilgrimage.** This section sketched the
> gateway as the node running a Pilgrim client against its own Shrine over loopback. As built, it invokes the
> `IOracleHandler` in-process instead. The reason: the Pilgrim side is a *node-level* API, so a real loopback would
> mean a second `CupriNode` in-process, a TCP listener and a Noise handshake per visit — to reach a handler this
> process already holds. The argument for loopback was that **no third party sits in the middle**; a direct call
> satisfies that more strongly, since nothing reaches a wire at all, and the content is byte-identical because both
> paths end at the same handler. **This holds only for an own-hosted Shrine** — foreign-Shrine gatewaying (Phase 4)
> genuinely needs a Pilgrimage, and will get one.

- **Mode 2 (gateway / SSR)** — **SSR = *server-side rendering*: the node fetches the L2 content itself, turns it into
  finished HTML, and serves that over ordinary HTTP; the browser receives a completed page and runs no client at all.**
  It is what makes **IIS / reverse-proxy / Cloudflare / Tor** work: no browser WebRTC required. **When the Nodestar
  serves its *own* Shrine this is a loopback call** (the Pilgrim and the Shrine are the same process), so it stays
  end-to-end clean — no third party in the middle. Gatewaying *someone else's* Shrine would make the node a
  content-seeing proxy; **v1 scopes gateway mode to own-hosted Shrines**.
- **Mode 2 cannot stream.** A finished HTML page has no channel back to the server unless you add one, and the
  live-data rule below forbids every such channel (WebSockets, SSE, polling). So Mode 2 serves a **point-in-time
  snapshot**; live updating is a Mode-1 feature. This is a structural property of the two modes, not a sample detail.

| Deployment | Mode 1 (WebRTC) | Mode 2 (SSR) |
|---|---|---|
| Standalone, public IP | ✅ | ✅ |
| Behind reverse proxy | ✅ *if* UDP also forwarded | ✅ |
| Behind **Cloudflare tunnel** | ✗ (no inbound UDP) | ✅ |
| **Purely on Tor** (onion) | ✗ (would deanonymise) | ✅ |
| IIS | ✅ *if* UDP reachable | ✅ |

The showcase — one Nodestar that is web server **and** Shrine host of its own site — runs Mode 2 as loopback and works
in *every* row, Tor included, with no browser WebRTC. That is the MVP.

## The client — bootstrap, then everything over WebRTC

The Nodestar serves one thing over HTTPS: the **web client** — the **real CupriNet client stack compiled to
WebAssembly**, wrapped in a plain DOM shell (address bar, status, viewport) and a small JS-interop shim for
`RTCPeerConnection`. **One codebase, zero protocol duplication** — the browser runs the same Noise, framing and rites
that the C# node does, because it *is* the same code.

**The boundary, stated once:**

> **The client is a clearnet asset.** It is an ordinary web page, served by an ordinary HTTP server — the Nodestar's
> Kestrel, a reverse proxy, a CDN, or a file on disk. It has no Signet, is not addressed on the overlay, and is not
> fetched over L2. It is cacheable, versionable, and swappable like any other static asset.
>
> **The L2 site is what arrives over WebRTC.** The content, its assets, and its live data are overlay-addressed and
> reach the browser only through the DataChannel.

Everything that matters about the design follows from that split: the clearnet half is a dumb, replaceable bootstrap;
the addressed, authenticated, live half is entirely on L2. (CupriNet's own
[`websites-l2.md`](../../MeshProtocol/design/websites-l2.md) already frames it this way — "an ordinary web page… served
over clearnet by *some* node (or any static host)".)

The bootstrap is the *only* HTTP the browser performs:

```
1. GET /            → the WASM client + the node's own Intonation, inlined
2. WebRTC connect   → auto-dials back from the seeded Intonation; no signalling server
3. Pilgrimage       → Noise XX pinning the Signet — an authenticated L2 session to the Shrine
4. Oracle consult   → the actual site content, injected into the DOM      (Vessel stream 5)
5. Auspice attend   → snapshot, then updates pushed indefinitely          (Vessel stream 7)
```

Step 4 is the important one: the served page is a **loader**, not the site. The site is fetched over L2 and added to the
DOM by the client. Step 5 gives it the thing a static page can't have — a **subscription**, "like WebSockets but
entirely over WebRTC". Steps 4 and 5 run **concurrently on one Pilgrimage**, because Oracle and Auspice bind to
different Vessel streams and the Shrine session muxes both.

**Why WASM and not a hand-written JS client.** A JS client would be a **second wire-compatible implementation** of the
protocol *and* its crypto — permanent drift risk, a conformance corpus as a build gate, and no way to avoid it:

- **The Pilgrimage handshake is not optional — DTLS cannot replace it.** The node's signed Intonation *does* carry its
  DTLS certificate fingerprint, and the browser *does* verify it, which makes even the pipe MITM-resistant against a
  fake node. But that authenticates **the node's Sigil**, and a Shrine's **Signet is a deliberately separate keypair**
  — identity separation is the point (hosting must leak no L1 identity), and for anonymous hosting the Signet is never
  in the Intonation at all. **The key you must authenticate is precisely the one DTLS cannot vouch for.** CupriNet is
  explicit: *"DTLS is not our trust layer… DTLS and ICE are just the pipe"*
  ([`webrtc-clients.md`](../../MeshProtocol/design/webrtc-clients.md)). There is no shortcut past Noise.
- **In WASM the crypto question disappears.** The stack carries its own implementation, so the browser's WebCrypto gaps
  (notably **no ChaCha20-Poly1305**) are irrelevant. What remains is a **performance** question — how fast that crypto
  is once compiled — not a compatibility one.
- **The JS that's left is genuinely tiny.** Parse the Intonation's WebRTC block, `setRemoteDescription`, `createOffer`,
  done — there is no signalling loop to implement, so the shim sits behind the existing `IDataChannel` seam and nothing
  above it learns WebRTC exists.

**Toolchain: NativeAOT-LLVM. Only.** NativeAOT-LLVM is a **.NET toolchain** (`dotnet/runtimelab`), *not* a CupriFace
feature, so using it costs us no CupriFace dependency. CupriFace 0.2.10 ships a `samples/WebLlvm` host — **14.2 MB
(5.5 MB gzipped), ~7× faster than interpreted, CI-gated on pull requests against real Chromium** — so the toolchain
demonstrably carries a large C# codebase into a browser.

**Mono / interpreted .NET-WASM is explicitly rejected**, even though CupriFace also ships that host (`samples/WebWasm`)
and it would otherwise be the obvious fallback. Neither Blazor nor raw interpreted WASM is in scope.

> ### ✅ RESOLVED — BouncyCastle compiles, trims, links **and runs** under NativeAOT-LLVM
>
> Removing the Mono fallback made this a blocker rather than a budget item: with no second way to run the stack in a
> browser, a failure would have forced a crypto-backend swap through the Alembic seam or a rethink of the browser tier.
> **It was measured rather than assumed** — see [`probe/WasmCrypto`](../probe/WasmCrypto), which references the real
> `CupriNet.Alembic.BouncyCastle` + `CupriNet.Rites` packages, not a toy.
>
> Published for `browser-wasm` under **`TrimMode=full`** (trimming included deliberately, so reflection the trimmer
> would later break could not hide), linked by emcc, and **executed**:
>
> ```
> suite=BouncyCastle secure=True
> ed25519 sign+verify=True
> sha256=32B sha512=64B hkdf=32B
> aead=ChaCha20Poly1305Aead agreement=X25519KeyAgreement
> auspice roundtrip kind=Snapshot topic=overlay bytes=43
> oracle request encoded bytes=19
> PROBE OK
> ```
>
> **Size:** 1.2 MB wasm, **418 KB gzipped** (+170 KB JS) for crypto + rites, standalone. Against CupriFace's 14.2 MB /
> 5.5 MB for its rendering engine, the protocol layer is a rounding error — the client's budget is dominated by the
> renderer, not the stack.
>
> **Settled in passing:** the suite's AEAD is **ChaCha20-Poly1305** with X25519 agreement. That confirms a JS client
> would indeed have needed its own ChaCha implementation, since WebCrypto has none — moot now that the C# stack
> compiles, but it retires the question rather than leaving it open.
>
> *Caveat worth keeping:* this proves the **crypto and rite codecs**. It does not yet prove the full client path
> (`CupriNet.Hosting`'s Pilgrim side, plus CupriFace) compiles under the same toolchain, nor does it measure the
> handshake's *speed* in wasm. Widen the probe before Phase 2 rather than after.

**Deployment detail, load-bearing and easy to miss:** the web front **must serve `.wasm` as `application/wasm`**. Served
as `octet-stream` the browser refuses to stream-instantiate and the app simply fails to start. Bake it into Kestrel's
content-type mapping and the IIS/proxy deploy recipes.

### ⚠ The 256 KiB message ceiling — a hard constraint on everything served over L2

**Verified in CupriWebRTC source.** `SctpAssociation.MaxMessageBytes = 256 KiB`, enforced in **both** directions: a
hard `ArgumentOutOfRangeException` on send, and refuse-to-buffer on reassembly (*"refuse to buffer without bound"*).
The source is explicit that this is *"the number an endpoint may honestly advertise as SDP's `a=max-message-size`"* — it
matches what browsers actually accept, so **it is not a number we can raise.**

Note the asymmetry, which is easy to get backwards: the TCP path caps a frame at **16 MiB**
(`FrameCodec.DefaultMaxFrameSize`); `DataChannelVessel` imposes no cap of its own, so it is tempting to read the WebRTC
path as *less* constrained. It is **64× more** constrained.

And an Oracle response is **one message**: `OracleResponse.Body` is a fully-materialised `byte[]`, `ConsultAsync` sends
one frame and reads one, and `StaticFileOracleHandler` does `File.ReadAllBytesAsync`. So:

| Payload | Over WebRTC | Verdict |
|---|---|---|
| An HTML page, CSS, JSON | comfortably under 256 KiB | ✅ fine via Oracle |
| A photo, a large inlined asset | often over | ⚠ split assets into separate consults |
| **A WASM app blob (multi-MB)** | **far over — throws** | ❌ **Oracle cannot carry it** |
| A live snapshot or update | tiny per message | ✅ fine via Auspice |

**The Auspice enforces its own, tighter ceiling: `MaxPayloadBytes` = 192 KiB**, checked on *both* encode and decode,
leaving 64 KiB of headroom for framing, the vessel header and Noise/DTLS overhead (padding may reach `MaxPaddedBytes` =
224 KiB). It fails **at the rite with a clear message** rather than deep in the transport — so a feed that tries to push
too much gets a comprehensible error, not an SCTP fault. Size feeds against 192 KiB, not 256.

**Consequence: the hosted-app tier cannot fetch its blob over Oracle.** This is a real Phase-3 blocker, and it would
surface at *runtime*, not compile time. The route is **[`Reliquary`](../../MeshProtocol/src/CupriNet.Rites/Reliquary.cs)**
— chunked, hash-verified, resumable transfer that already exists in CupriNet (64 KiB default chunks, well under the
ceiling; per-chunk and whole-file hashes; 8 GiB limit). It is **not currently wired to the Shrine path**, so connecting
it is a prerequisite for Phase 3, not an optimisation. Reinventing chunking inside Oracle would duplicate it.

**Design rule that follows:** treat an Oracle consult as *"one page-sized document"*, an Auspice message as *"a stream of small
messages"*, and Reliquary as *"anything large"*. Nothing served over L2 may assume it can send a multi-megabyte buffer
in one call.

### CupriFace renders the site — no `iframe`, no bridge

The client hands L2 site content to **CupriFace**, which parses HTML + CSS (AngleSharp DOM, a real selector cascade),
lays it out and paints it to the canvas. The two content kinds now differ only by **whether the site ships code**:

1. **Document** — markup, CSS and data-bindings fetched over L2 and rendered by the client's own CupriFace. **No blob,
   no separate module, nothing to instantiate.** Live data flows *in-process*: an Auspice update lands in a C# model and
   CupriFace's binding (`{{path}}`, `data-repeat`) refreshes the view.
2. **Hosted app** — the site additionally ships **compiled C# behaviour** as a WASM blob, fetched **via Reliquary**
   (*not* an Oracle consult — see the 256 KiB ceiling), run in a **capability-scoped** runtime: it draws into a
   client-owned region and reaches the network *only* through the host API the client grants — no L1 session, no ambient
   network, no other sites. Reliquary's per-chunk hashes give the client **integrity verification of the blob it is
   about to execute**, which a single opaque response would not.

**What this buys, and what it costs:**

- ✅ **The `postMessage` bridge disappears entirely.** It existed only because a sandboxed frame has no network of its
  own. There is no frame, so there is no bridge and no bridge contract to design — a whole deliverable and a whole open
  question deleted from Phase 2.
- ✅ **Hostile site script cannot run, because there is no JavaScript engine.** CupriFace's central claim — *"does not
  embed a web browser or a JavaScript engine"* — is a stronger isolation posture for the markup tier than a sandboxed
  frame, which sandboxes script rather than removing it. Residual surface is the parser, the CSS cascade, the binding
  engine and resource loading — real, but far smaller than a scripting runtime.
- ✅ **One renderer, every target.** The same markup paints identically in the browser, on desktop and on Android, and
  CupriFace carries a **real-DOM ARIA mirror on the web host**, so screen readers still work despite canvas rendering.
- ⚠️ **The bootstrap is no longer small — measured at 4.5 MB gzipped** for renderer + full client stack
  ([details](#bundle-size-measured)). "A thin client" is not an accurate description of the clearnet asset, and the
  deployment story has to own that number. The renderer is 87% of it.
- ⚠️ **Chrome/content separation is now ours to enforce.** With everything painted into one canvas, the boundary between
  client chrome (address bar, connection panel) and site content is no longer the browser's to police — see the
  [connection panel](#the-connection-panel--who-am-i-actually-talking-to) rules.

## The sample

A **separate sample solution** (`samples/`, its own `.sln`, not referenced by the Nodestar build or the template) that
proves the platform by streaming live data to a browser. **Two sample projects, one feed, one hard transport rule.**

**The transport rule: no WebSockets, no SSE, no long-polling, no HTTP polling.** The only HTTP the browser ever makes is
the client bootstrap. From then on **every byte of application data — the site's own markup, its assets, and every
update — rides the L2 session over the WebRTC DataChannel.** If a demo can't be built that way it doesn't ship in the
sample; the point of the sample is that the overlay *is* the live-data transport.

### Live data: the Auspice rite

**This is no longer ours to design — CupriNet 0.3.2 ships it.** The `Auspice` is the Oracle's streaming sibling: *the
Oracle answers once; the Auspice streams.* Nodestar implements no protocol here and re-exposes it as `Site.Feed(…)`
sugar, which is exactly the division upstream intends.

```
Pilgrim ──Attend("ticks")──►  Shrine
        ◄─ Snapshot            opening state, sent once per attendance
        ◄─ Update              incremental, pushed until…
        ──Depart──►            …the Pilgrim leaves, or
        ◄─ Sealed              …the feed ends, fails, or was never hosted
```

The semantics this design wanted are the semantics it shipped with:

- **Snapshot then updates, one attendance.** A late joiner gets current state first, so there is no
  connected-but-empty window, and both share the stream's ordering.
- **An unhosted feed is refused, not ignored** — `Sealed("no such feed")`, so a client never hangs waiting for a stream
  that will never arrive. The same applies across versions: both rites are now CupriMark-versioned, so a newer Pilgrim
  attending an older Shrine gets a **typed refusal instead of silence**.
- **One bad feed seals only itself** — a throwing source never takes the Shrine down.
- **Bounded by design** — `MaxConcurrentFeeds = 32` per Pilgrim, a Ward against unbounded subscriptions.
- **Per-message, not a byte stream** — for anything large, use the Reliquary.

**Padding is a first-class privacy control, and it matters more here than anywhere else.** Encryption hides bytes, not
how many there are — and a live feed leaks far more through size than a request/response burst does, because update size
tracks content and snapshot size tracks how much state exists. `AuspicePadding` quantises each message before sealing:
`Blocks(512)` by default, or `PowersOfTwo`. **Nodestar should default feeds to padded rather than expose an unpadded
fast path**, since the author most likely to forget is the one streaming the most revealing data. It does not hide
timing, and the doc says so.

### Reconnect

The one thing upstream leaves to us: after a DataChannel drop the client re-attends and takes a **fresh snapshot**
rather than attempting replay. Simple, always correct, and honest about the gap — the sample **shows the gap on screen**
rather than hiding it.

### "Constellation" — the shared feed

Both projects render the same feed: **a live view of the node's own overlay.** The host streams its *real* activity —
pilgrims connecting and dropping, rite invocations, per-session throughput — as a node/edge graph with sparklines.

It is chosen over synthetic data because it is **self-demonstrating**: *start a second node and watch it appear in the
first one's page*, live, over the very connection being visualised. Snapshot-then-update is legible with no
explanation, because the snapshot **is** the current constellation. And the data is real, so the demo doubles as an
operator's view of the node.

> **Peers are nodes, not viewers — and an earlier draft of this section got that wrong.** It claimed a second *tab*
> would appear in the first tab's graph. It cannot. This feed projects the **L1 overlay map**, where an entry means
> another CupriNode: durable identity, anchored overlay presence, a signed `PeerRecord`. A browser visitor is a
> **Pilgrim** — throwaway identity per visit, and the Pilgrimage *skips the overlay join by design*, so there is no
> record to project. Measured: with a browser client connected and streaming this feed, the count read `0 of 0` until
> a second **node** started.
>
> That distinction is the architecture working as intended, not a gap: a visitor who leaves no overlay trace is
> exactly what "the Shrine learns nothing durable about who visited" means. **If the demo should also show live
> visitors, that is a separate feed** — the Shrine host knows its own attending Pilgrims — and it would need its own
> think about what a visitor count reveals.

> **What is safe to publish — an earlier claim in this doc was wrong.** A previous revision said peer identities and
> addresses should be withheld from anonymous viewers. That is **incorrect**, and CupriNet's own security note corrects
> it: `PeerRecord`s are *signed by their owners precisely to be redistributed*, and every Intonation's Litany already
> hands out sampled Sigils. Publishing peer data is what the overlay is **for**.
>
> **The real line is `Bucket` / `Standing` / `Taint`** — not facts about peers, but *this node's private judgements*
> about them, which the code states never propagate as authoritative fact. Streaming them would tell a Sybil operator
> whether its identities had been noticed or quarantined.
>
> **The trap is concrete and easy to hit:** `ConstellationEntry` bundles those judgements together with the public
> record, so serialising the entry leaks them **by accident**. **Project `entry.Record`, never the entry.** The sample
> must demonstrate the correct projection, since it is the thing an author will copy.
>
> Rule of thumb, worth stating in the sample's own comments: *if the control plane wouldn't serve it to a peer, don't
> push it to an anonymous visitor.*

*(Naming: CupriNet already has a `ConstellationEntry` type for a node's peer view, which is what this sample streams —
apt, but decide whether the sample keeping the name reads as a core feature. `Overlook` or `Vigil-view` are alternatives
if the collision grates.)*

| Project | L2 content | Ships code? | Renders |
|---|---|---|---|
| `samples/Constellation` | **Document tier** — HTML + CSS + bindings over the Oracle rite | **No** — markup only | client's CupriFace, driven by the model |
| `samples/Constellation.CupriFace` | **Hosted-app tier** — a compiled CupriFace app, fetched via Reliquary | **Yes** — a WASM blob | its own C# behaviour in a scoped region |

- **`samples/Constellation` — the basic L2 website.** A Nodestar host publishing the `overlay` Auspice feed plus the
  page itself over L2. The client fetches the markup and renders it with CupriFace; an `Update` lands in a C# model and
  the binding refreshes the view — **no bridge, no frame, no glue.** **The project references no CupriFace and ships no
  code**: it serves HTML, CSS and a feed. That is precisely the point — an author writes a website; the client is what
  happens to render it. The original ask survives the architecture change intact.
- **`samples/Constellation.CupriFace` — the WASM app.** The same `overlay` feed and the same snapshot-then-delta
  subscription, but the client fetches a [CupriFace](https://github.com/Wixely/CupriFace) blob (HTML+CSS → GPU
  canvas from a C# model, compiled to `WebLlvm`) and executes it — the app runs in the browser as if native, reaching
  the feed *only* through the granted host API. Same data, richer rendering: an animated force-directed graph where
  the HTML version draws a simpler SVG. It exercises the **host-API capability contract** from the outside, exactly the
  pressure a first-party integration wouldn't apply. **One app, two front doors:** the same C# source also runs as a
  native desktop client (`DesktopHost` over TCP, no WebRTC, same feed) — the reason to build it in CupriFace at all.

Running the two side by side is itself the demonstration: **one feed, one subscription model, two renderers**, and each
appears in the other's graph.

### The connection panel — "who am I actually talking to?"

Both samples show a **persistent panel naming the peer they are connected to**, so that navigating between nodes makes
the change unmistakable. Without it the demo is ambiguous: two Constellation graphs look alike, and the single most
interesting fact — *that you just moved to a different, independently-addressed host* — is the one thing not on screen.

What we can honestly show, and where it comes from:

| Field | Source | Notes |
|---|---|---|
| **Signet** (`cupri1…`) | pinned during the Pilgrimage | the address you reached, self-authenticating. **The headline field.** |
| **Moniker** | optional signed Intonation field | a human name for the node, when it advertises one |
| **Concordium** | node config | which network this is |
| **Transport** | CupriWebRTC | DTLS **1.3 or 1.2** (it dispatches both), ICE-lite, host candidate `IP:port` |
| **Mode** | client | direct on-ramp vs server-rendered snapshot |
| **Session** | client | uptime, bytes in/out, RTT, feeds currently attended |

Three design rules, in decreasing obviousness:

1. **It is client chrome, never site content.** A site must not be able to draw its own connection panel — that is the
   browser address-bar spoofing problem imported wholesale. It lives with the address bar, outside whatever region the
   site renders into.
2. **The Signet display is a security affordance, not a readout.** It is the same job an `.onion` address does and the
   same one CupriNet already intends — *"the browser pins the Sigil, a bech32 fingerprint the user can compare."* Show
   it truncated with the full value one click away, and design it to be *compared*, not just displayed.
3. **Do not show a node Sigil for a Shrine session — even when we know one.** Serving a Shrine presents *only* the
   Signet, precisely so hosting leaks no L1 identity. If the client learned the entry node's Sigil during the WebRTC
   step and displayed it beside the Signet, the panel would assert a linkage the protocol deliberately refuses to make,
   and would be actively misleading for an anonymously-hosted Shrine. **The panel shows what the Pilgrimage proved, and
   nothing it merely happens to know.**

This pairs with Constellation rather than duplicating it: the feed shows *the node's* view of its peers; the panel shows
*your* view of the node. Running two sample hosts and moving between them exercises both at once — each host's graph
gains and loses you as a Pilgrim while the panel's Signet visibly changes.

**Both sit correctly against the 256 KiB ceiling** — and that is partly why this demo was chosen. `Constellation`'s page
is well under it, and its updates are tiny Auspice messages, so the Document-tier sample is buildable **today** with
no new transport work. `Constellation.CupriFace` is the one that needs Reliquary wired up first, which is exactly why it
sits in Phase 3 behind that prerequisite rather than alongside the HTML version.

**Mode-2 consequence, stated plainly.** Server-side rendering cannot stream under the transport rule — pushing updates
into a finished HTML page needs a channel the rule forbids. So over Tor / Cloudflare the sample renders a **point-in-time
snapshot** of the constellation (accurate as of the request, refreshed by reloading), and live updating is a
**Mode-1-only** feature. That is a deliberate limitation, not a gap to paper over: it makes the cost of the gateway mode
legible. Conveniently, the snapshot the SSR path renders is *the same snapshot* a Mode-1 subscriber receives on join —
one state serialisation, two consumers.

## Making & deploying a site (the author journey)

The design goal: **zero → running, addressable CupriNet site in one `dotnet new` + one `dotnet run`, and one deploy
recipe to put it online.**

- **Scaffold.** `dotnet new nodestar-site` (`--content static|handler|stream`) lays down a ready project: the host
  `Program.cs`, the content (`wwwroot-l2/` for static, a handler stub, or a feed-publishing stub), `appsettings.json`,
  a `Dockerfile` (FROM the Nodestar SDK base image), and a `deploy/` folder with a recipe per target. **The template has
  no CupriFace option** — `samples/Constellation` is roughly the `--content stream` output, and
  `samples/Constellation.CupriFace` is the separate thing you'd build on top by choice.
- **Develop.** `dotnet run` boots a local Nodestar and prints the site's `cupri1…` address and a local URL; open it and
  the client auto-connects to your local node and renders your site. The site key lives in the data dir, so the
  address is stable across restarts. (The CupriFace sample adds its own `--desktop` native-window loop; that's the
  sample's affordance, not the template's.)
- **Deploy.** Copy-paste recipes in `deploy/` for the whole matrix: a Docker image (`FROM
  ghcr.io/wixely/cuprinet-nodestar` + your content), `docker-compose` (clearnet and Tor), IIS (`web.config`/ANCM),
  reverse-proxy / Cloudflare-tunnel snippets, and systemd / Windows-service units — all config via
  `CUPRINET_NODESTAR_*`. The `cupri1…` key persists in the data volume, so the address survives redeploys.

## Repo & build

- **Own repo** (`CupriNodestar`) — a published SDK package family + reference host + web client + template + samples.
  It's an ASP.NET Core web host: heavy, opinionated, and a *product*, so it stays out of the deliberately lean CupriNet
  repo (which avoids ASP.NET Core and keeps its main build feed-free). This mirrors the project's polyrepo pattern
  (CupriWebRTC, CupriTor, CupriMark, CupriCurve, CupriFace).
- **The web client is C# compiled to WASM.** A `clients/web/` project targeting NativeAOT-LLVM (no Mono
  fallback), with a small JS shim, shipped as an embedded resource in `CupriNet.Nodestar.WebRtc`. It references the
  CupriNet client stack **and CupriFace** from the feed — no protocol code and no renderer of its own.
- **The CupriFace boundary is a build gate, on the server side.** CI builds **`CupriNet.Nodestar`** (base) with the
  CupriFace feed package **unavailable**, so a UI-runtime dependency leaking into the node/host/SSR path is a build
  failure rather than a convention. `.WebRtc` and the client are exempt by design — that is where the renderer lives.
- **Solutions:** `Nodestar.sln` (packages + host + client + template) and `samples/Samples.sln`.
- **Consumes from the Wixely feed**: CupriNet core **0.3.2+** (Shrine/Oracle/Signet/Auspice), CupriNet.WebRtc, and
  **CupriFace 0.2.10+** (client-side only).
- **Mirrors Lodestar's packaging**: Dockerfile (multi-arch), docker-compose (clearnet + Tor), systemd + Windows-service
  units, triple-source config (`appsettings.json` → `CUPRINET_NODESTAR_*` env → CLI).
- **Native AOT is on the table for the host.** CupriWebRTC **0.3.1** declares `IsAotCompatible` and enforces it in CI by
  publishing and running a native binary over both DTLS versions. A Nodestar host could therefore ship as a **native
  AOT binary** — fast start, small container, no runtime — which matters for the Docker/Tor deployment rows. Worth
  validating early, since ASP.NET Core in the same process is the part likelier to resist trimming. *(This is the
  **server** target; it says nothing about NativeAOT-LLVM/WASM in the browser, which is a different toolchain — though
  a trim-clean dependency graph is a mild positive signal for it.)*

## Phased plan (product)

0. ✅ **Done — the toolchain holds.** BouncyCastle + the rites compile, trim, link and run under NativeAOT-LLVM for
   `browser-wasm` at 418 KB gzipped ([`probe/WasmCrypto`](../probe/WasmCrypto)). The last pre-code unknown is closed;
   the streaming-rite question closed itself when CupriNet 0.3.2 shipped `Auspice`. **Remaining probe work belongs in
   Phase 2**: widen it to `CupriNet.Hosting`'s Pilgrim path plus CupriFace, and measure handshake latency.
1. **Base package + Mode 2 everywhere.** Ship `CupriNet.Nodestar` + the `cuprinet-nodestar` reference host + the `dotnet
   new nodestar-site` template: builder API, ASP.NET Core host serving the intonation page (ported from Lodestar), the
   node Lodestar-grade, one own Shrine **SSR (loopback) → HTML** via an `ISiteHandler`, and the full deployment matrix
   as `deploy/` recipes. Works everywhere, Tor included, with no browser WebRTC. **Mostly integration** — `HostShrine`,
   the Oracle handlers, `SignetStore` and the loopback Pilgrimage all exist in CupriNet 0.3.0.
2. **Mode 1 — the served WASM client.** The client stack + CupriFace compiled to WASM, the `IDataChannel` JS shim over
   `RTCPeerConnection`, auto-dial from the seeded Intonation, address bar + **connection panel**, and Document sites
   fetched over L2 and rendered. Plus **`Site.Feed` sugar over `IAuspiceSource`** and the **Auspice → model → binding**
   path — this is what makes `samples/Constellation` possible, and it lands with `.WebRtc`. *(Pilgrimage, Oracle and
   Auspice are inherited from CupriNet; rendering is inherited from CupriFace. Nodestar writes the sugar, the chrome and
   the wiring — no protocol and no renderer.)*
3. **Hosted-app tier + the CupriFace sample.** **Prerequisite: wire `Reliquary` into the Shrine path** — without it
   there is no way to move a multi-MB blob over a 256 KiB-per-message channel, so this gates the whole phase. Then the
   host-API capability contract and the client fetching, verifying, instantiating and sandboxing a WASM blob against it
   — all CupriFace-agnostic in the client. Finally, in the **separate samples solution**,
   `samples/Constellation.CupriFace`: the app shipped as *both* the served browser experience and a native
   `DesktopHost` client, used as the outside-in test of that contract.
4. **Reach & privacy.** L1 attach / roaming (address bar to Shrines you don't hold a link for); private
   (Watchword-gated) Shrines; brokered WebRTC to NAT'd hosts (consent-gated, reusing the Ferryman
   `RelayApprovalRequested` + `KnownRelays` TOFU); optional foreign-Shrine gatewaying (consent-gated).

## Open questions

- ~~Does BouncyCastle compile under NativeAOT-LLVM?~~ **Answered: yes** — compiled, trimmed, linked and executed at
  418 KB gzipped. See the resolved note above and [`probe/WasmCrypto`](../probe/WasmCrypto).
- ~~Does the rest of the client path compile?~~ **Answered: yes, all of it.** Both halves — `CupriNet.Hosting`'s
  Pilgrim path *and* CupriFace — compile, trim (`TrimMode=full`), link and run for `browser-wasm`. See
  [the measured bundle](#bundle-size-measured) below and [`probe/`](../probe).
- **Handshake latency** on a mid-range phone. Compilation is settled; speed is not. Measure the Pilgrimage end-to-end
  — BouncyCastle in wasm is the thing to watch.
- **Startup cost of a 4.5 MB gzipped bundle** on a mid-range phone — download, instantiation and time-to-first-paint.
  Size is now known; what it *feels* like is not.
- **Wiring `Reliquary` to the Shrine path.** *(Now the largest open protocol item.)* Not optional — it is the only route
  past the 256 KiB ceiling for app blobs and large assets, and it gates Phase 3. Recorded upstream as a prerequisite
  too. Decide the shape: a reserved Oracle path that hands off to a Reliquary transfer, or a Reliquary rite on its own
  Vessel stream alongside Oracle (5), Conduit (4) and Auspice (7).
- **Padding policy for feeds.** `AuspicePadding` defaults exist, but Nodestar decides what an author gets *by default*
  and how visible the choice is. Recommendation: padded unless explicitly opted out, since the unpadded fast path is
  most tempting to exactly the feeds that leak most. Note it does not hide **timing** — a fixed-cadence feed is still
  legible, which may argue for jitter in `Site.Feed`.
- **Asset strategy under the ceiling.** A page and each of its assets are separate consults. Decide whether the client
  fetches assets automatically (rewriting URLs in the sandboxed frame) or the site does it explicitly, and what a page
  that exceeds 256 KiB on its own should do.
- **Noise XX means the Pilgrim still mints a keypair per visit.** v1 pins the Signet under a *throwaway per-visit*
  identity; **Noise NK** (no Pilgrim static key) is the cleaner fit and is deferred upstream. It changes the client's
  startup cost, so worth knowing which lands first.
- **Client distribution & integrity.** It's a clearnet asset, so it can be cached, mirrored, or served from a CDN —
  which also means it can be tampered with, and it's the code that verifies everything else. Decide whether Subresource
  Integrity, a pinned version, or signing is warranted, and whether third parties may host it.
- **Chrome/content isolation inside one canvas.** *(The question the `iframe` used to answer, in its new form.)* Script
  execution is gone with the JS engine, but chrome and content now share a renderer and a surface. Settle how the
  address bar and connection panel are made unspoofable by site markup — a reserved region CupriFace will not let site
  content paint into, separate documents, or an out-of-canvas DOM overlay for chrome only. **Decide before the sample
  ships**, since it is the thing an author copies.
- **Residual attack surface of rendering hostile markup.** No JS engine removes the largest class, not all of them: the
  HTML parser, the CSS cascade, the binding engine (`{{path}}`, `data-repeat`) and resource loading all consume
  untrusted input from an anonymous Shrine. Worth a fuzzing pass against CupriFace's parser with site-supplied markup,
  and a decision on whether a site may reference external resources at all.
- **Is 4.5 MB gzipped acceptable, and if not, what gives?** No longer a guess — see the table below. The renderer is
  **87% of it**, so the levers are all on that side: `-O3` + `wasm-opt` (the probes used `-O1`), lazy-loading the
  renderer after the connection is up, or splitting Document-tier rendering from the hosted-app tier. Dropping to an
  interpreted runtime is not on the table.

### Bundle size, measured

Three probes, each adding a layer, all published for `browser-wasm` under `TrimMode=full` at `-O1` and **executed**:

| Probe | Adds | Raw | Gzipped |
|---|---|---|---|
| [`WasmCrypto`](../probe/WasmCrypto) | BouncyCastle + Oracle/Auspice codecs | 1.2 MB | **418 KB** |
| [`WasmClient`](../probe/WasmClient) | `CupriNet.Hosting` — the full Pilgrim path | 1.7 MB | **594 KB** |
| [`WasmRender`](../probe/WasmRender) | **CupriFace** — parse → style → layout → paint | 13 MB | **4.5 MB** |

**The protocol stack is not the problem.** The entire CupriNet client — crypto, rites, and the node-level Pilgrim API
with its overlay machinery — trims to **594 KB gzipped**, and `CupriNet.Hosting` costs only +176 KB despite dragging in
Concordance, Traversal and Persistence that a browser never uses. The renderer is ~3.9 MB of the 4.5 MB total.

`WasmRender` is a genuine end-to-end render, not a link check: it parses HTML, applies a stylesheet, lays out, paints
to an `SKSurface`, then **reads a pixel back** (`#141416` — the background the CSS asked for) and PNG-encodes the
result. Reading the framebuffer is the point; a render that "succeeded" without touching pixels would prove nothing.
- **Host-API capability contract** — the capability surface a hosted WASM blob is granted, and how the client exposes it
  to a nested app without handing over the raw session. Settle in Phase 3, with the CupriFace sample as the first
  consumer. **Two WASM modules in one tab** is the cost to measure there.
- **Version floor & the 0.3.0 wire break.** Nodestar targets **CupriNet 0.3.2+**. The Oracle frame gained a version byte
  in 0.3.2, deliberately breaking 0.3.0 while that release was a day old. Nothing to do beyond pinning the floor — but
  worth stating in the template and the Docker base image so no one ships against 0.3.0.
