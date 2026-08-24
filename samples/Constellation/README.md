# Constellation

A Nodestar whose website **is** the node's own view of the network. Run two *nodes* and each appears in the other's
page — arriving over the very connection being visualised.

> **Peers are nodes, not viewers.** The feed projects the L1 overlay map, so an entry means another CupriNode with a
> durable identity and a signed record. A browser visitor is a **Pilgrim**: throwaway identity, and the Pilgrimage
> skips the overlay join by design, so it leaves no trace to project and never appears here. Opening more tabs
> changes nothing — which is the privacy property working, not a bug.

It ships **no compiled UI and references no CupriFace**: HTML, CSS, and a live feed, served over L2. Rendering is the
client's job. That is the claim the sample exists to make.

## What is on the page

Everything is **measured**. There is no synthetic series anywhere in this sample, and that is the point: a sine wave
would animate just as prettily and prove nothing, whereas a stalled feed here shows up as stalled numbers.

Two figures answer to **you**, and they are the ones to watch:

| Figure | What it is | What moves it |
|---|---|---|
| **Watching now** | Concurrent Auspice emanations. The rite starts one per attending session. | Open a second tab — it reads 2. |
| **Pages served** | Oracle consults answered for this site. | Navigate or revisit — it climbs. |

The rest is process-level truth from the runtime: uptime, CPU as a percentage of one core (so >100 is legitimate on a
multi-core box), working set, thread count, gen-0 collections, and three rolling 32-sample sparklines — **CPU**,
**allocation rate**, and this feed's **own output in bytes/sec**.

Then `Peers`, which is the L1 overlay map and the redaction boundary the sample's tests pin.

> **Why no per-rite traffic chart.** Bytes/sec split across Oracle, Auspice and Conduit would be the obvious headline.
> `CupriNode` exposes no such counters, so there is no honest way to report them — the feed's own output is counted
> instead, because that is a number this sample genuinely owns. Inventing the rest and labelling it "traffic" would
> have been easy and would have been a lie.

> **Why the charts step rather than glide.** CupriFace tweens `transform` but not layout properties, so a *gliding*
> bar needs `scaleY` — and the engine ignores `transform-origin`, scaling about the element's centre, which turns a
> rising series into a symmetric bowtie ([CupriFace#54](https://github.com/Wixely/CupriFace/issues/54)). A correct
> chart that steps once a second beats a smooth one that looks broken. The breathing dot beside **Live** is the
> continuously-animated element, and it is load-bearing: a feed that has silently died otherwise looks exactly like
> a quiet one.

## The demo, from VS Code

**Run → "Demo: two nodes + browser"**. That builds the client, stages it, starts two independent nodes, and opens the
browser client on alpha once its web front is actually listening.

Then, to move between nodes:

1. **Ctrl+Shift+P → Run Task → "demo: copy beta's link"** — beta's link lands on the clipboard.
2. Paste it into the client's address bar and press **Go**.

The chrome status changes from alpha's `cupri1…` to beta's, and the page becomes beta's own site. The status line and
the site's own connection panel are independent claims that should agree — the chrome is the one to trust, because a
site cannot write there.

That copy step is the one part that cannot be automated: a link is minted at runtime and carries live reachability,
so there is nothing sensible to hard-code and nothing to pre-fill.

**The two nodes deliberately do not know each other.** Navigation is dialling a link, not following the overlay — so
`Peers` stays `0 of 0` on both. If you also want them listing each other, launch **"Constellation: beta (seeded from
alpha)"** instead and paste alpha's link (there is a task for that too) at the prompt.

> Both nodes must pass `--AdvertiseSiteInLink true`, and the configs do. A Pilgrim pins the site's Signet, so a node
> that does not put one in its link cannot be visited at all — the client reports *"that node advertises no site in
> its link"*.

## Run one

```bash
dotnet run --project samples/Constellation
```

Then open <http://localhost:8080/>. The node's link and QR are at `/_nodestar`.

## Run two, and watch them find each other

The interesting demo needs a second node seeded from the first.

```bash
# Terminal 1 — alpha
dotnet run --project samples/Constellation -- \
  --ListenPort 47990 --WebPort 8090 --Moniker alpha --DataDirectory data/alpha

# Grab alpha's link
curl -s http://localhost:8090/_nodestar/link.json

# Terminal 2 — beta, seeded from that link
dotnet run --project samples/Constellation -- \
  --ListenPort 47991 --WebPort 8091 --Moniker beta --DataDirectory data/beta \
  --SeedLinks:0="cuprinet://intone/…"
```

Open <http://localhost:8090/> and <http://localhost:8091/> side by side. Each lists the other, and the **`self`**
fingerprint on one page is the **peer** fingerprint on the other — which is the whole point: the page is describing
the network that delivered it.

## What it demonstrates

- **A site served entirely over L2** — the page and its stylesheet are separate Oracle consults, because an Oracle
  response is one message and the transport caps a message at 256 KiB.
- **A live feed** (`overlay`) over the Auspice rite: snapshot first, then updates, so a viewer arriving mid-stream is
  never connected-but-empty.
- **The connection panel** — naming the peer you are attached to, so navigating between nodes is unmistakable.

## What it demonstrates about *not* publishing things

`OverlayFeed.Project()` is the part worth reading. `ConstellationEntry` bundles a peer's **signed, redistributable
record** together with **this node's private judgements about that peer** — `Bucket`, `Standing`, `Taint` — plus
`Source` (how we came to know them) and `Slash24` (their subnet). Serialising the entry leaks all five by accident.

So the projection is an **allow-list**, not a deny-list. The obvious version — serialise the entry and strip the bad
fields — starts leaking silently the day a field is added upstream. Naming each published field means a new one has to
be deliberately let out.

The rule it follows: *if the control plane wouldn't serve it to a peer, don't push it to an anonymous visitor.*

## Today it is Mode 2

There is no browser client yet, so the page polls the gateway's snapshot endpoint
(`/_nodestar/feed/overlay`) — a server-rendered point-in-time view, which is all a page with no WebRTC can have.
When the client lands, it attends the same feed and the same payload arrives pushed instead of polled.
