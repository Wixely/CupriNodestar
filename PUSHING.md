# Pushing this repository

**Done.** The repository is at [Wixely/CupriNodestar](https://github.com/Wixely/CupriNodestar) (private), and
`.github/workflows/build.yml` went green on its first run. What follows is kept as the record of how it was set up
and what the first run actually cost — see [what actually happened](#what-actually-happened-on-the-first-run).

## 1. Create the repository and push

```bash
gh repo create Wixely/CupriNodestar --private --source=. --remote=origin --push
```

Or without the CLI: create `Wixely/CupriNodestar` empty on github.com, then

```bash
git remote add origin https://github.com/Wixely/CupriNodestar.git
git push -u origin main
```

## 2. Give it a token for the Wixely feed — *not needed, as it turned out*

`CupriNet.*`, `CupriWebRTC` and `CupriFace` all come from **GitHub Packages under the Wixely account**, and the
automatic `GITHUB_TOKEN` is scoped to *this* repository — it can be refused for packages owned elsewhere, even in the
same account. CupriNet's own CI hits this and solves it the same way, which is why every job here reads
`secrets.PACKAGES_TOKEN || secrets.GITHUB_TOKEN`.

Create a classic PAT with **`read:packages`**, then:

```bash
gh secret set PACKAGES_TOKEN --repo Wixely/CupriNodestar
```

**If restore fails on the first run, this is almost certainly why.**

## 3. Watch it fail, and send the logs back

```bash
gh run watch
gh run view --log-failed
```

## What each job is for

| Job | What it proves |
|---|---|
| `build` | Both solutions compile; both test suites pass. Packs the prerelease packages. |
| `browser` | Mode 1 works in real Chromium — dial, Pilgrimage, fetch, render, live update. |
| `example` | Produces the download people can actually run. |
| `cupriface-boundary` | The node, both transports and the reference host carry no UI runtime, enforced rather than claimed. |
| `release` | On a `v*` tag: a **prerelease** GitHub Release with the examples and packages. |

## What actually happened on the first run

Run #1 was **green**, which is not what this section used to predict. Recorded because the predictions were wrong in
a useful direction, and because the timings are the thing worth knowing next time:

| Predicted failure | Outcome |
|---|---|
| **Feed authentication** — "the most likely failure by a distance" | Worked on the automatic `GITHUB_TOKEN`. No `PACKAGES_TOKEN` was ever set. Both repositories are owned by the same account, and that turned out to be enough. |
| **`wasm-tools` on `windows-latest`** | Installed cleanly, ~3.5 min. |
| **The `playwright.ps1` path** | Resolved exactly where expected. |
| **`--with-deps` on Windows** | Harmless, but the Chromium install is the slowest step in the run at ~4.5 min. |
| **Artifact size** | Not a problem; all three RIDs published and uploaded in well under a minute each. |

Whole run: about **13 minutes**, dominated by `browser` (wasm toolchain → wasm publish → Chromium → gate). The
`build` job finishes in under two.

Keep `PACKAGES_TOKEN` in mind anyway: the fallback works today because this repository and the packages share an
owner. If either moves, restore starts failing and step 2 becomes real again.

## Making a prerelease

```bash
git tag v0.1.0-alpha.1
git push origin v0.1.0-alpha.1
```

Packages are versioned `0.1.0-alpha.<run number>` on every build, so two artifacts are never confusable — which
matters when the whole point is somebody sending back a log and being asked *which build*.

Prerelease is deliberate, not modesty: nothing has been consumed by anyone, the deploy story is unfinished
(`TODO.md`), and the packaging changed recently enough that its shape should not be treated as settled.

## What the CI cannot tell you

- **The Docker image has never been built.** No Docker on the development machine, and no job builds it yet.
- **Tor is wired but has never carried a circuit.** `CupriNet.Nodestar.Tor` supplies the transport, `UseTor()` is
  wired through to the reference host, and the structural tests in `TorWiringTests` cover the opt-in, the config
  opt-out and the refusal. What none of that touches is the network: this machine has no Tor access, so no onion has
  ever been published and no circuit has ever been opened. **The first person to run with `EnableTor: true` is
  testing it for the first time** — the interesting log lines are the `Tor [nn%]` bootstrap progress and whatever
  `Tor face:` address it prints.
- **Nothing is published to a feed**, so the packages have never been consumed as packages — every reference in this
  repository is a `ProjectReference`.
