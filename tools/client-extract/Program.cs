using System.Reflection;

// Writes the embedded browser client out as files, for an image that serves it from ClientRoot.
//
// The bundle is embedded with LogicalName "client.<file>" — a FLAT set, because the published wasm output has no
// subdirectories. That is what makes the reverse mapping unambiguous: strip the prefix and what remains is the
// file name, dots and all ("client.dotnet.native.wasm" -> "dotnet.native.wasm"). If the bundle ever gains a
// subdirectory the names become ambiguous, so this refuses rather than guessing where the boundary lies.

var into = args.FirstOrDefault() ?? "/out";
Directory.CreateDirectory(into);

var assembly = Assembly.Load("CupriNet.Nodestar.Client.CupriFace");
const string prefix = "client.";

var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

if (names.Length == 0)
{
    // The package restored but carries nothing. That is what an unpopulated bundle looks like: the client project
    // globs `client\**\*`, and the glob is empty until the wasm has been published at least once.
    Console.Error.WriteLine(
        $"'{assembly.GetName().Name}' {assembly.GetName().Version} contains no embedded client. The package was "
        + "built before its bundle was staged, so there is nothing to serve — a Mode-1 image built from it would "
        + "answer 404 at /_nodestar/app.");
    return 1;
}

long total = 0;
foreach (var name in names)
{
    var file = name[prefix.Length..];

    using var stream = assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"resource '{name}' was listed but could not be opened");

    var path = Path.Combine(into, file);
    using (var target = File.Create(path)) stream.CopyTo(target);

    total += new FileInfo(path).Length;
    Console.WriteLine($"  {file} ({new FileInfo(path).Length:N0} bytes)");
}

Console.WriteLine($"{names.Length} files, {total / 1024.0 / 1024.0:F1} MB, from {assembly.GetName().Name}");

// The one file the page cannot start without, named explicitly so a bundle that restored but is somehow partial
// fails here rather than as a blank canvas in someone's browser.
if (!File.Exists(Path.Combine(into, "index.html")))
{
    Console.Error.WriteLine("the bundle has no index.html, so there is no page to serve at /_nodestar/app");
    return 1;
}

return 0;
