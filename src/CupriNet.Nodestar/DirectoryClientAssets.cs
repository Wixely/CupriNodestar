using System.Collections.Concurrent;

namespace CupriNet.Nodestar;

/// <summary>
/// Serves a browser client from a directory on disk — the bring-your-own-client path.
///
/// <para>It exists so a host can offer Mode 1 without compiling a client into itself. Nodestar's reference client is
/// a NuGet package (<c>CupriNet.Nodestar.Client.CupriFace</c>), which is convenient but takes a hard dependency on
/// this project's renderer of choice; pointing at a folder takes none. An operator unzips whatever client they
/// prefer — ours, or anything that speaks the CupriNet client protocol — and the host serves it.</para>
///
/// <para>This is what keeps a turnkey host honest about <c>EnableWebRtc</c>: the transport gives browsers something
/// to dial, and this gives them something to run, without the host ever referencing a UI runtime.</para>
/// </summary>
public sealed class DirectoryClientAssets
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, ClientAsset?> _cache = new();

    public DirectoryClientAssets(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
    }

    /// <summary>Whether the directory exists and holds anything. A host should say so rather than serve 404s.</summary>
    public bool HasContent => Directory.Exists(_root) && Directory.EnumerateFiles(_root).Any();

    /// <summary>The configured directory, for diagnostics.</summary>
    public string Root => _root;

    /// <summary>
    /// Resolves one file of the client, or null if it is not there.
    ///
    /// <para>The path arrives from an HTTP request, so it is <b>untrusted</b>: it is combined with the root and the
    /// result is checked to still be under the root before anything is read. Without that, <c>../</c> segments walk
    /// out of the client directory and the host becomes a reader of arbitrary files — the data directory holding the
    /// node's secrets very much included. Rooted paths and drive-qualified paths are refused for the same reason,
    /// because <see cref="Path.Combine(string, string)"/> silently discards the root when the second argument is
    /// absolute.</para>
    /// </summary>
    public ClientAsset? Get(string path) => _cache.GetOrAdd(path, Load);

    private ClientAsset? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal)) return null;

        var full = Path.GetFullPath(Path.Combine(_root, path));

        // The separator matters: without it "/client-evil" would pass a plain StartsWith("/client") check.
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(full, _root, StringComparison.Ordinal))
            return null;

        if (!File.Exists(full)) return null;

        return new ClientAsset(File.ReadAllBytes(full), ContentTypeFor(full));
    }

    /// <remarks>
    /// <c>application/wasm</c> is load-bearing: served as <c>application/octet-stream</c> the browser refuses to
    /// stream-instantiate the module and the client fails to start with no obvious cause.
    /// </remarks>
    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".wasm" => "application/wasm",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".dat" or ".bin" => "application/octet-stream",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };
}
