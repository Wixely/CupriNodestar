# Serving this site over Tor

`UseTor()` is wired in, so these settings reach a real onion transport rather than being decoration:

```
CUPRINET_NODESTAR_EnableTor=true     # onion alongside clearnet
CUPRINET_NODESTAR_TorOnly=true       # onion only — no clearnet, and WebRTC is refused
CUPRINET_NODESTAR_TorFacePort=8080   # also publish the HTTP front as an onion
```

Two onions come out of it: the **overlay onion**, by which another node reaches this Shrine over Tor, and — when
`TorFacePort` is set — the **face onion**, by which a browser reaches the HTTP front.

`TorOnly` deliberately refuses WebRTC. WebRTC is a clearnet UDP transport, so offering it would publish the very IP
the onion exists to hide.

**Bootstrapping takes minutes on a cold start**, and the progress is logged rather than swallowed — watch for
`Tor [nn%]`. Nothing in the Nodestar repository has ever opened a real circuit, so treat your first run as the first
real test of this path.
