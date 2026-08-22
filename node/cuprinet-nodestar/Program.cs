using CupriNet.Nodestar;

// The reference host: everything here is configuration, not code. An operator edits appsettings.json (or sets
// CUPRINET_NODESTAR_* / passes --flags) and gets a running node that serves a directory on L2.
var builder = NodestarApplication.CreateBuilder(args);

// The one thing this host adds over the library defaults: a content directory to serve. It is read from the same
// configuration as everything else, so it can come from the file, the environment, or the command line.
var contentRoot = builder.Configuration["SiteRoot"] ?? "l2-wwwroot";
Directory.CreateDirectory(contentRoot);
builder.Site.ServeStaticFiles(contentRoot);

await using var app = builder.Build();
await app.RunAsync();
