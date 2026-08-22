namespace CupriNet.Nodestar;

/// <summary>
/// Configuration for the CupriNode a Nodestar hosts. Bound from <c>appsettings.json</c> (the "Nodestar" section),
/// <c>CUPRINET_NODESTAR_*</c> environment variables, and the command line — in that order of increasing precedence.
///
/// <para>Defaults are <b>Lodestar-grade</b>: a Nodestar keeps every duty a Lodestar has (overlay keep-alive, a browser
/// entry point, warm start, Ferryman) and adds L2 hosting on top. So WebRTC and gossip are on unless you turn them
/// off, rather than off unless you turn them on.</para>
/// </summary>
public sealed class NodestarOptions
{
    /// <summary>The <c>appsettings.json</c> section these bind from.</summary>
    public const string SectionName = "Nodestar";

    /// <summary>The network (Concordium) this node joins. Nodes only ever see peers on the same one.</summary>
    public string Concordium { get; set; } = "cuprinet";

    /// <summary>A self-asserted display name, carried unverified in the link. Peers trust it only via the fingerprint.</summary>
    public string? Moniker { get; set; }

    /// <summary>Where identity, the Signet, and the known-peer cache are persisted. Must survive restarts, or the
    /// site's <c>cupri1…</c> address changes on every deploy.</summary>
    public string DataDirectory { get; set; } = "data";

    // ---- Overlay reachability -------------------------------------------------------------------------------

    /// <summary>The address the overlay listener binds. <c>0.0.0.0</c> serves every interface.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>The TCP port the overlay listens on.</summary>
    public int ListenPort { get; set; } = 47654;

    /// <summary>A public hostname or IP to advertise, when it differs from what the socket sees (NAT, container).</summary>
    public string? PublicHost { get; set; }

    /// <summary>Extra <c>host:port</c> beacons to advertise in the link.</summary>
    public IList<string> AdvertisedAddresses { get; } = [];

    // ---- Browser on-ramp ------------------------------------------------------------------------------------

    /// <summary>
    /// Accept browser WebRTC DataChannels (Mode 1). On by default — a Nodestar exists to be visited from a browser.
    /// Ignored in onion-only mode, because WebRTC is a clearnet UDP transport and using it there would both leak the
    /// node's clearnet IP and bypass the visitor's own Tor path.
    /// </summary>
    public bool EnableWebRtc { get; set; } = true;

    /// <summary>The UDP port for the WebRTC endpoint. Defaults to <see cref="ListenPort"/> when unset.</summary>
    public int? WebRtcPort { get; set; }

    // ---- Tor ------------------------------------------------------------------------------------------------

    /// <summary>Run an onion service alongside clearnet (dual-stack).</summary>
    public bool EnableTor { get; set; }

    /// <summary>Onion only: no clearnet beacons, no WebRTC. Mode 2 (server-side rendering) is the delivery path.</summary>
    public bool TorOnly { get; set; }

    // ---- Clearnet web front ---------------------------------------------------------------------------------

    /// <summary>The HTTP port the clearnet front listens on — the intonation page and the served client.</summary>
    public int WebPort { get; set; } = 8080;

    /// <summary>Serve the clearnet web front at all. Off makes this a headless Shrine host with no HTTP surface.</summary>
    public bool EnableWebFront { get; set; } = true;

    /// <summary>
    /// A second HTTP port for an onion service to forward to. Requests arriving here are served an <b>onion-only</b>
    /// link, so a visitor who came through Tor is never handed this node's clearnet beacons. Unset means no Tor face.
    /// </summary>
    public int? TorFacePort { get; set; }

    /// <summary>How long a minted link stays valid.</summary>
    public int LinkLifetimeMinutes { get; set; } = 60;

    /// <summary>How often the served link is re-minted. Shorter means fresher reachability, more minting.</summary>
    public int LinkRefreshSeconds { get; set; } = 120;

    // ---- Overlay behaviour ----------------------------------------------------------------------------------

    /// <summary>Seconds between overlay gossip rounds.</summary>
    public int GossipIntervalSeconds { get; set; } = 60;

    /// <summary>How many peers each gossip round reaches.</summary>
    public int GossipFanout { get; set; } = 4;

    /// <summary>Broker hole punches for NAT'd peers (consent-gated at the peer end).</summary>
    public bool EnableFerryman { get; set; } = true;

    /// <summary>Discover peers on the local network.</summary>
    public bool EnableLanDiscovery { get; set; } = true;

    /// <summary>Ask the router for a port mapping (UPnP/NAT-PMP).</summary>
    public bool EnablePortMapping { get; set; } = true;

    /// <summary>Links used to find the network on a cold start.</summary>
    public IList<string> SeedLinks { get; } = [];

    // ---- The Shrine -----------------------------------------------------------------------------------------

    /// <summary>
    /// Stamp this site's Signet into every Intonation the node mints, so "here is my link" also says "here is my
    /// site". <b>Off by default</b>: it ties the site to this node's overlay Sigil, which is right for a public or
    /// branded host and wrong for anonymous hosting.
    /// </summary>
    public bool AdvertiseSiteInLink { get; set; }

    /// <summary>The Signet name in the secret store. Changing it changes the site's <c>cupri1…</c> address.</summary>
    public string SiteName { get; set; } = "default";
}
