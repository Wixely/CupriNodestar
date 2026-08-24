# TODO

What is designed but not built. Kept separate from `design/nodestar.md` so that document can describe the intended
system, while this one is honest about what a clone actually gets today.

## Deployment

- [x] **Dockerfile** for the reference host (Mode 2). Written; **the image has never been built** — Docker is not
      installed on the development machine. Verified by other means: the `dotnet publish` line, the portable-IL
      output (no apphost, so one image runs on every arch), the `dotnet cuprinet-nodestar.dll` entrypoint, and the
      `CUPRINET_NODESTAR_DataDirectory` / `SiteRoot` environment variables all work. **Unverified: the Docker layers
      themselves** — base images, the BuildKit secret mount, the non-root user, the volume.
- [ ] **Build the image once** and correct whatever the above missed.
- [ ] **`docker-compose`** — clearnet and onion. The onion variant is now buildable (Tor is wired); it has never run.
- [ ] **Mode 1 from the container is unverified.** The reference host now serves a browser client from `ClientRoot`,
      and that path is proven outside a container — a bundle in the directory, a real browser, dial through to paint.
      What no one has tried is the container itself: the `/client` bind mount, the `app` user's read access to it,
      and whether inbound UDP reaches the container at all in a given deployment.
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

      **Still unproven:** nobody has restored them FROM the feed and built against one. Every reference in this
      repository is a `ProjectReference`, so a missing transitive dependency would look exactly like today — green
      tests, working demo, broken for everyone else.
- [ ] **`dotnet new nodestar-site` template.** The design's headline promise — "zero → running site in one
      `dotnet new` + one `dotnet run`" — and the single largest gap between what the README says and what exists.

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
- [ ] **Hosted apps (the Relic tier).** Now unblocked. A Shrine could name relics through `IRelicSource` and a client
      could `FetchRelicAsync` a WASM blob, verify it against the manifest, and only then run it — which is the whole
      point: integrity is proven *before* execution, so a hostile host can fail a fetch but cannot corrupt one. The
      pieces exist upstream and nothing here calls them yet. `SiteBuilder` would need a `ServeRelics(...)` alongside
      `ServeStaticFiles`, and the browser client a way to be told which relic to run.
- [ ] **The 192 KiB page ceiling is undocumented for site authors.** As of 0.3.4 `StaticFileOracleHandler` refuses an
      over-ceiling file at the rite, with a message naming the Relic rite, instead of failing later at the transport.
      That is a much better failure — but nothing in this repository's docs tells someone dropping a large image into
      `l2-wwwroot` that a ceiling exists at all, so they meet it as a surprise rather than a constraint.

## Client

- [ ] **A site cannot declare its feed name.** The client attends `"overlay"` and nothing else, so every
      Document-tier site must call its feed that. A client that renders whatever site it is pointed at should not
      know any site's feed names — the name wants to come from the site, via a header on the Oracle response or an
      attribute in its markup.
- [x] **Resize handling.** The canvas's pixel buffer now tracks its CSS box (via `ResizeObserver`, plus a window
      listener for the monitor-scale case where `devicePixelRatio` changes while the element does not), and the frame
      pump repaints when it notices a new size. Previously the buffer was sized once at boot and the browser scaled
      that bitmap to fit, so resizing stretched and blurred the page instead of re-rendering it. CupriFace was never
      the limit here — it re-lays-out at whatever size `Render` is handed.
- [~] **Horizontal overflow at narrow widths — mitigated, not fixed.** A `cupri1…` address is ~62 characters with no
      break opportunity and nothing can break it: `word-break`, `overflow-wrap` and `word-wrap` are all no-ops in
      CupriFace 0.2.11 ([CupriFace#59](https://github.com/Wixely/CupriFace/issues/59)). Hybrid zoom hides it, because
      the layout width never falls below the 1024px design width — the page scales down instead of reflowing
      narrower. The underlying inability to break a long token is untouched, and would resurface the moment a site
      opted out of scaling.
- [~] **The page can be taller than the canvas, and there is no scrolling.** Hybrid zoom largely answers this by
      scaling a tall page down to fit rather than clipping it. It is not scrolling, though: a page far taller than
      the viewport scales down until it is unreadable, and there is still no way to move around one. Real scrolling
      needs input dispatch — see below.
- [ ] **No input reaches the document.** The client calls no `DispatchPointer`, `DispatchWheel` or `DispatchClick`,
      so an L2 site cannot be clicked, scrolled, pinched or typed into — it is a live picture. Everything
      interactive CupriFace supports is unreachable until the client forwards events, which is the single largest
      capability gap in the client.
- [ ] **No history.** No back, and links are the only way in — there is no roaming to an address you do not already
      hold a link for.
- [x] **Reconnects after the serving node restarts.** Detected in ~7s (a `disconnected` connection state plus a
      grace timer, rather than waiting ~30s for ICE consent freshness to expire), then a backoff that re-fetches the
      link over HTTP before dialling. The re-fetch is mandatory, not an optimisation: a restarted node regenerates
      its ICE credentials and DTLS certificate, so the link a page booted with is permanently dead.
- [ ] **A pasted link cannot be reconnected to.** Auto-reconnect only works for the node that served the page,
      because refreshing a link means an HTTP fetch and the page has an HTTP relationship with its origin alone.
      When a node reached by pasted link restarts, the address bar is the only way back. A node could plausibly
      serve links for peers it knows, which would close this — it is a design question, not an oversight.

## Infrastructure

- [x] **CI runs, and is green.** Pushed to [Wixely/CupriNodestar](https://github.com/Wixely/CupriNodestar) (private);
      run #1 passed every job — both solutions built and tested, the CupriFace boundary enforced on a clean runner,
      Mode 1 exercised in real Chromium, and runnable examples produced for three RIDs. None of the failures
      [PUSHING.md](PUSHING.md) predicted actually happened; the feed authenticated on the automatic `GITHUB_TOKEN`.
      Roughly 13 minutes end to end.
- [ ] **The packages have never been consumed as packages.** Every reference in this repository is a
      `ProjectReference`; nobody has restored `CupriNet.Nodestar` from a feed and built against it. A missing
      transitive dependency would look exactly like today: green tests, working demo, broken for everyone else.

## Upstream

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
- [ ] **[CupriFace#51](https://github.com/Wixely/CupriFace/issues/51)** — `repeat(auto-fill, minmax(…))` collapses
      grid tracks. Worked around with flex wrap; no action needed here unless it is fixed.
