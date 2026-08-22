# Probes

Throwaway projects that answer one question each, and are **not part of the product build** — nothing in
`Nodestar.slnx` references them. They exist because the browser client rests on assumptions that are cheap to test now
and expensive to discover later.

Each publishes for `browser-wasm` via **NativeAOT-LLVM** under `TrimMode=full`, and each **runs** rather than merely
linking. Trimming is left on deliberately: an untrimmed pass would hide reflection the trimmer breaks later and pass
for the wrong reason.

| Probe | Question | Answer |
|---|---|---|
| `WasmCrypto` | Does BouncyCastle survive the toolchain? | Yes — 418 KB gzipped |
| `WasmClient` | Does `CupriNet.Hosting`'s Pilgrim path? | Yes — 594 KB gzipped |
| `WasmRender` | Does CupriFace, alongside all of it? | Yes — 4.5 MB gzipped |

## Running one

```bash
dotnet publish probe/WasmRender/WasmRender.csproj -c Release
```

Then run the published program under the Emscripten `node` that ships with the `wasm-tools` workload:

```bash
cd probe/WasmRender/bin/Release/net10.0/browser-wasm/publish
"$(dotnet --list-sdks >/dev/null; echo /c/Program\ Files/dotnet)/packs/Microsoft.NET.Runtime.Emscripten.3.1.56.Node.win-x64/10.0.10/tools/bin/node.exe" \
  --experimental-global-webcrypto WasmRender.js
```

Two notes on that command, both of which cost time to work out:

- **`--experimental-global-webcrypto`** — Node 18 has no global `crypto.subtle`, which the .NET runtime's bootstrap
  wants. A browser has it; this is a Node-only wrinkle and says nothing about our code.
- **`DotNetJsApi` is off** in these projects. With it on, the native module is wired to a loader script and stops
  being standalone, so it cannot be run directly. A real client will want that interop layer — this is a property of
  the probes, not a recommendation.

## What they are careful about

**Reaching the code.** `TrimMode=full` removes what it can prove unreachable, so a probe that merely *referenced*
types would link successfully and prove nothing. Every result is printed. `WasmClient` goes further: the Pilgrim calls
sit behind `if (args.Length > 99)` — never executed, because a browser has no sockets, but the trimmer cannot fold a
runtime condition away, so the path is genuinely compiled and trimmed.

**Touching the framebuffer.** `WasmRender` reads a pixel back after painting and checks it is the colour the
stylesheet asked for, then PNG-encodes the surface. A render that "succeeds" without producing pixels would be a
false pass.

## What they do not answer

Speed. Compilation and size are settled; **handshake latency and time-to-first-paint on a mid-range phone are not**,
and the probes use `-O1` rather than the `-O3` + `wasm-opt` a shipping bundle would.
