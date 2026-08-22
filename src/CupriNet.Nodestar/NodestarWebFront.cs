using System.Text;
using System.Text.Json.Nodes;
using CupriNet.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CupriNet.Nodestar;

/// <summary>
/// The clearnet HTTP front: the intonation page (this node's connection link + QR + the site's <c>cupri1…</c>
/// address) and a small JSON endpoint the page polls so the link refreshes without a reload.
///
/// <para>HTTP only, by design — put a reverse proxy in front for TLS, which is also how the IIS / Cloudflare / onion
/// deployment rows work. Kestrel rather than <c>HttpListener</c> because this front will later also serve the WASM
/// client and the Mode-2 server-rendered site, which want real routing and static-file handling.</para>
/// </summary>
internal sealed class NodestarWebFront(
    NodestarLinkProvider links,
    NodestarOptions options,
    Func<string?> siteAddress,
    SiteGateway? gateway,
    Func<string, ClientAsset?>? clientAssets,
    ILogger log)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();          // the host already owns console logging
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(options.WebPort);
            if (options.TorFacePort is int torPort && torPort != options.WebPort)
                k.ListenAnyIP(torPort);
            // Nothing here identifies the server: an onion that leaks "Kestrel" in a header is still a fingerprint.
            k.AddServerHeader = false;
        });

        var app = builder.Build();

        // Everything the NODE serves about itself lives under one reserved prefix, so the site owns the rest of the
        // namespace. A Nodestar behind a tunnel or an onion is a website host first: a visitor typing the hostname
        // wants the site, not an operator page.
        const string NodePrefix = "/_nodestar";

        app.MapGet(NodePrefix, (HttpContext ctx) =>
            Results.Content(IntonationPage.Render(Snapshot(ctx), siteAddress(), options), "text/html; charset=utf-8"));

        // Polled by the page. Kept separate from the HTML so a refresh costs a few hundred bytes, not a re-render.
        app.MapGet($"{NodePrefix}/link.json", (HttpContext ctx) =>
        {
            var snapshot = Snapshot(ctx);
            var json = new JsonObject
            {
                ["link"] = snapshot.Link,
                ["qr"] = snapshot.QrDataUri,
                ["generatedAt"] = snapshot.GeneratedAt.ToString("O"),
                ["site"] = siteAddress(),
                ["network"] = options.Concordium,
            };
            return Results.Content(json.ToJsonString(), "application/json; charset=utf-8");
        });

        // A liveness probe that says nothing about the node — useful behind a proxy, harmless to expose. Kept at the
        // root as well as under the prefix because proxies and orchestrators expect to find it there.
        app.MapGet("/healthz", () => Results.Text("ok"));
        app.MapGet($"{NodePrefix}/healthz", () => Results.Text("ok"));

        if (clientAssets is not null)
        {
            // Mode 1 lives under the node prefix rather than at the root. The gateway keeps "/" because it works in
            // every deployment row; the client needs a reachable WebRTC endpoint, which not every row has.
            // One catch-all, not two routes: a `**` segment also matches the empty string, so an explicit "/app/"
            // route beside it is ambiguous and every request 500s. Serve() maps empty to index.html.
            //
            // A redirect to the trailing-slash form does not work either � ASP.NET treats "/app" and "/app/" as the
            // same route, so it redirects to itself forever. Hence the <base> tag injected below: it fixes relative
            // URL resolution without depending on the shape of the request path at all.
            app.MapGet($"{NodePrefix}/app", () => Serve(""));
            app.MapGet($"{NodePrefix}/app/{{**path}}", (string path) => Serve(path));

            // The seed. The page needs this node's signed Intonation to dial back, and inlining it is what removes
            // the signalling server: the browser already holds the remote description before it opens a socket.
            app.MapGet($"{NodePrefix}/app/intonation.json", (HttpContext ctx) =>
            {
                var snapshot = Snapshot(ctx);
                return Results.Content(
                    new JsonObject { ["link"] = snapshot.Link }.ToJsonString(),
                    "application/json; charset=utf-8");
            });
        }

        if (gateway is not null)
        {
            // Mode 2: the site itself, rendered server-side. A point-in-time snapshot of a feed is available too, so
            // a gateway visitor sees live-ish data even though nothing can be pushed to them.
            app.MapGet($"{NodePrefix}/feed/{{name}}", async (string name, HttpContext ctx) =>
            {
                var snapshot = await gateway.SnapshotAsync(name, ctx.RequestAborted).ConfigureAwait(false);
                return snapshot is null
                    ? Results.NotFound()
                    : Results.Bytes(snapshot, "application/octet-stream");
            });

            // The catch-all goes last; the explicit routes above win on specificity, so a site can never shadow the
            // node's own endpoints by serving a file at that path.
            app.MapGet("/{**path}", gateway.HandleAsync);
            app.MapMethods("/{**path}", ["HEAD"], gateway.HandleAsync);
        }
        else
        {
            // With no gateway there is nothing to serve at the root, so send visitors to the page that does exist.
            app.MapGet("/", () => Results.Redirect(NodePrefix));
        }

        log.LogInformation("Web front listening on http://0.0.0.0:{Port}{Tor} — site at /, node at {Prefix}.",
            options.WebPort,
            options.TorFacePort is int p && p != options.WebPort ? $" (Tor face :{p})" : string.Empty,
            NodePrefix);

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serves one file of the browser client, or 404 when the client was not built into this host.</summary>
    private IResult Serve(string path)
    {
        var isIndex = string.IsNullOrEmpty(path) || path is "index.html";
        var asset = clientAssets?.Invoke(isIndex ? "index.html" : path);
        if (asset is null) return Results.NotFound();

        // The client's own markup uses relative urls, so it works unchanged whether it is served from "/app" or
        // "/app/" � or, later, from somewhere else entirely. Stamping the base at serve time is what makes the
        // bundle mount-point-agnostic instead of hard-coding where it lives.
        if (!isIndex) return Results.Bytes(asset.Content, asset.ContentType);

        var html = Encoding.UTF8.GetString(asset.Content)
            .Replace("<head>", $"<head><base href=\"{ClientPath}/\">", StringComparison.Ordinal);
        return Results.Content(html, asset.ContentType);
    }

    /// <summary>Where the browser client is mounted. Used for the injected &lt;base&gt; and nothing else.</summary>
    private const string ClientPath = "/_nodestar/app";

    /// <summary>
    /// Picks the link class by the port the request arrived on. This is the whole reason the Tor face is a separate
    /// listener: a visitor who came through the onion is handed an <b>onion-only</b> link, so the page can never leak
    /// the node's clearnet address to someone who deliberately avoided it.
    /// </summary>
    private LinkSnapshot Snapshot(HttpContext context)
    {
        var onTorFace = options.TorFacePort is int torPort && context.Connection.LocalPort == torPort;
        if (onTorFace) return links.Current(LinkTransports.OnionOnly);
        return links.Current(options.TorOnly ? LinkTransports.OnionOnly : LinkTransports.ClearnetOnly);
    }
}
