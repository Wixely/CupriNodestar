# The browser client goes here

Empty, and that is a working state: with nothing in this directory the node serves **Mode 2** — it makes the L2
visit itself and hands the browser ordinary HTML. No client is involved, so none is needed.

Unzip a client bundle here to add **Mode 1**, where the browser makes the visit itself over WebRTC and the node is
a peer rather than a server. `/_nodestar/app` then serves it. The directory is mounted read-only; nothing in the
node writes to it.

This file exists so the directory does, because Docker creates a missing bind-mount source as a root-owned
directory, and the container runs as `app`.
