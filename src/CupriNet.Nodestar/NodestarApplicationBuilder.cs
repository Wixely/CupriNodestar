using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CupriNet.Nodestar;

/// <summary>
/// Builds a Nodestar: a CupriNode, a clearnet HTTP front, and an L2 site, wired together.
/// <c>WebApplication.CreateBuilder</c>, but for a node that also hosts content on the overlay.
/// </summary>
/// <example>
/// <code>
/// var builder = NodestarApplication.CreateBuilder(args);
/// builder.Node.Concordium = "example.chat";
/// builder.Site.ServeStaticFiles("l2-wwwroot");
/// var app = builder.Build();
/// await app.RunAsync();
/// </code>
/// </example>
public sealed class NodestarApplicationBuilder
{
    internal NodestarApplicationBuilder(string[] args)
    {
        // Three sources, increasing precedence: the file an operator edits, the env a container sets, the flags a
        // developer types. The env prefix is stripped, so CUPRINET_NODESTAR_ListenPort lands at the configuration
        // root and binds alongside the appsettings section below.
        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CUPRINET_NODESTAR_")
            .AddCommandLine(args)
            .Build();

        Configuration.GetSection(NodestarOptions.SectionName).Bind(Node);
        Configuration.Bind(Node);
    }

    /// <summary>The bound configuration, for anything this builder does not surface directly.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>The node: which network, where it listens, whether it accepts browsers, whether it uses Tor.</summary>
    public NodestarOptions Node { get; } = new();

    /// <summary>What this Nodestar serves on L2 — the part you supply.</summary>
    public SiteBuilder Site { get; } = new();

    /// <summary>Logging for the host. Defaults to timestamped single-line console output, as a daemon wants.</summary>
    public ILoggerFactory LoggerFactory { get; set; } = Microsoft.Extensions.Logging.LoggerFactory.Create(b =>
        b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        }));

    /// <summary>Assembles the application. Nothing binds a port or touches the network until <c>RunAsync</c>.</summary>
    public NodestarApplication Build() => new(Node, Site, LoggerFactory);
}
