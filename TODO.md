# TODO

What is designed but not built. Kept separate from `design/nodestar.md` so that document can describe the intended
system, while this one is honest about what a clone actually gets today.

## Deployment

- [x] **Dockerfile** for the reference host (Mode 2). Written; **the image has never been built** — Docker is not
      installed on the development machine. Verified by other means: the `dotnet publish` line, the portable-IL
      output (no apphost, so one image runs on every arch), the `dotnet cuprinet-nodestar.dll` entrypoint, and the
      `CUPRINET_NODESTAR_DataDirectory` / `SiteRoot` environment variables all work. **Unverified: the Docker layers
      themselves** — base images, the BuildKit secret mount, the non-root user, the volume.
- [x] **Built the image, and ran it.** Docker 29.1.3, 29 Aug 2026. Every line of that Dockerfile was reasoned
      rather than observed until now; the header records what the first run actually confirmed — the BuildKit secret
      mount, the private-feed restore, the portable-IL publish, the non-root `app` user, the volume, `/healthz`,
      `/_nodestar/link.json`, and **Mode 2 serving a bind-mounted site end to end** with a `{{ }}` placeholder
      binding to empty rather than showing braces. 350 MB.

      Nothing was wrong with it, which is worth recording as plainly as a defect would have been.

      Still unproven and each for its own reason: inbound UDP reaching the container, Mode 1 through a mounted
      `/client`, and Tor. One environment note is in the Dockerfile so nobody chases it: under Docker inside WSL a
      container stops cleanly a few seconds after the shell session that started it closes — that is WSL reclaiming
      the distro, not the node stopping.
- [x] **`docker-compose`** — clearnet and onion. `deploy/docker-compose.yml`, with `deploy/README.md`,
      `.env.example`, and a page in `deploy/site` so `up` shows something.

      Two services rather than one with a switch. A clearnet node publishes ports and advertises an address; an
      onion node publishes nothing and exists so that no address of its is known. As a flag an operator could
      half-apply it, and onion mode with the ports still published is the one outcome Tor was chosen to prevent.
      The onion service is behind a profile, so `up` runs the clearnet node and nothing else.

      **Verified by running a container with this configuration** — gateway answers, site mounts read-only and is
      served, `/data` writable as `app`, all three ports publish, healthcheck healthy in ~8s.

      **The healthcheck found a real trap.** The runtime image has no curl, wget or nc, so the check is bash
      talking to `/dev/tcp`. Its `/bin/sh` is dash, where that is not a builtin — as a plain string or CMD-SHELL the
      check fails with *"cannot create /dev/tcp/...: Directory nonexistent"* and the container reports unhealthy
      while serving perfectly. Measured both ways; only the exec-array form invoking bash works.

      **Two things remain unverified and are marked in the file.** Compose's own parsing of it — the profile, the
      anchors, `secrets: environment:` (needs Compose 2.23.1+) — because no Compose plugin was installed on the
      machine that checked; one `docker compose config` closes that. And the onion service, for the standing
      reason: no Tor circuit has ever been opened from this repository.

- [x] **Regression: declaring no address suppressed the node's own discovery.** `AdvertisedBeacons` was handed an
      empty list when nothing was configured, and an empty list means "advertise nothing" rather than "no opinion" —
      so the node stopped finding its own address. Fixed by passing null instead.

      **Caught by CI, not by 112 local tests, and the reason is the lesson.** This machine discovers no address
      either way, so a suppressed discovery and an absent one produce identical links here; the test written to
      cover the feature asserted an absence and was blind to the cause of it. On CI — a machine with a routable
      interface — all five browser-gate tests failed with *"this node's link carries no clearnet beacon to dial"*.

      The new test asserts the SHAPE of what is handed to the node (null, not empty) rather than what a link ends up
      containing, because that is the only thing that differs between the two environments. Mutation-tested: putting
      the empty list back fails both new tests.

      **It also invalidated a measurement.** "A container advertises nothing at all on 0.6.2" was this bug, not
      CupriNet behaviour — the original `127.0.0.1` observation was right all along, and the docs that had been
      corrected to say otherwise are corrected back.

- [x] **The image builds with no feed credential.** The Dockerfile restores from an `offline-packages/` directory
      when the build context carries one, and only falls back to the feed otherwise.

      Found by needing it: verifying the compose stack against current code meant rebuilding, and no `read:packages`
      token was available in the session. The GitHub MCP server is authenticated but holds its credential and
      exposes no way to hand one out, which is correct of it and a dead end for this.

      The way through was that the token was never actually necessary. A machine that can build this repository has
      already downloaded every package it needs — all 109, checked — so demanding a feed credential to containerise
      what it just compiled asks for a secret the build does not need. Staged from `~/.nuget/packages` and built
      with no network and no token.

      Also useful beyond this session: an air-gapped build, and CI without feed access.
