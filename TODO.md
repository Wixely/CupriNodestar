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
- [ ] **`docker-compose`** — clearnet and, once Tor is wired, onion.
- [ ] **`deploy/` recipes** — IIS (`web.config` / ANCM), reverse proxy, Cloudflare tunnel, systemd, Windows service.
- [ ] **A Mode-1 image.** Needs the wasm bundle, which is a build output a fresh clone does not have. The intended
      answer is restoring `CupriNet.Nodestar.Client.CupriFace` from the feed rather than carrying the Emscripten
      toolchain in a build stage — which is why the bundle is embedded in a package at all. Blocked on publishing.

## Packaging

- [ ] **Publish the packages.** `dotnet pack` already produces all three cleanly (base 26 KB, `.WebRtc`, and the
      client at 5.4 MB — the split doing its job). Nothing pushes them: CI has no pack/push step, and there is no
      remote to push from.
- [ ] **`dotnet new nodestar-site` template.** The design's headline promise — "zero → running site in one
      `dotnet new` + one `dotnet run`" — and the single largest gap between what the README says and what exists.

## Not wired up

- [ ] **Tor.** `EnableTor` and `TorOnly` are honoured by `NodestarOptions` and change the node's `ReachabilityMode`,
      but no onion transport is ever constructed. `TorOnly` today produces a node that advertises no beacons at all
      rather than an onion one — worse than the flag not existing.
- [ ] **Reliquary → the Shrine path.** Gates the hosted-app tier: an Oracle response is one message and a WASM app
      blob is far past the 256 KiB ceiling, so there is no way to deliver one without it.

## Client

- [ ] **A site cannot declare its feed name.** The client attends `"overlay"` and nothing else, so every
      Document-tier site must call its feed that. A client that renders whatever site it is pointed at should not
      know any site's feed names — the name wants to come from the site, via a header on the Oracle response or an
      attribute in its markup.
- [ ] **No resize handling.** The canvas renders at whatever size it had on first paint.
- [ ] **No history.** No back, and links are the only way in — there is no roaming to an address you do not already
      hold a link for.

## Infrastructure

- [ ] **CI has never run.** There is no git remote: `.github/workflows/build.yml` is validated for YAML syntax and
      nothing else. The wasm workload install, the `playwright.ps1` path, and cross-repo feed authentication are all
      unverified.

## Upstream

- [x] **[CupriNet#1](https://github.com/Wixely/CupriNet/issues/1)** — done, in CupriNet **0.3.3**. `ShrineSession`
      moved to a socket-free `CupriNet.Shrine` package beside a static `Pilgrimage.OverVesselAsync`, with the
      namespace kept and a `TypeForwardedTo` so nothing downstream had to be renamed. `BrowserPilgrim.cs` is deleted:
      there is one implementation of the handshake again.
- [ ] **[CupriFace#51](https://github.com/Wixely/CupriFace/issues/51)** — `repeat(auto-fill, minmax(…))` collapses
      grid tracks. Worked around with flex wrap; no action needed here unless it is fixed.
