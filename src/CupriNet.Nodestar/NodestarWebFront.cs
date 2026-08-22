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

        app.MapGet("/", (HttpContext ctx) =>
            Results.Content(IntonationPage.Render(Snapshot(ctx), siteAddress(), options), "text/html; charset=utf-8"));

        // Polled by the page. Kept separate from the HTML so a refresh costs a few hundred bytes, not a re-render.
        app.MapGet("/link.json", (HttpContext ctx) =>
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

        // A liveness probe that says nothing about the node — useful behind a proxy, harmless to expose.
        app.MapGet("/healthz", () => Results.Text("ok"));

        log.LogInformation("Web front listening on http://0.0.0.0:{Port}{Tor}.",
            options.WebPort,
            options.TorFacePort is int p && p != options.WebPort ? $" (Tor face :{p})" : string.Empty);

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
