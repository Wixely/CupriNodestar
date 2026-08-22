using System.Collections.Concurrent;
using System.Reflection;

namespace CupriNet.Nodestar.WebRtc;

/// <summary>
/// Serves the browser client from resources embedded in this assembly, so a host ships the on-ramp without needing
/// the wasm toolchain or a build step of its own.
/// </summary>
internal static class EmbeddedClientAssets
{
    private const string Prefix = "client.";
    private static readonly Assembly Assembly = typeof(EmbeddedClientAssets).Assembly;
    private static readonly ConcurrentDictionary<string, ClientAsset?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ClientAsset? Get(string path) => Cache.GetOrAdd(path, Load);

    private static ClientAsset? Load(string path)
    {
        // Path traversal has no purchase here — resource names are a flat set fixed at build time, and a name that
        // is not in it simply does not resolve. Normalising separators is only about matching the LogicalName shape.
        var name = Prefix + path.Replace('/', '.').Replace('\\', '.').TrimStart('.');

        using var stream = Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new ClientAsset(memory.ToArray(), ContentTypeFor(path));
    }

    /// <summary>
    /// Content types for what a wasm client is actually made of.
    ///
    /// <para><c>application/wasm</c> is the load-bearing one: served as <c>application/octet-stream</c> the browser
    /// refuses to stream-instantiate the module and the client fails to start with no obvious cause. It is the single
    /// most common way to break a working wasm bundle at deploy time.</para>
    /// </summary>
    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wasm" => "application/wasm",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".dat" or ".bin" => "application/octet-stream",
        ".woff2" => "font/woff2",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