- [~] **Mode 1 from the container — it serves; the WebRTC dial is still unproven.** Checked 30 Aug against a real
      containerised node with the bundle bind-mounted read-only.

      **Serving works, and three of the four unknowns are closed.** The `app` user reads a root-owned mode-755
      mount without a chown, `/_nodestar/app` answers, the wasm is served byte-for-byte (14,828,766 bytes), and
      headless Chromium on the host loaded the module and started a visit.

      **Two configuration traps found by doing it**, both of which fail confusingly rather than obviously.
      `AdvertiseSiteInLink` defaults to false, so a containerised node serves a client that boots, dials nothing and
      repeats *"that node advertises no site in its link"* — the link has no Signet to pin. And UDP has to be
      published (`-p 47654:47654/udp`) as well as TCP. Both are now in the Dockerfile.

      **Mode 2 from the container is proven all the way to a browser.** Headless Chromium on the host rendered a
      bind-mounted site as ordinary DOM — zero canvas elements, no client, no WebRTC, a silent console, the gateway
      header and `no-store` present, and the `{{ }}` placeholder bound rather than showing. That is worth stating
      beside the Mode 1 result, because it is the same container and the same host: the path that needs no inbound
      UDP works end to end, and the one that needs it is what stalls.

      **Inbound UDP is characterised rather than settled.** With both fixed, the browser reached ICE `checking` and
      then `connection failed` — further than a missing route would get, so signalling and answer-from-the-link both
      work and the connectivity check is what failed. The path here was Windows → WSL2 → the docker bridge, and ICE
      is precisely what does not survive two layers of source-address rewriting. It wants a native Linux Docker
      host, where there is one network namespace between browser and node rather than three.

      **The finding that matters more for a real deployment:** the containerised node advertised `127.0.0.1` as its
      only beacon, because nothing it could see was routable. A browser anywhere else would dial its own loopback —
      and until `PublicHost` was wired up, nothing could tell the node otherwise. See below.

- [x] **A node can be told its own reachable address.** `PublicHost` / `PublicPort` / `AdvertisedAddresses` /
      `AdvertiseLocalAddresses` now reach `CupriNodeOptions.AdvertisedBeacons`, and a link decodes to what an
      operator declared. Found while proving Mode 1 from a container, where the node advertised only `127.0.0.1`.

      **It was worse than "no option exists".** `PublicHost` and `AdvertisedAddresses` were already there, already
      documented, and referenced by nothing — configuration an operator could set correctly and have silently
      ignored, which is harder to diagnose than an option that is absent. The absence was the reported symptom; the
      dead wiring was the actual defect.

      Three things the tests found that guessing would not have:
      - The browser client has **no fallback** to the origin the page came from: with no usable beacon it throws
        rather than trying the obvious thing. So whatever the node advertises is the only dial target there is.
      - CupriNet drops reserved IPv6 before it reaches a link, `2001:db8::/32` among it. The first version of the
        IPv6 test used a documentation address, failed, and would have been read as "IPv6 beacons do not work".
      - Beacon **order** is meaning: the client dials the first non-onion beacon and never reaches the second.

      Startup now prints what a browser will dial, and warns when the answer is nothing — the failure otherwise
      appears only in a visitor's browser console, on their machine, while the node logs a healthy start.

- [ ] **`deploy/` recipes** — IIS (`web.config` / ANCM), reverse proxy, Cloudflare tunnel, systemd, Windows service.
- [ ] **A Mode-1 image.** Needs the wasm bundle, which is a build output a fresh clone does not have. The intended
      answer is restoring `CupriNet.Nodestar.Client.CupriFace` from the feed rather than carrying the Emscripten
      toolchain in a build stage — which is why the bundle is embedded in a package at all. Blocked on publishing.

## Packaging

- [x] **Packages publish to GitHub Packages on a tag.** `https://nuget.pkg.github.com/Wixely` — the same feed
      `nuget.config` already restores the upstream packages from, so consumption and publication now point at the
      same place. The `publish` job pushes on `v*` tags only: every push to main still packs and keeps the result as
      a build artifact, but one feed version per commit would bury the versions that mean something.

      They are no longer attached to the GitHub Release. A `.nupkg` someone has to find and download by hand is not
      how a package is consumed; the release carries the runnable examples, the feed carries the packages.

      **Proven as of v0.1.0-alpha.2.** A throwaway project restored all four from the feed into an isolated package
      directory, composed a Nodestar from them (`UseWebRtc` + `UseTor` + `ServeCupriFaceClient`), built, ran, and
      answered 200 on both `/` and `/_nodestar/app` — so there is no missing transitive dependency and the client
      package really does carry its bundle. That exercise also found the shutdown bug fixed alongside it.
- [x] **`dotnet new nodestar-site`.** The README's headline promise, and now a command. `templates/` packs
      `CupriNet.Nodestar.Templates`; the generated project is a node, a self-contained page, a live feed bound into
      it, and a `nuget.config` pointing at the feed — `--network`, `--moniker` and `--tor` are its parameters.

      **Proven by running it, and running it is what found the defects.** Installed the pack, generated a project,
      built it, and started it: three things were wrong that reading could not have shown. The `nuget.config`
      comment contained `--username`, and an XML comment may not contain `--`, so every generated project failed to
      restore. `WebPort` was assigned in `Program.cs`, which silently defeats `CUPRINET_NODESTAR_WebPort` — so the
      template's own parameter was the thing stopping configuration working, and the parameter is gone. And the
      startup banner hard-coded 8080, so it printed where the site was not.

      One caveat that is not the template's: it floats to `0.1.0-*`, which resolves to **alpha.4** — the newest
      published — and that predates the Mode-2 gateway binding, so the page serves with `{{ }}` showing. Against
      the current build the same generated project binds correctly (`served over L2`, ticks counting). It fixes
      itself the moment anything newer publishes, which is the artifact quota's problem rather than this one's.

## Not wired up

