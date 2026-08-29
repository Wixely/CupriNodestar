# NodestarSite

A CupriNet node that hosts a site at a self-authenticating `cupri1…` address.

```
dotnet run
```

Then open <http://localhost:8080/_nodestar> for the node's link and QR code, or
<http://localhost:8080/> for the site itself.

## What you just started

Two ways to reach the same site:

- **Over WebRTC**, the real path. A browser loads the served client, dials this node back with no signalling
  server — the link it needs is inlined in the page and signed — and fetches the site over L2. The page is
  rendered without a browser engine or a JavaScript engine.
- **Over HTTP**, the gateway. The same site rendered server-side, for anywhere WebRTC cannot reach: behind a
  Cloudflare tunnel, or over Tor.

## The files

| | |
|---|---|
| `Program.cs` | what this node is and what it serves |
| `l2-wwwroot/index.html` | the site — one self-contained document |
| `.nodestar/` | **the site's identity.** Delete it and the address changes |

## The address is the data directory

`.nodestar/` holds the Signet, which *is* the `cupri1…` URL people link to. Losing it does not reset a cache; it
publishes a different site at a different address, and every link to the old one is dead. Nothing warns you, because
minting a fresh identity looks exactly like a first run. Back it up like a TLS private key.

## Sizes

Every message a rite carries is capped at **192 KiB** — the size a browser's SCTP association will carry. A page, a
feed message and a session frame are each bounded by it. For anything bigger, publish it as a relic
(`builder.Site.ServeRelics(...)`): it travels chunk by chunk and is verified against a manifest before any bytes are
handed over, so a visitor can prove a blob's integrity before running it.
