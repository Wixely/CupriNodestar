using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// The bring-your-own-client file server.
///
/// <para>Most of this is about the path, because the path comes from an HTTP request. The host's data directory —
/// master key, Signet, known peers — sits near the client directory in a normal deployment, so a resolver that can
/// be walked out of its root is not a 404 bug, it is key disclosure.</para>
/// </summary>
public class DirectoryClientAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nodestar-client-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside;

    public DirectoryClientAssetsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "index.html"), "<html></html>");
        File.WriteAllBytes(Path.Combine(_root, "app.wasm"), [0, 97, 115, 109]);
        File.WriteAllText(Path.Combine(_root, "sub", "nested.js"), "export {}");

        // A stand-in for the secrets that really do live beside a client directory.
        _outside = Path.Combine(Path.GetDirectoryName(_root)!, "nodestar-secret-" + Guid.NewGuid().ToString("N") + ".key");
        File.WriteAllText(_outside, "master-key-material");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (File.Exists(_outside)) File.Delete(_outside);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void It_serves_a_file_from_the_directory()
    {
        var asset = new DirectoryClientAssets(_root).Get("index.html");

        Assert.NotNull(asset);
        Assert.Equal("text/html; charset=utf-8", asset.ContentType);
    }

    [Fact]
    public void It_serves_a_nested_file()
        => Assert.NotNull(new DirectoryClientAssets(_root).Get(Path.Combine("sub", "nested.js")));

    /// <summary>Served as octet-stream the browser refuses to stream-instantiate the module, silently.</summary>
    [Fact]
    public void Wasm_is_served_as_application_wasm()
        => Assert.Equal("application/wasm", new DirectoryClientAssets(_root).Get("app.wasm")!.ContentType);

    [Fact]
    public void A_missing_file_is_null_rather_than_an_exception()
        => Assert.Null(new DirectoryClientAssets(_root).Get("nope.js"));

    /// <summary>
    /// The one that matters. Every shape here resolves outside the root, and each must be refused rather than read.
    /// </summary>
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("sub\\..\\..\\escape.txt")]
    public void It_refuses_to_walk_out_of_its_root(string path)
        => Assert.Null(new DirectoryClientAssets(_root).Get(path));

    /// <summary>
    /// Specifically proves the refusal protects a real file. The traversal tests above would pass just as well
    /// against a resolver that returned null because the target did not exist.
    /// </summary>
    [Fact]
    public void A_traversal_cannot_reach_a_file_that_really_is_there()
    {
        var relative = Path.GetRelativePath(_root, _outside);

        Assert.True(File.Exists(_outside), "the target must exist, or this proves nothing");
        Assert.StartsWith("..", relative, StringComparison.Ordinal);
        Assert.Null(new DirectoryClientAssets(_root).Get(relative));
    }

    /// <summary>
    /// Path.Combine DISCARDS its first argument when the second is absolute, so an absolute request would otherwise
    /// be served verbatim from anywhere on disk — the sharpest edge in this whole class.
    /// </summary>
    [Fact]
    public void It_refuses_an_absolute_path()
        => Assert.Null(new DirectoryClientAssets(_root).Get(_outside));

    [Fact]
    public void An_empty_directory_reports_no_content()
    {
        var empty = Path.Combine(Path.GetTempPath(), "nodestar-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            Assert.False(new DirectoryClientAssets(empty).HasContent);
            Assert.True(new DirectoryClientAssets(_root).HasContent);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
