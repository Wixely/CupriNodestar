# Constellation

A Nodestar whose website **is** the node's own view of the network. Open two of them and each appears in the other's
page — arriving over the very connection being visualised.

It ships **no compiled UI and references no CupriFace**: HTML, CSS, and a live feed, served over L2. Rendering is the
client's job. That is the claim the sample exists to make.

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
