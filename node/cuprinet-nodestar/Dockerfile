# Nodestar — a CupriNet node that serves a site on L2.
#
# Build from the REPOSITORY ROOT, so the project references under src/ are in the context:
#   docker build -f node/cuprinet-nodestar/Dockerfile -t cuprinet-nodestar .
#
# SCOPE: this image ships Mode 2 working out of the box — the site rendered server-side and served over plain HTTP,
# the configuration that works in every deployment row including the two that cannot carry inbound WebRTC at all (a
# Cloudflare tunnel, and Tor).
#
# It also has the WebRTC transport and the onion transport compiled in, so EnableWebRtc and EnableTor are real
# settings rather than decoration. What it does NOT bake in is a browser client: no wasm toolchain runs here, which
# is what keeps the build ordinary and the image small. Mount a client bundle at /client and Mode 1 works too — see
# the note at the bottom.

# ---- build ----
# Pinned to BUILDPLATFORM so a multi-arch build does not emulate the whole SDK under QEMU. `dotnet publish` here
# produces portable IL (no RID, UseAppHost=false), which is arch-neutral — the same /app runs everywhere. Only the
# runtime stage below is resolved per target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .

# CupriNet.* comes from the Wixely GitHub Packages feed, so restore needs a read:packages token. Pass it as a
# BuildKit secret rather than a build arg — an ARG is baked into image history and readable by anyone who pulls it:
#   docker build --secret id=packages_token,env=PACKAGES_TOKEN -f node/cuprinet-nodestar/Dockerfile .
RUN --mount=type=secret,id=packages_token \
    sh -c 'TOKEN="$(cat /run/secrets/packages_token 2>/dev/null || true)"; \
      if [ -n "$TOKEN" ]; then \
        dotnet nuget update source GitHub-Wixely-Packages --username docker --password "$TOKEN" \
          --store-password-in-clear-text --configfile nuget.config; \
      fi; \
      dotnet restore node/cuprinet-nodestar/cuprinet-nodestar.csproj'

RUN dotnet publish node/cuprinet-nodestar/cuprinet-nodestar.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# ---- runtime ----
# aspnet, not runtime: the web front is Kestrel, so the ASP.NET Core shared framework has to be present.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.title="Nodestar"
LABEL org.opencontainers.image.description="A CupriNet node that hosts a site on L2, served over HTTP (Mode 2)."
LABEL org.opencontainers.image.source="https://github.com/Wixely/CupriNodestar"
LABEL org.opencontainers.image.licenses="MIT"

WORKDIR /app
COPY --from=build /app .

# THE DATA DIRECTORY IS THE ADDRESS.
#
# The site's Signet — its cupri1… address, which is its URL — is persisted here. A container that loses this volume
# comes back up as a different site: every link to it is dead, and nothing warns you, because minting a fresh Signet
# looks exactly like a first run. For a node this would be an inconvenience; for a site it is the whole identity.
# Mount it, back it up, and treat it as you would a TLS private key.
ENV CUPRINET_NODESTAR_DataDirectory=/data

# What the site serves. Mount your own over it: `-v ./my-site:/site:ro`.
ENV CUPRINET_NODESTAR_SiteRoot=/site

# The browser client, if you want Mode 1. Empty by default, and an empty directory means /_nodestar/app is simply not
# served — the node is still dialable by a client the visitor already has. Mount a bundle over it to serve one:
#   -v ./client:/client:ro
# Any client speaking the CupriNet client protocol works; the one from a Nodestar release unzips straight in. It is a
# mount rather than a build step so this image never carries a UI runtime it might not be asked to use.
ENV CUPRINET_NODESTAR_ClientRoot=/client

RUN mkdir -p /data /site /client && chown app:app /data /site /client
USER app
VOLUME ["/data"]

# The overlay listener (TCP). Publish with `-p 47654:47654`.
EXPOSE 47654
# The WebRTC endpoint (UDP, same number — WebRtcPort defaults to ListenPort). Only useful where inbound UDP can
# actually reach the container; behind a Cloudflare tunnel it cannot, which is why Mode 2 exists. Publish with
# `-p 47654:47654/udp` when you want browsers to be able to dial directly.
EXPOSE 47654/udp
# The clearnet web front: the site at /, the node's own page at /_nodestar. Publish with `-p 8080:8080`.
EXPOSE 8080

# Everything is configurable through CUPRINET_NODESTAR_* — see NodestarOptions. For example:
#   -e CUPRINET_NODESTAR_Concordium=example.chat
#   -e CUPRINET_NODESTAR_Moniker="my node"
#   -e CUPRINET_NODESTAR_AdvertiseSiteInLink=true
ENTRYPOINT ["dotnet", "cuprinet-nodestar.dll"]

