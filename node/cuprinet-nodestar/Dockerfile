# Nodestar — a CupriNet node that serves a site on L2.
#
# Build from the REPOSITORY ROOT, so the project references under src/ are in the context:
#   docker build -f node/cuprinet-nodestar/Dockerfile -t cuprinet-nodestar .
#
# SCOPE: this image is the reference host, which is Mode 2 — the site is rendered server-side and served over plain
# HTTP. That is the configuration that works in every deployment row, including the two that cannot carry inbound
# WebRTC at all (a Cloudflare tunnel, and Tor). It serves no browser client and therefore needs no wasm toolchain,
# which is what keeps this image small and its build ordinary. A Mode-1 image is a separate, heavier thing — see the
# note at the bottom.

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

RUN mkdir -p /data /site && chown app:app /data /site
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
# Serve the browser client (Mode 1). That needs the published wasm bundle staged into
# CupriNet.Nodestar.Client.CupriFace before build, and a fresh clone does not have it — the bundle is a build output,
# not a source file. Doing it properly means either a first stage carrying the wasm workload and Emscripten (a very
# large image for a step most deployments do not need) or restoring the client from the published package once it
# exists on the feed. The latter is the intended answer, and it is why the bundle is embedded in a package at all.
#
# Tor. CUPRINET_NODESTAR_EnableTor is honoured by the options but the onion transport is not wired into
# NodestarApplication yet, so setting it does nothing today.