- [~] **Tor — wired, never dialled.** `CupriNet.Nodestar.Tor` supplies the transport and `UseTor()` wires it into
      `ConfigureOnionTransport`; the reference host calls it when `EnableTor`/`TorOnly` ask, so those settings are no
      longer decoration. Two onions come out of it: the **overlay onion** (how another *node* reaches this Shrine over
      Tor) and, when `TorFacePort` is set, the **face onion** forwarding to the HTTP front (how a *browser* reaches it,
      necessarily through the Mode-2 gateway). `TorWiringTests` covers the opt-in, the configuration opt-out and the
      startup refusal.

      **What is unverified is the network itself.** This machine has no Tor access, so nothing here has published an
      onion or opened a circuit — every test is structural by necessity. The first real run is the first real test.
      Specifically unproven: that Tor bootstraps inside the container, that `PublishAuxiliaryOnionAsync` returns a
      reachable address, and that a browser can load the gateway through it.

      Also unproven and worth watching: an onion-only node must never offer WebRTC (it would publish the clearnet IP
      the onion exists to hide). The code skips it, but no test observes an onion-only node's advertised beacons.
- [x] **Reliquary → the Shrine path.** Landed upstream in CupriNet 0.3.4 as the **Relic rite** — the Reliquary over
      the Pilgrimage on stream 8, chunk-by-chunk under the same 192 KiB frame ceiling, every chunk verified against
      the manifest and the whole file before any bytes are returned. Nothing in this repository uses it yet; see
      "Hosted apps" below for what it now makes possible.
