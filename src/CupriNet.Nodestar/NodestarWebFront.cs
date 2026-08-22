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
