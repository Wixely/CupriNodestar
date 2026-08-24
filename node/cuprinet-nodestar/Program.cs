using CupriNet.Nodestar;
using CupriNet.Nodestar.Tor;
using CupriNet.Nodestar.WebRtc;
using Microsoft.Extensions.Logging;

// The reference host: everything here is configuration, not code. An operator edits appsettings.json (or sets
// CUPRINET_NODESTAR_* / passes --flags) and gets a running node that serves a directory on L2.
var builder = NodestarApplication.CreateBuilder(args);

// Tor, only when asked for. Conditional rather than unconditional because building the transport starts an embedded
// Tor client, and the overwhelmingly common case is a clearnet node that should not pay for one. Without this the
// EnableTor and TorOnly settings below would be inert — the node would refuse to start rather than quietly serve
// clearnet, which is the correct refusal, but an operator wants the flag to work rather than to be caught by it.
if (builder.Node.EnableTor || builder.Node.TorOnly)
    builder.UseTor();

// The browser on-ramp, when asked for and when it can work. Onion-only mode skips it on purpose: WebRTC is a
// clearnet UDP transport, so offering it there would publish the very IP the onion exists to hide.
if (builder.Node.EnableWebRtc && !builder.Node.TorOnly)
    builder.UseWebRtc();

// The one thing this host adds over the library defaults: a content directory to serve. It is read from the same
// configuration as everything else, so it can come from the file, the environment, or the command line.
var contentRoot = builder.Configuration["SiteRoot"] ?? "l2-wwwroot";
Directory.CreateDirectory(contentRoot);
builder.Site.ServeStaticFiles(contentRoot);

// The client, as FILES rather than as a package.
//
// Accepting browser connections and deciding what runs in the browser are separate choices, and this host declines
// to make the second one. Referencing CupriNet.Nodestar.Client.CupriFace would be one line and would drag a renderer
// into every deployment of this image — including onion and gateway-only ones that never serve a client at all, and
// including operators who prefer a different one. CupriFace is this project's preference, not the platform's
// requirement, and this host is the part most likely to be run by someone who does not share it.
//
// So: drop a client into ClientRoot and it is served; leave the directory empty and /_nodestar/app simply is not
// there. The published bundle from a Nodestar release unzips straight into it.
var clientRoot = Path.GetFullPath(builder.Configuration["ClientRoot"] ?? "client");
var client = new DirectoryClientAssets(clientRoot);
var clientAvailable = client.HasContent;
if (clientAvailable) builder.ServeClient(client.Get);

await using var app = builder.Build();
await app.StartAsync();

// Said after the node is up, so it sits with the rest of the startup state rather than scrolling past before it.
// The silent-mismatch case is the one worth naming: a node accepting DataChannels while serving no client looks
// misconfigured from a browser and perfectly healthy from the logs.
if (builder.Node.EnableWebRtc && !builder.Node.TorOnly && !clientAvailable)
    app.Logger.LogInformation(
        "WebRTC is on but no browser client is present in {ClientRoot}, so /_nodestar/app is not served. The node is "
        + "still dialable by a client the visitor already has; to serve one, unzip a client bundle into that "
        + "directory or set ClientRoot.", clientRoot);

await app.RunAsync();
