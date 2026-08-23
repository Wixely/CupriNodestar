using CupriNet.Nodestar;
using CupriNet.Nodestar.Tor;

// The reference host: everything here is configuration, not code. An operator edits appsettings.json (or sets
// CUPRINET_NODESTAR_* / passes --flags) and gets a running node that serves a directory on L2.
var builder = NodestarApplication.CreateBuilder(args);

// Tor, only when asked for. Conditional rather than unconditional because building the transport starts an embedded
// Tor client, and the overwhelmingly common case is a clearnet node that should not pay for one. Without this the
// EnableTor and TorOnly settings below would be inert — the node would refuse to start rather than quietly serve
// clearnet, which is the correct refusal, but an operator wants the flag to work rather than to be caught by it.
if (builder.Node.EnableTor || builder.Node.TorOnly)
    builder.UseTor();

// The one thing this host adds over the library defaults: a content directory to serve. It is read from the same
// configuration as everything else, so it can come from the file, the environment, or the command line.
var contentRoot = builder.Configuration["SiteRoot"] ?? "l2-wwwroot";
Directory.CreateDirectory(contentRoot);
builder.Site.ServeStaticFiles(contentRoot);

await using var app = builder.Build();
await app.RunAsync();
