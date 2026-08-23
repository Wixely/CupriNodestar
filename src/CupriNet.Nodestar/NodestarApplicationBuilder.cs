using CupriNet.Hosting;
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

    /// <summary>
    /// Supplies the optional WebRTC transport (the browser on-ramp). The seam lives here rather than in the base
    /// package's own code so that <c>CupriNet.Nodestar</c> never references a WebRTC stack: <c>IWebRtcTransport</c>
    /// is a CupriNet.Hosting interface, and only <c>CupriNet.Nodestar.WebRtc</c> supplies an implementation.
    /// </summary>
    public NodestarApplicationBuilder ConfigureTransport(
        Func<NodestarOptions, Action<string>, IWebRtcTransport?> factory)
    {
        TransportFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>
    /// Serves a browser client of your choosing: a lookup from relative path to file. Null means no client is
    /// served, which is the right state for a gateway-only deployment.
    ///
    /// <para><b>The transport does not choose this.</b> Accepting browser DataChannels and deciding what runs in the
    /// browser are separate concerns, so <c>UseWebRtc</c> does not set it. Nodestar's reference client lives in
    /// <c>CupriNet.Nodestar.Client.CupriFace</c> and is opted into with <c>ServeCupriFaceClient()</c>; anything that
    /// speaks the CupriNet client protocol can take its place here.</para>
    /// </summary>
    public NodestarApplicationBuilder ServeClient(Func<string, ClientAsset?> assets)
    {
        ClientAssets = assets ?? throw new ArgumentNullException(nameof(assets));
        return this;
    }

    /// <summary>The client file lookup, if one was supplied.</summary>
    public Func<string, ClientAsset?>? ClientAssets { get; set; }

    internal Func<NodestarOptions, Action<string>, IWebRtcTransport?>? TransportFactory { get; private set; }

    /// <summary>Assembles the application. Nothing binds a port or touches the network until <c>RunAsync</c>.</summary>
    public NodestarApplication Build() => new(Node, Site, LoggerFactory, TransportFactory, ClientAssets);
}
