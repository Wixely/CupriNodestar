using System.Net;
using System.Text;

namespace CupriNet.Nodestar;

/// <summary>
/// Renders the intonation page: the node's connection link, its QR, and the site's <c>cupri1…</c> address.
///
/// <para>Hand-written HTML with no external resources — no CDN, no font fetch, no analytics. That is deliberate: this
/// page is served by onion deployments too, where a single third-party request would deanonymise the visitor. It also
/// means the page renders identically with no network beyond the one that fetched it.</para>
/// </summary>
internal static class IntonationPage
{
    public static string Render(LinkSnapshot snapshot, string? siteAddress, NodestarOptions options)
    {
        var page = new StringBuilder(Template);
        page.Replace("{{network}}", Escape(options.Concordium));
        page.Replace("{{link}}", Escape(snapshot.Link));
        page.Replace("{{qr}}", Escape(snapshot.QrDataUri));
        page.Replace("{{site}}", Escape(siteAddress ?? "(no site hosted)"));
        page.Replace("{{moniker}}", Escape(string.IsNullOrWhiteSpace(options.Moniker) ? "" : options.Moniker));
        return page.ToString();
    }

    // Everything interpolated here is either operator-supplied config or node-minted, but it is all encoded anyway:
    // a Moniker is self-asserted and unverified, so treating it as untrusted is the only safe default.
    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    private const string Template = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="robots" content="noindex">
          <title>Nodestar</title>
          <style>
            :root { color-scheme: light dark; --fg:#111; --dim:#666; --bg:#fafafa; --card:#fff; --line:#e3e3e3; --accent:#7a5cff; }
            @media (prefers-color-scheme: dark) {
              :root { --fg:#e8e8ea; --dim:#9a9aa2; --bg:#141416; --card:#1c1c20; --line:#2c2c33; --accent:#9d86ff; }
            }
            * { box-sizing: border-box; }
            body { margin:0; padding:2rem 1rem; background:var(--bg); color:var(--fg);
                   font:16px/1.55 system-ui,-apple-system,Segoe UI,Roboto,sans-serif; }
            main { max-width: 44rem; margin: 0 auto; }
            h1 { font-size:1.35rem; margin:0 0 .25rem; letter-spacing:-.01em; }
            .sub { color:var(--dim); margin:0 0 1.5rem; font-size:.92rem; }
            .card { background:var(--card); border:1px solid var(--line); border-radius:12px;
                    padding:1.1rem 1.25rem; margin-bottom:1rem; }
            .label { font-size:.72rem; text-transform:uppercase; letter-spacing:.09em; color:var(--dim); margin-bottom:.45rem; }
            code { font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace; font-size:.86rem;
                   word-break:break-all; display:block; }
            .addr { color:var(--accent); font-weight:600; }
            .qr { display:flex; justify-content:center; padding:.5rem 0 0; }
            .qr img { width:min(15rem,60vw); height:auto; image-rendering:pixelated;
                      background:#fff; padding:.6rem; border-radius:8px; }
            footer { color:var(--dim); font-size:.8rem; text-align:center; margin-top:1.5rem; }
            .stale { opacity:.45; transition:opacity .2s; }
          </style>
        </head>
        <body>
        <main>
          <h1>Nodestar {{moniker}}</h1>
          <p class="sub">A CupriNet node hosting a site on L2. Network: <strong>{{network}}</strong></p>

          <section class="card">
            <div class="label">Site address</div>
            <code class="addr" id="site">{{site}}</code>
          </section>

          <section class="card">
            <div class="label">Connection link</div>
            <code id="link">{{link}}</code>
            <div class="qr"><img id="qr" src="{{qr}}" alt="QR code for this node's connection link"></div>
          </section>

          <footer>The link rotates periodically. This page refreshes it on its own.</footer>
        </main>
        <script>
          // Poll rather than reload: the link is the only thing that changes, and a reload would fight the QR image
          // cache. Failures are shown by dimming, not by an error — a node that briefly cannot mint is not broken.
          const link = document.getElementById('link');
          const qr = document.getElementById('qr');
          const site = document.getElementById('site');
          const body = document.body;
          async function refresh() {
            try {
              const r = await fetch('link.json', { cache: 'no-store' });
              if (!r.ok) throw new Error(r.status);
              const d = await r.json();
              link.textContent = d.link;
              qr.src = d.qr;
              if (d.site) site.textContent = d.site;
              body.classList.remove('stale');
            } catch {
              body.classList.add('stale');
            }
          }
          setInterval(refresh, 30000);
        </script>
        </body>
        </html>
        """;
}
