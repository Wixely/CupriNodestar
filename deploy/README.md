# Running a Nodestar with Compose

```sh
docker compose -f deploy/docker-compose.yml up
```

Then open <http://127.0.0.1:8080>. That is the whole clearnet path: a node holding a site on L2, and an HTTP
gateway making it readable by a browser that knows nothing about CupriNet.

Building the image needs a GitHub PAT with `read:packages`, because the CupriNet packages are on a private feed:

```sh
export GITHUB_PACKAGES_TOKEN=ghp_…
docker compose -f deploy/docker-compose.yml build
```

It is passed as a build secret, so it is not recorded in the image or its history. Running an image you already
have needs no token.

## Set your address

The setting a containerised node most often needs is the address visitors reach it at:

```sh
cp deploy/.env.example deploy/.env      # then set NODESTAR_PUBLIC_HOST
```

A node advertises what its own interfaces report, and in a container that is the bridge — one was observed
advertising `127.0.0.1` as its only beacon, so every visitor dialled their own loopback. The browser client dials
the first non-onion beacon in the link and has no fallback to the origin the page came from.

Leave it unset and the node says so at startup, which is the point of the line:

```
info: Browsers will dial 203.0.113.7:47654 (Manual). If that is not the address your visitors can reach, set PublicHost.
warn: This node's link carries no clearnet address, so a browser has nothing to dial and every Mode 1 visit will fail.
```

Purely local use — a browser on the same machine as the node — needs none of this. The gateway is reached over
whatever address you typed, and no beacon is involved.

## The Wards

The bounds and deadlines that stop one visitor taking the node off the air — a global visit cap, a per-address
cap, an idle deadline, and the overlay's equivalents. They are CupriNet's, and a Nodestar now forwards them:

```sh
CUPRINET_NODESTAR_Wards__MaxPilgrimagesPerAddress=64
CUPRINET_NODESTAR_Wards__PilgrimageIdleTimeout=00:30:00
```

or in `appsettings.json`:

```json
{ "Nodestar": { "Wards": { "MaxPilgrimagesPerAddress": 64, "PilgrimageIdleTimeout": "00:30:00" } } }
```

**Leave them alone unless something is actually wrong.** Every one is unset by default and CupriNet's own value
applies, which is deliberate: a number pinned here would keep applying after CupriNet changed it, including after
CupriNet changed it *because it turned out to be exploitable*. The node logs which ones you overrode at startup
and says nothing when you overrode none.

The one most likely to need changing is **`MaxPilgrimagesPerAddress`** (8 on CupriNet 0.6.2). It counts concurrent
visits from one source address, which is the right defence for a public site and the wrong one when every visitor
arrives from the same place — behind a corporate NAT, a CGNAT, or a reverse proxy that does not preserve the
client address. There the ninth simultaneous visitor is turned away by a defence aimed at somebody else.

**`PilgrimageIdleTimeout`** (5 minutes) does not do what its name suggests to a live feed: traffic in *either*
direction keeps a visit alive, so a browser attending an Auspice and sending nothing is not idle. Raise it for
visitors who genuinely sit still — a page left open in a tab — not for feeds.

`Wards:EnableToll=false` removes the cost of arriving, which is what makes every other bound expensive to
exhaust. The node warns when you do it.

## Your site

Anything in `deploy/site` is served as the site, and edits are live: the next request gets the new file, with no
rebuild and no restart. Replace `index.html` and reload — measured at about 200ms, which was the first time it was
asked rather than a delay.

## Mode 1

Two ways. **Unzip a browser client bundle into `deploy/client`** and the node serves `/_nodestar/app` — see
`deploy/client/README.md`. Or **build the image that ships one**:

```sh
docker build --target mode1 -f node/cuprinet-nodestar/Dockerfile -t cuprinet-nodestar:mode1 .
```

That unpacks the published client package rather than compiling wasm, so it needs no Emscripten and no bundle in
the build context — about 24 MB larger than the default image, almost all of it renderer. Pin which client with
`--build-arg CLIENT_VERSION=0.1.0-alpha.10`.

The default image deliberately carries no client: a deployment behind a tunnel or an onion cannot use Mode 1 at all
and should not haul a renderer around to not use it.

Both UDP and TCP on the overlay port are published, because WebRTC is UDP — with only the TCP half published the
dial fails and nothing says why.

## Over Tor

```sh
docker compose -f deploy/docker-compose.yml --profile onion up
```

A second service, behind a profile so it is never started by accident. It publishes **no ports at all**: that is
the entire point, and it is why this is a separate service rather than a flag on the first one. An onion node with
ports still published is a node whose IP is discoverable, which is the one thing Tor was chosen to prevent.

Read the address out of the log — `Tor face: http://<address>/`. A cold bootstrap takes minutes and reports
progress as `Tor [nn%]`.

**This has never been run.** Tor is compiled in and wired, and the image needs nothing extra for it, but no circuit
has ever been opened from this repository — no machine here has Tor access. Its bootstrap, its onion address and a
browser reaching it are all unproven. Treat your first run as the first real test.

Onion delivery is Mode 2 only. WebRTC is clearnet UDP and does not cross a Tor circuit, so an onion visitor gets
server-rendered snapshots; live feeds are a Mode 1 capability.

## What persists

The node's identity lives in a named volume (`nodestar-data`), separate from your site. Lose it and the site's
`cupri1…` address changes, which makes it a different site to everyone who had the old one. `docker compose down`
keeps it; `down -v` destroys it.

The onion service has a volume of its own. Two nodes cannot share one — the data directory holds the node's keys,
and pointing both at it would have two processes contending for the same overlay identity.

## Health

Both services report health by asking their own gateway for `/healthz` over bash's `/dev/tcp`, since the runtime
image carries no curl or wget. `docker compose ps` shows the result. It proves the HTTP front is answering, not
that your site is correct — a node with an empty site directory is healthy and serves 404s.
