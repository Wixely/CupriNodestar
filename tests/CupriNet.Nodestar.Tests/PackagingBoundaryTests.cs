using System.Text.RegularExpressions;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// Guards the packaging boundaries the design asserts, so they are checked rather than merely written down.
///
/// <para>Both invariants here are the kind that stay true by accident for a long time and then quietly stop: a
/// convenience <c>PackageReference</c> added to reach one type, a client bundle wired into the wrong package. Nothing
/// fails at that moment — the break only shows up as a deployment carrying weight it should not, or a boundary a
/// README still claims exists.</para>
/// </summary>
public sealed class PackagingBoundaryTests
{
    /// <summary>
    /// No server-side project may reference CupriFace.
    ///
    /// <para>CupriFace is this project's preferred renderer, not a requirement of the platform, so a node that serves
    /// its site over Tor or a Cloudflare tunnel — or serves no browser client at all — must not carry a UI runtime.
    /// The boundary is stronger than "the client package may": the renderer is compiled <i>into</i> the embedded wasm
    /// bundle, so even <c>CupriNet.Nodestar.Client.CupriFace</c> restores nothing.</para>
    /// </summary>
    [Theory]
    [InlineData("src")]
    [InlineData("node")]
    public void No_server_side_project_references_CupriFace(string area)
    {
        var offenders = ProjectsIn(area)
            .Where(p => ReferencesPackage(p, "CupriFace"))
            .Select(p => Path.GetFileName(p))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{area}/ must stay free of CupriFace, but these reference it: {string.Join(", ", offenders)}. " +
            "The renderer belongs in the browser bundle (clients/web), which is embedded rather than restored.");
    }

    /// <summary>
    /// The transport must not serve a client.
    ///
    /// <para>Accepting browser DataChannels and choosing what runs in the browser are separate concerns. If
    /// <c>UseWebRtc()</c> ever sets <c>ClientAssets</c> again, wanting Mode 1 silently means taking this project's
    /// renderer — which is exactly the coupling the packaging split removed.</para>
    /// </summary>
    [Fact]
    public void UseWebRtc_does_not_choose_a_client()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "CupriNet.Nodestar.WebRtc", "NodestarWebRtcExtensions.cs"));

        // Comments may discuss it; an assignment would mean the transport had taken the decision back.
        var assigns = Regex.IsMatch(source, @"^\s*builder\.(ClientAssets\s*=|ServeClient\()", RegexOptions.Multiline);

        Assert.False(assigns,
            "UseWebRtc() must not supply a client — ServeCupriFaceClient() or ServeClient(...) is the caller's choice.");
    }

    /// <summary>Only the wasm projects are expected to reference CupriFace — a check that the boundary is real, not vacuous.</summary>
    [Fact]
    public void The_browser_client_does_reference_CupriFace()
    {
        // Without this, the tests above would pass just as happily if CupriFace had been removed from the repo
        // entirely, and would be guarding nothing.
        var client = Path.Combine(RepoRoot, "clients", "web", "CupriNet.Nodestar.Client.csproj");
        Assert.True(File.Exists(client), $"expected the browser client at {client}");
        Assert.True(ReferencesPackage(client, "CupriFace"),
            "the browser client is where the renderer lives; if it no longer references CupriFace these boundary "
            + "tests are asserting nothing.");
    }

    private static IEnumerable<string> ProjectsIn(string area)
    {
        var root = Path.Combine(RepoRoot, area);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            : [];
    }

    /// <summary>Matches a real <c>PackageReference</c>, not a mention in a comment — the file is full of those.</summary>
    private static bool ReferencesPackage(string projectPath, string package) =>
        Regex.IsMatch(File.ReadAllText(projectPath),
            $"""<PackageReference\s+Include\s*=\s*"{Regex.Escape(package)}"\s""");

    /// <summary>Walks up to the directory holding the solution, so the tests do not depend on the output path shape.</summary>
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nodestar.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }
}
