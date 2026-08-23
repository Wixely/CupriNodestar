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

- [~] **Tor.** The seam is wired: `ConfigureOnionTransport` mirrors the WebRTC one, `IOnionTransport` comes from
      `CupriNet.Hosting` so the base package stays Tor-free, and a supplied transport reaches
      `CupriNodeOptions.OnionTransport`. Requesting Tor without one is now **refused at startup** rather than
      silently serving clearnet — an anonymity setting that quietly does not apply is the one failure mode it must
      never have.

      **Blocked on [CupriNet#2](https://github.com/Wixely/CupriNet/issues/2):** `CupriNet.Tor` is not published (its
      CI skips packing it), so there is no concrete `IOnionTransport` to reference. When it lands, the
      `CupriNet.Nodestar.Tor` package and `UseTor()` are about twenty lines. Writing our own binding over `CupriTor`
      instead would be the transcription mistake that #1 just removed.

      **Untestable here regardless:** this machine has no Tor network access, so even with the package the onion
      path could not be exercised locally.
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

- [ ] **CI has never run.** There is no git remote, so `.github/workflows/build.yml` is validated for YAML syntax and
      nothing else. See [PUSHING.md](PUSHING.md) for the setup and the list of things most likely to fail first
      (feed authentication, the wasm workload on a runner, the `playwright.ps1` path).
- [ ] **The packages have never been consumed as packages.** Every reference in this repository is a
      `ProjectReference`; nobody has restored `CupriNet.Nodestar` from a feed and built against it. A missing
      transitive dependency would look exactly like today: green tests, working demo, broken for everyone else.

## Upstream

- [x] **[CupriNet#1](https://github.com/Wixely/CupriNet/issues/1)** — done, in CupriNet **0.3.3**. `ShrineSession`
      moved to a socket-free `CupriNet.Shrine` package beside a static `Pilgrimage.OverVesselAsync`, with the
      namespace kept and a `TypeForwardedTo` so nothing downstream had to be renamed. `BrowserPilgrim.cs` is deleted:
      there is one implementation of the handshake again.
- [ ] **[CupriFace#51](https://github.com/Wixely/CupriFace/issues/51)** — `repeat(auto-fill, minmax(…))` collapses
      grid tracks. Worked around with flex wrap; no action needed here unless it is fixed.