- [x] **A site is reachable over a vessel that is not a browser DataChannel.** `NodestarApplication.AcceptPilgrimageAsync`
      serves the site to one Pilgrim over any `IVessel` the caller accepts — a test harness, a desktop client over
      TCP, anything that is not WebRTC.

      Found by [#2](https://github.com/Wixely/CupriNodestar/issues/2), reported as a conduit fault and diagnosed as
      something else entirely. A TCP connection to the node's listen port reaches the **node**: it completes a
      node-to-node handshake presenting the node's own Sigil, so pinning the site's Signet fails, and pinning the
      node's Sigil instead succeeds into a session with no Shrine behind it. Every rite then answers with a closed
      stream. The Oracle failed identically to the conduit, which is what showed the conduit was never the problem —
      worth remembering as a diagnostic: if one rite looks broken over a transport, check another before blaming it.

      Only WebRTC routed into the Pilgrimage on its own, and nothing here had ever exercised anything else.

- [x] **A TCP listener for the Shrine — landed upstream, exposed here.** `NodestarOptions.ShrinePort` opens a port
      on which every connection is a Pilgrimage and the Signet is presented unconditionally;
      `NodestarApplication.ShrineEndPoint` reads back what it bound. Off by default, because opening a port is a
      deployment decision and the two paths that matter most need none — a browser arrives over WebRTC and the
      gateway never leaves the process.

      CupriNet 0.5.0's `ListenForPilgrims`, which came out of the design question [#2](https://github.com/Wixely/CupriNodestar/issues/2)
      raised. It **inverts** that failure rather than papering over it: on the L1 port, pinning the node's Sigil
      succeeded into a session with no Shrine behind it, and here the host has no Sigil to offer, so a wrong key
      cannot complete at all. A node hosting no Shrine never opens the port, so dialling is refused outright.

      `AcceptPilgrimageAsync` stays and now delegates to the overload that serves whatever the node hosts, rather
      than restating one site's parts. That is not tidiness: 0.5.0 also selects between several hosted Signets from
      the visitor's blinded target, so passing one site's handler explicitly would quietly answer every visitor as
      that site.
- [x] **Relics are served.** `SiteBuilder.ServeRelics(directory)`, `ServeRelic(name, bytes)`, or your own
      `IRelicSource`. Built at startup rather than per request, because hashing a relic into its manifest is what
      makes the manifest a promise instead of a guess — a file changed on disk afterwards is not picked up until a
      restart.

      This is the answer to the 192 KiB ceiling, and it buys something a large response never could: every chunk is
      verified against the manifest as it arrives and the whole file before any bytes are returned, so a visitor can
      prove a blob's integrity *before* running it. `PilgrimageOverVesselTests` fetches a ~500 KB relic — nearly
      three frames, so the chunking is actually exercised — over a real Pilgrimage and compares it byte for byte.

      Deferred until the node starts because hashing needs the crypto suite, which is the node's rather than the
      builder's; the builder holds the intent, the same shape `Feed` uses.
- [x] **The 192 KiB ceiling is documented for site authors.** README has a section on what a site may serve and
      how big it may be: the cap per rite, what happens when you exceed it, and the two ways past — serve it as a
      relic, or chunk it yourself against `MaxFrameBytes` rather than hard-coding a number that differs by path. It
      also says the thing an author meets first: a Document-tier site is one document, and an embedded image will
      reach the ceiling long before the markup does.

- [x] **Raw sessions — the README's fourth site option.** `SiteBuilder.OnSession(protocolId, handler)` serves a
      duplex, message-framed pipe for the life of one visitor, over the Conduit rite on vessel stream 4. Asked for in
      [#1](https://github.com/Wixely/CupriNodestar/issues/1); unblocked by
      [CupriNet#3](https://github.com/Wixely/CupriNet/issues/3) landing in 0.3.6.

      `SiteSession` is the plain-naming adapter, the same one adapter deep that `Serve` is over the Oracle: bytes in,
      bytes out, no `ConduitFrame` to build and no flag bits to learn. Three properties it guarantees, because they
      are what a protocol moving onto L2 needs and what the issue asked for — **frames keep their boundaries**
      (nothing re-frames what a protocol already framed), **a clean close is null rather than an exception** (someone
      closing a tab is not an error path, and it latches so a receive loop cannot hang on it), and **concurrent sends
      are safe** (`ConduitSession` holds a send lock as of 0.3.5).

      The `protocolId` check lives here rather than in each author's handler: a frame under another id ends the
      session with `"unknown protocol"` and *tells the peer*, so someone who dialled the wrong site learns that
      instead of waiting. `OnSession(IConduitHandler)` reaches the rite's own names for anyone who wants them.

      **A conduit now round-trips over a real transport.** `PilgrimageOverVesselTests` starts a node, accepts a
      Pilgrimage over a TCP vessel and echoes a frame back — the first time any rite reached a site over something
      other than a browser DataChannel. It checks the Oracle beside the conduit on purpose, so a future failure says
      which of the two broke.

      **Still untested: a conduit over WebRTC.** The browser gate proves Mode 1 for the Oracle and the Auspice, but
      the reference client opens no conduit, so the browser path remains unexercised. Banter's web head will be the
      first to do it. The client half needs no Nodestar code — `ShrineSession.Conduits` comes from CupriNet.

- [x] **Mode 2 binds the page before serving it.** A Document-tier page is a template whose `{{ }}` placeholders a
      Mode-1 client resolves; the gateway used to hand that template straight to a browser, which rendered the braces
      as literal text. Every Document-tier site was affected, including the onion and tunnel deployments that are
      Mode 2 by necessity. The gateway now substitutes dotted paths and expands `data-repeat` against the feed
      snapshot it already had, HTML-escaping every value.

      **Escaping is the security boundary, not tidiness.** In Mode 1 a feed value reaches CupriFace, which has no
      script engine, so an injected `<script>` is inert. Mode 2 hands the same value to a real browser, which runs
      it — the identical payload is harmless on one path and script injection on the other.

      Deliberately a subset: dotted paths and `data-repeat`, which is the whole surface the tier uses. It is not a
      template language and should not become one. Not CupriFace either — the server carries no UI runtime, and
      CupriFace offers no way to hand back a bound document (`BuildAriaHtml` emits landmarks and drops the content),
      so there was nothing to reuse even at the cost of the dependency.

## Client

- [x] **A site declares its own feed name.** `<meta name="cupri-feed" content="…">`, read by the client before it
      attends and by the gateway before it binds, so Mode 1 and Mode 2 agree instead of both assuming `"overlay"`.
      Markup rather than a response header because `ServeStaticFiles` is the common case and cannot set one.

      The browser gate's own feed is now called `gate`, which is what makes the declaration load-bearing: a client
      that ignored it would attend `"overlay"`, receive nothing, and fail every feed assertion. The scan is
      duplicated between client and server — a wasm build carrying CupriFace cannot share an assembly with one that
      must stay free of a UI runtime — so it is deliberately the same algorithm written twice, pinned by
      `DeclaredFeedTests`.
- [x] **Resize handling.** The canvas's pixel buffer now tracks its CSS box (via `ResizeObserver`, plus a window
      listener for the monitor-scale case where `devicePixelRatio` changes while the element does not), and the frame
      pump repaints when it notices a new size. Previously the buffer was sized once at boot and the browser scaled
      that bitmap to fit, so resizing stretched and blurred the page instead of re-rendering it. CupriFace was never
      the limit here — it re-lays-out at whatever size `Render` is handed.
- [x] **A site declares the size it was authored for.** `<meta name="cupri-design" content="800x600">`, so hybrid
      zoom fits a page against what it actually is rather than an assumed 1024×768. The assumption was wrong in both
      directions: a page written for a narrow column was scaled down as though it wanted a thousand pixels, and a
      wide one was squeezed. Clamped rather than trusted — a declared zero would be divided by.

      The gate declares a non-default size precisely so the painter and the hit test have to agree about it: both
      derive from one `Zoom()`, and if only one read the declaration the pointer test would start missing what it
      can plainly see.

- [x] **Horizontal overflow at narrow widths — fixed upstream, and this entry was stale.** A `cupri1…` address is
      ~62 characters with no break opportunity, and `word-break` / `overflow-wrap` were no-ops in CupriFace 0.2.11
      ([CupriFace#59](https://github.com/Wixely/CupriFace/issues/59)). They work on **0.3.0**, which this repository
      already pins: a 62-character address in a 200px box now wraps and keeps its ink inside the box, measured with a
      render probe rather than taken from a changelog.

      Recorded because the entry claimed otherwise for several versions. A note that something is broken is worth
      re-testing on a bump; carrying one that has quietly become false is how a workaround outlives its reason.
- [~] **Scrolling works within a page; a whole page still scales rather than scrolls.** The wheel now reaches the
      document, so a scrollable region moves and repaints — verified in Chromium. What hybrid zoom still does is
      scale a tall PAGE down to fit rather than letting it scroll, so a page far taller than the viewport shrinks
      until it is unreadable. A site can now declare its design size, which is the honest fix for a page that knows
      its own shape — what remains is what to do with one that does not say, which is a policy question rather than
      a missing capability.
- [x] **Input reaches the document.** Pointer, wheel and keyboard are carried into CupriFace, so an L2 site can be
      clicked, scrolled and typed into rather than being a live picture. The cursor is driven from the document's own
      hit test, which is the only affordance a canvas-painted site has — without it a link is indistinguishable from
      a paragraph until you click it.

      Events queue in JavaScript and are drained once per frame, for the same reason inbound frames are: calling into
      wasm from a DOM handler re-enters the runtime at an arbitrary point. Pointer moves coalesce; discrete events
      never do. The hit-test mapping shares `Zoom()` with the painter rather than recomputing it — the symptom of
      those drifting apart is a page that looks right and answers clicks a few pixels away from where they landed.

      Proven in real Chromium by two gate tests: hovering resolves a pointer cursor, and the wheel scrolls a region
      and changes the picture. Both SWEEP the canvas rather than aiming at coordinates, because where an element
      lands depends on the zoom the page was fitted at — computing that in the test would be reimplementing the
      renderer's arithmetic, and getting it subtly wrong looks exactly like the feature being broken.

      **Delivery is ordered and never torn, but not retried** — CupriNet 0.3.7 wrote that contract down after we
      asked. A receiver that lets frames queue past the mux's per-stream limit loses the ones past it *silently*:
      the sender's write reports success and no field in a frame reveals the gap. Only the Epistle has a Vigil.

      `SiteSession` is built around that rather than passing it on. A background reader takes frames off the rite as
      fast as they arrive, so the queue that drops silently stays empty; if the author's handler falls behind it is
      *our* bounded queue that fills, and the session ends with an exception naming the problem. A protocol that
      quietly loses messages does not fail, it corrupts — so falling behind is loud, and it happens earlier and more
      cheaply than the transport's own limit would.

      That also means the handler shape first shipped here was the wrong one to advertise: receive, process inline,
      receive again is exactly what fills the queue. Take the frame and come straight back; do the work elsewhere.

      **Keys are forwarded and nothing consumes them yet**, which is a CupriFace boundary rather than a gap here.
      Measured against 0.3.0 and 0.5.0: an `<input>` in ordinary markup is not focusable, and `DispatchKey` answers
      false for the arrows, space and End even with the pointer over a scrollable region. The client therefore only
      claims a key while the engine reports a focused field — today never — so Tab, space and the arrows keep their
      browser behaviour instead of being swallowed for nothing. Nothing here changes when the engine gains focusable
      text; there is simply no way to gate-test it until then.
- [x] **Back.** The client keeps where the visitor has been and the chrome has a Back control, disabled until there
      is somewhere to go so it never lies about being able to. Each entry remembers whether it was the serving node,
      because going back has to restore the ability to auto-reconnect to that node and must not claim an HTTP
      relationship with a pasted link that this page does not have.

      A reconnect to the same node is deliberately NOT pushed: Back should walk the places the visitor went, not a
      node's outages. Still absent is roaming — links remain the only way in, so there is nowhere to go that you do
      not already hold a link for.
- [x] **Reconnects after the serving node restarts.** Detected in ~7s (a `disconnected` connection state plus a
      grace timer, rather than waiting ~30s for ICE consent freshness to expire), then a backoff that re-fetches the
      link over HTTP before dialling. The re-fetch is mandatory, not an optimisation: a restarted node regenerates
      its ICE credentials and DTLS certificate, so the link a page booted with is permanently dead.
- [~] **A pasted link cannot be reconnected to — softened, and deliberately not solved.** Auto-reconnect works only
      for the node that served the page: a restarted node regenerates its ICE credentials and DTLS fingerprint, so
      the link that reached it is permanently dead, and the only way to get a fresh one is an HTTP fetch — which this
      page can do with its origin and nowhere else.

      Two things now blunt it. **Back** returns to wherever the visitor came from, so a dead node is no longer a dead
      end. And the link that just ended is **put back in the address bar**, so returning once the node is up is one
      click rather than a hunt for a link the visitor may no longer have. The page refuses to overwrite anything
      already typed, so the suggestion can never cost an edit in progress.

      **The obvious fix is declined on purpose.** A node could serve links for peers it knows — it is in the overlay
      and Constellation already draws that map. But asking node A for node B's link tells A exactly which site you
      are about to visit, and "nobody else learns of the visit" is the property the whole no-signalling design exists
      to protect. Misdirection is not the risk (the Pilgrimage pins the Signet, so a substituted node fails the
      handshake) — disclosure is. It would also work only sometimes, because A's record of B is useful only if it was
      refreshed after B restarted, which is exactly the moment it will not have been. The real answer is L1 roaming,
      and a Pilgrim skips the overlay join by design, which is what makes a visitor leave no trace.

## Infrastructure

- [x] **CI runs, and is green.** Pushed to [Wixely/CupriNodestar](https://github.com/Wixely/CupriNodestar) (private);
      run #1 passed every job — both solutions built and tested, the CupriFace boundary enforced on a clean runner,
      Mode 1 exercised in real Chromium, and runnable examples produced for three RIDs. None of the failures
      [PUSHING.md](PUSHING.md) predicted actually happened; the feed authenticated on the automatic `GITHUB_TOKEN`.
      Roughly 13 minutes end to end.

## Upstream

- [x] **A right-click no longer activates whatever it lands on.** This client's hand-written input layer forwarded
      every `pointerdown` as a press regardless of button, so a right-click pressed the thing it was meant to open a
      menu about. Both press and release are now filtered to the left button — the release too, or the document
      holds a press it never received, which is how a stuck `:active` happens.

      Found by reading CupriFace 0.9.0's release notes: it fixed exactly this in both of ITS web hosts. Because this
      client wrote its own input layer rather than using theirs, it had the bug independently and inherited no fix.

      **Not covered by the gate, and the reason is worth recording** so nobody assumes it was overlooked. A test
      needs a click to have an observable effect, and on this canvas it has none: `:active` produces no repaint at
      all (measured — press and release both returned `Tick` false with zero damage rects), and anchors are
      swallowed by the host. There is nothing on the page a click visibly changes.

- [~] **Stage 1 of the host adoption: the paint path now runs through CupriFace's browser host.** `BrowserRenderer`
      stopped rendering — no Skia surface, no hand-applied zoom, no premultiplied-to-straight readback, no blit. It
      drives `WebHostCore.Init` and `WebHostCore.Tick` and adapts this client's input to them; `CupriFaceBridge`
      receives what the host wants to say to the page.

      **The loop is deliberately NOT surrendered.** `WebHost.Run` would own the frame pump, and this client's pump
      is also the async pump that NativeAOT-LLVM wasm has no event loop for. So the host is ticked from the loop
      that already existed.

      **Three things measured on desktop before any of it was written**, which is the payoff of the spike's finding
      that the host is portable managed code:
      - Input arrives in HOST (device) pixels, not logical ones — a pointer at device (120,40) over a 2x document
        highlighted an element occupying logical (0,0)-(80,30). Dividing by the zoom, as the old renderer had to,
        would now apply it twice.
      - A press and a release raise a click by themselves, exactly once. The page used to send a third, synthetic
        click event; forwarding it as well would have activated every link twice. It is gone, and the click count
        rides on the press.
      - `Tick` answers false when nothing changed, so an idle page still costs no pixels.

      Hybrid zoom moved into `SiteApp.Present`, with the same arithmetic as the `Zoom()` it replaces. What changes is
      who owns it: the host scales the canvas AND divides pointer positions by one number, where before painting and
      hit-testing were kept in step by both calling one private method — a discipline rather than a guarantee.

      Gained, none of which this client had: the ARIA mirror (a canvas announces itself and nothing inside it, so
      every site was a blank page to a screen reader), damage-rect painting, and the host driving the cursor.

      **Following an `<a href>` inside a site works, off the engine's own `CupriDocument.Navigated` event.** A
      click stays on the open Pilgrimage and costs one Oracle round trip; the connection, handshake and pinned
      Signet are untouched. `Navigated` carries every href a click resolved to, with `External` already separating
      one a host should open in a browser from one the app should route. Fragment-only hrefs do not arrive, which
      is right — they move within a page.

      **Two wrong turns got there, and both are worth keeping.** The first: `OnClick("a", …)` never fires, because
      the engine's link branch claims the click. The second: `IWebBridge.Navigate` fires only for the EXTERNAL
      subset, so relative and custom-scheme links look dropped. Together those made in-site links look impossible,
      and this was briefly written up as such and reverted.

      It was then rebuilt on a hit test — `HitTest` at the release point, walking `RenderNode.Parent` to an element
      with an `href` — which worked and was the wrong answer. `Navigated` is the intended one, needs no coordinate
      arithmetic, and covers activations a pointer release does not.

      **The finding under both mistakes is about tooling, not CupriFace.** The reflection dump used to survey the
      API printed properties and methods, and neither FIELDS nor EVENTS. `RenderNode.Element` is a field;
      `CupriDocument.Navigated` is an event. Each absence became a confident claim — one of them nearly an upstream
      bug report. A survey that silently omits two member kinds is worse than no survey.

      Answered upstream by [CupriFace #89](https://github.com/Wixely/CupriFace/issues/89), documented in 0.10.0 with
      no behaviour change. **Verified on 0.8.0**, which this repository is pinned to, rather than assumed from the
      release note: relative, absolute and custom-scheme all fire there identically, so no upgrade is needed.

      Where a site may send a visitor stays one function: another page of this site, or a `cupri1…` node, and
      nothing else.

- [~] **Stage 1 of the host adoption: the paint path now runs through CupriFace's browser host.** `BrowserRenderer`
      stopped rendering — no Skia surface, no hand-applied zoom, no premultiplied-to-straight readback, no blit. It
      drives `WebHostCore.Init` and `WebHostCore.Tick` and adapts this client's input to them; `CupriFaceBridge`
      receives what the host wants to say to the page.

      **The loop is deliberately NOT surrendered.** `WebHost.Run` would own the frame pump, and this client's pump
      is also the async pump that NativeAOT-LLVM wasm has no event loop for. So the host is ticked from the loop
      that already existed.

      **Three things measured on desktop before any of it was written**, which is the payoff of the spike's finding
      that the host is portable managed code:
      - Input arrives in HOST (device) pixels, not logical ones — a pointer at device (120,40) over a 2x document
        highlighted an element occupying logical (0,0)-(80,30). Dividing by the zoom, as the old renderer had to,
        would now apply it twice.
      - A press and a release raise a click by themselves, exactly once. The page used to send a third, synthetic
        click event; forwarding it as well would have activated every link twice. It is gone, and the click count
        rides on the press.
      - `Tick` answers false when nothing changed, so an idle page still costs no pixels.

      Hybrid zoom moved into `SiteApp.Present`, with the same arithmetic as the `Zoom()` it replaces. What changes is
      who owns it: the host scales the canvas AND divides pointer positions by one number, where before painting and
      hit-testing were kept in step by both calling one private method — a discipline rather than a guarantee.

      Gained, none of which this client had: the ARIA mirror (a canvas announces itself and nothing inside it, so
      every site was a blank page to a screen reader), damage-rect painting, and the host driving the cursor.

      **Following an `<a href>` inside a site works, by hit-testing rather than by anything the host offers.**
      Clicking a link stays on the open Pilgrimage and costs one Oracle round trip; the connection, the handshake
      and the pinned Signet are untouched.

      The host is no help here, which is worth recording because it sends you the wrong way. Measured on 0.9.0:

      | clicked | result |
      |---|---|
      | `<a href="https://example.com/">` | `IWebBridge.Navigate` fires |
      | `<a href="/second.html">` | nothing |
      | `<a href="cuprinet://x">` | nothing |
      | `OnClick("a", …)` | never fires — even for the absolute one |
      | `OnClick("button", …)` | fires normally |

      So the host consumes anchor clicks and re-emits only absolute `http(s)` ones — the right shape for "open this
      in a browser tab", the wrong one for in-app routing, and precisely backwards for a client whose only valid
      links are the ones it drops. The answer is `HitTest` at the release point, walking `RenderNode.Parent` to the
      nearest `Element` carrying an `href`.

      **A correction, because it was nearly acted on.** This was first written up as impossible, and an upstream
      issue was almost filed saying so. The claim rested on "`RenderNode` exposes no element" — which came from an
      API dumper that printed properties and methods and **not fields**. `Element`, `Tag`, `Parent` and `Children`
      are fields. A gap in the tool became a stated fact about someone else's library.

      Where a site may send you is a security boundary, so it is one function: another page of this site, or a
      `cupri1…` node, and nothing else. `http:`, `javascript:`, `data:` and `mailto:` are refused and logged.

- [~] **Stage 1 of the host adoption: the paint path now runs through CupriFace's browser host.** `BrowserRenderer`
      stopped rendering — no Skia surface, no hand-applied zoom, no premultiplied-to-straight readback, no blit. It
      drives `WebHostCore.Init` and `WebHostCore.Tick` and adapts this client's input to them; `CupriFaceBridge`
      receives what the host wants to say to the page.

      **The loop is deliberately NOT surrendered.** `WebHost.Run` would own the frame pump, and this client's pump
      is also the async pump that NativeAOT-LLVM wasm has no event loop for. So the host is ticked from the loop
      that already existed.

      **Three things measured on desktop before any of it was written**, which is the payoff of the spike's finding
      that the host is portable managed code:
      - Input arrives in HOST (device) pixels, not logical ones — a pointer at device (120,40) over a 2x document
        highlighted an element occupying logical (0,0)-(80,30). Dividing by the zoom, as the old renderer had to,
        would now apply it twice.
      - A press and a release raise a click by themselves, exactly once. The page used to send a third, synthetic
        click event; forwarding it as well would have activated every link twice. It is gone, and the click count
        rides on the press.
      - `Tick` answers false when nothing changed, so an idle page still costs no pixels.

      Hybrid zoom moved into `SiteApp.Present`, with the same arithmetic as the `Zoom()` it replaces. What changes is
      who owns it: the host scales the canvas AND divides pointer positions by one number, where before painting and
      hit-testing were kept in step by both calling one private method — a discipline rather than a guarantee.

      Gained, none of which this client had: the ARIA mirror (a canvas announces itself and nothing inside it, so
      every site was a blank page to a screen reader), damage-rect painting, and the host driving the cursor.

      **Following an `<a href>` inside a site is NOT POSSIBLE on CupriFace 0.8.0 or 0.9.0.** It was built, it did
      not work, and it is reverted. Measured on the desktop host, which is where this is cheap to establish:

      | clicked | result |
      |---|---|
      | `<a href="https://example.com/">` | `IWebBridge.Navigate` fires |
      | `<a href="/second.html">` | nothing |
      | `<a href="cuprinet://x">` | nothing |
      | `OnClick("a", …)` | never fires — even for the absolute one |
      | `OnClick("button", …)` | fires normally, so the selector mechanism works |

      The host consumes anchor clicks and re-emits only absolute http(s) ones, which is the right shape for "open
      this in a browser tab" and the wrong one for in-app routing. `HitTest` cannot rescue it either: `RenderNode`
      exposes neither the element nor a bound model, so knowing the x,y is not enough.

      CupriFace 0.9.0 closed [#85](https://github.com/Wixely/CupriFace/issues/85) — `OnContext` and `LastContext` —
      but that is context-menu shaped and does not reach an ordinary left-click on an anchor. Worth an upstream
      issue of its own; the evidence above is the issue.

      **A caution about how this was nearly missed.** The gate test written for it passed 7/7 while proving nothing:
      it looked for "second page" in the accessibility mirror, which was also the text of the link on the FIRST
      page, so it matched before anything was clicked. Only a mutation run — pointing the link off-network, which
      the client must refuse — exposed it, by passing when it could not possibly have. A marker has to be absent
      from the page you start on.

- [~] **Adopt `CupriFace.Web.NativeAot` as the client's host — spiked, and the answer is yes.** CupriFace 0.8.0
      ships the browser host as a package for the runtime this client actually uses (NativeAOT-LLVM, not Mono),
      carrying the frame loop, damage-rect blitting, pointer/touch/wheel/keyboard, a touch recogniser, the ARIA
      mirror a screen reader reads, IME composition, the clipboard and browser-decoded video.

      This repository hand-wrote roughly 2,000 lines of that because the package did not exist, and it silently
      lacks touch, ARIA, IME and clipboard.

      **The three questions are answered, by running the host rather than reading it.**

      1. *Can it be re-pointed at a new document mid-session?* **Yes.** `WebHostCore.Init(app, configure, bridge)`
         called a second time with a different `CupriApp` yields a NEW document carrying the new content — checked
         by reading `BuildAriaHtml` before and after, which went from FIRST to SECOND, with
         `ReferenceEquals(before, after)` false. Navigation is therefore a re-`Init`, not `CupriApp` gymnastics.
      2. *How does our async browse loop coexist with the host's frame pump?* **We keep our loop.**
         `WebHostCore.Tick(width, height, nowMs)` is public, returns whether it painted, and synchronously drives
         layout → paint → `IWebBridge.Present` → `PublishAria`. So we call `Tick` from the loop we already have
         instead of surrendering it to `WebHost.Run`, and `MarkDirty()` asks for a frame.
      3. *Does `Present` supersede `Zoom()` or fight it?* **The question conflated two different methods.**
         `IWebBridge.Present(pixels, byteCount, w, h, dx, dy, dw, dh)` is the damage-rect blit — the output path,
         which replaces ours. `CupriApp.Present(float, float) -> PresentInfo(LogicalWidth, LogicalHeight, Scale)`
         is the fit-to-design hook, and it supersedes our `Zoom()` cleanly: same computation, returned rather than
         applied by us. User zoom is a third thing again (`CupriDocument.Zoom`, `PageZoomEnabled`, `ZoomIn/Out`),
         which this client does not have at all.

      **The finding that changes the risk, and was not on anyone's list.** All of the above was measured on DESKTOP
      .NET with a stub `IWebBridge` — no browser, no wasm. `WebHostCore` and `IWebBridge` are portable managed code,
      so the host integration becomes testable in the ordinary suite instead of only through the eight-minute
      browser gate. A migration that could only be checked by the gate is a different proposition from one where
      each step has a unit test.

      **What it replaces:** `BrowserLoop` (105), `BrowserRenderer` (376), `BrowserInput` (131) and most of
      `imports.js` (531) — about 1,143 lines — become an `IWebBridge` implementation plus a `CupriApp` subclass.
      The package ships 302 lines of JS that do more. `BrowserDataChannel`, `BrowserNavigation`, `SiteManifest`
      and `FeedModel` are CupriNet concerns and stay.

      **Suggested staging**, each step gated: implement `IWebBridge` over the existing JS interop and drive `Tick`
      from the current loop; then adopt the package's `imports.js`/`main.js` and delete ours; then take the
      capabilities we never had. The ILC feed it needs is already configured here.

- [x] **CupriFace 0.2.12 imported.** Each fix re-verified with a headless render probe rather than taken on trust:
      **#54** `transform-origin` (the sparklines now tween instead of stepping), **#53** `:root` custom properties
      (the palette moved back off `body`), **#55** percentage height on a block child. Browser gate re-run, since a
      renderer bump moves every pixel.

      Still open upstream and still worked around here: **#51** grid `auto-fill/minmax` (flex wrap instead), **#56**
      layout properties do not animate (irrelevant now that transforms anchor correctly), **#59** unbreakable tokens
      (hidden by hybrid zoom rather than fixed), and new **#63** — `transform-origin: bottom center`, the two-keyword
      form, is still unparsed and silently centres. The single keyword is what this repository uses.

- [x] **[CupriNet#1](https://github.com/Wixely/CupriNet/issues/1)** — done, in CupriNet **0.3.3**. `ShrineSession`
      moved to a socket-free `CupriNet.Shrine` package beside a static `Pilgrimage.OverVesselAsync`, with the
      namespace kept and a `TypeForwardedTo` so nothing downstream had to be renamed. `BrowserPilgrim.cs` is deleted:
      there is one implementation of the handshake again.
- [x] **[CupriFace#51](https://github.com/Wixely/CupriFace/issues/51)** — `repeat(auto-fill, minmax(…))` collapsed
      grid tracks on 0.2.11. It lays out correctly on **0.3.0**, verified by a render probe: three cards in a 400px
      grid span the full width instead of piling into a sliver. **#63** (the two-keyword `transform-origin`) is fixed
      there too — both keyword forms now put the scale origin in the same place.

      The Constellation sample still uses flex wrap where it wanted a grid. That is now a choice rather than a
      workaround; the comment in its markup still calls it the latter, and the layout has no visual test behind it,
      so it is worth changing deliberately rather than in passing.
- [x] **[CupriNet#3](https://github.com/Wixely/CupriNet/issues/3)** — done, in CupriNet **0.3.6**. `ConduitHost`,
      `IConduitHandler`, `DelegateConduitHandler`, a fourth `HostShrine` overload and `ShrineSession.Conduits`, in
      `CupriNet.Shrine` so a WASM build reaches them. `OnSession` is built on it.

      **0.3.5 is skipped deliberately, and the reason is worth keeping.** It shipped the seam, but a sealed conduit
      was followed by a read that never returned: the host holds the visit open for the other rites, so no close was
      coming. "Translate the seal, then expect null" — the obvious handler, and the one this repository would have
      written — hung rather than failed. 0.3.6 latches the seal. Two of the corrections upstream made were to claims
      in the issue that were simply wrong: the Conduit *was* already reachable on the channel path
      (`ArcanumSession.Conduits`), and `ReceiveAsync` was already nullable. The second came from reading the API
      through a reflection dump, which cannot see nullable annotations — compile a probe against the package instead.

      Also settled there, and relied on above: `ConduitFlags.Reserved` is the mask to test against (never `Sealed`),
      the 192 KiB ceiling is measured on the payload *before* padding, `ProtocolId` is the consumer's to choose with
      no registry, and a single reader is assumed on receive.