# ---- what this image deliberately does NOT do ----
#
# BUILD a browser client. The wasm bundle is a build output, not a source file, so a fresh clone does not have one,
# and producing it here would mean a first stage carrying the wasm workload and Emscripten — a very large image for a
# step most deployments do not need. Mounting a bundle at /client is the answer instead, and it has the better
# property anyway: the client stays swappable, and this image never carries a renderer it was not asked to run.
#
# ---- what the first build actually proved ----
#
# BUILT AND RUN, 29 Aug 2026, on Docker 29.1.3. Every line above was reasoned rather than observed until then; what
# follows is what the first run confirmed, because "it built" and "it works" are different claims.
#
#   the BuildKit secret mount     restore authenticated against the private feed and the token is absent from
#                                 image history, which is the whole reason it is a secret and not an ARG
#   the portable-IL publish       one /app, no RID, no apphost
#   the `app` user and chown      the container runs as uid 1654(app), not root
#   Mode 2 end to end             a site bind-mounted at /site is served at / with X-Nodestar-Mode: gateway and
#                                 Cache-Control: no-store, and a {{ }} placeholder with no feed behind it binds to
#                                 empty rather than showing braces
#   Mode 2 IN A REAL BROWSER      headless Chromium on the host rendered it as ordinary DOM: zero canvas elements,
#                                 no client, no WebRTC, nothing on the console. This is the row that has to work
#                                 where inbound UDP cannot reach — a tunnel, an onion, or the double NAT below —
#                                 and it is the one path here proven from a container all the way to a browser
#   /healthz and /_nodestar       both answer; link.json returns the node's link as JSON
#   size                          350 MB
#
# MODE 1 SERVES CORRECTLY from a mounted /client, checked 30 Aug with the bundle bind-mounted read-only:
#
#   the app user reads it     the mount arrives root-owned mode 755 and uid 1654 reads it without a chown
#   the bundle is intact      /_nodestar/app/dotnet.native.wasm is served byte-for-byte, all 14,828,766 of them
#   the seed is served        /_nodestar/app/intonation.json answers, which is what lets a client dial with no input
#   a real browser boots it   headless Chromium on the host loaded the module and began a visit
#
# TWO THINGS TO SET, because the defaults are wrong for this and the failure is confusing rather than obvious:
#
#   CUPRINET_NODESTAR_AdvertiseSiteInLink=true — without it the served client boots, dials nothing, and repeats
#   "that node advertises no site in its link" forever. The link carries no Signet, so a Pilgrim has nothing to pin.
#   Publish UDP as well as TCP — `-p 47654:47654/udp`. Without it there is no WebRTC path at all.
#
# INBOUND UDP remains UNPROVEN, and the shape of what is known is worth stating exactly. With the port published and
# the site advertised, a browser on the host got as far as ICE `checking` and then `connection failed`. That is
# further than a missing route would reach, so signalling and the answer-from-the-link both work; what did not is
# the connectivity check. The likely cause is this machine rather than the image: the path was Windows → WSL2 → the
# docker bridge, two layers of NAT, and ICE is exactly the thing that does not survive source-address rewriting.
#
# The related observation, and probably the more important one for a deployment: a containerised node advertised
# 127.0.0.1 as its ONLY beacon here, because nothing it could see was routable — no port mapping, no reflexive
# discovery. A browser elsewhere would dial its own loopback. That is now fixable: set PublicHost to the address
# visitors reach and the link carries it instead.
#
#   -e CUPRINET_NODESTAR_PublicHost=203.0.113.7 -e CUPRINET_NODESTAR_PublicPort=47654
#
# The node prints what browsers will dial at startup, so a wrong address is visible in the log rather than only in a
# visitor's browser console.
#
# So: prove this on a native Linux Docker host, where there is one network namespace between the browser and the
# node instead of three.
#   TOR. Compiled in and wired, but no circuit has ever been opened from anywhere in this repository.
#   DOCKER COMPOSE, and any multi-container arrangement.
#
# One environment note, so the next person does not chase it: run under Docker inside WSL, a container stops a few
# seconds after the shell session that started it closes — cleanly, SIGTERM and exit 0. That is WSL reclaiming the
# distro, not the node deciding to stop. A container held open by a live session ran indefinitely.
#
# Tor is compiled in and wired (CUPRINET_NODESTAR_EnableTor / _TorOnly reach a real onion transport), but no circuit
# has ever been opened from anywhere in this repository — the development machine has no Tor access. Bootstrapping
# inside a container is specifically unproven.
#
# Mode 1 through a MOUNTED client directory has been verified outside a container — this host binary, a bundle in
# ClientRoot, and a real browser completing dial → DataChannel → Pilgrimage → Oracle → paint. What that leaves
# unproven here is the container specifically: the bind mount, the `app` user's read access to it, and inbound UDP
# reaching the container at all.
