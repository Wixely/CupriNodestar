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

    /// <summary>
    /// The host or IP this node tells visitors to reach it at, when it cannot work that out for itself.
    ///
    /// <para><b>This is the address a browser actually dials.</b> A link carries the WebRTC credentials but no
    /// address of its own — the client takes the first non-onion beacon in the link and dials that with the WebRTC
    /// port. So whatever ends up here is what a Mode 1 visitor connects to, and the same beacon is what another
    /// node dials over TCP.</para>
    ///
    /// <para>Leave it unset on a machine with a routable address of its own: the node discovers what it has, and
    /// port mapping or reflexive observation fill in the rest. Set it whenever the address the node can see is not
    /// the address a visitor must use — behind a container bridge, a NAT that does not speak UPnP, a cloud load
    /// balancer.</para>
    ///
    /// <para><b>A container advertises its own address, which is the wrong one.</b> One was observed advertising
    /// <c>127.0.0.1</c> as its only beacon, so every visitor faithfully dialled their own loopback; behind a bridge
    /// it is the bridge address, which is no better. The node cannot tell — only an operator knows which address
    /// their visitors can reach, which is what this setting is for.</para>
    ///
    /// <para>A node that discovers nothing usable advertises no clearnet beacon at all, and the browser client then
    /// fails with "This node's link carries no clearnet beacon to dial" rather than falling back to the origin the
    /// page was served from. That is the quieter failure, because it happens in the visitor's browser while the
    /// node logs a healthy start.</para>
    ///
    /// <para>It does <b>not</b> affect the HTTP gateway. That is served over whatever address the visitor already
    /// reached, so a node behind a tunnel needs nothing here.</para>
    /// </summary>
    public string? PublicHost { get; set; }

    /// <summary>
    /// The port to advertise alongside <see cref="PublicHost"/>. Defaults to <see cref="ListenPort"/>.
    ///
    /// <para>It must be the port a visitor can actually reach, which is not necessarily the one this node bound.
    /// A port-forward that remaps the number has to be declared here, because the browser dials the advertised
    /// number verbatim.</para>
    /// </summary>
    public int? PublicPort { get; set; }

    /// <summary>
    /// Whether to advertise the addresses this node found on its own interfaces. Default true.
    ///
    /// <para>Turn it off when those addresses are useless to a visitor and would only get in the way — a container's
    /// bridge address, or a LAN address on a node serving the internet. A client dials the FIRST non-onion beacon it
    /// finds, so an unhelpful one listed ahead of <see cref="PublicHost"/> is not merely noise: it is what gets
    /// dialled.</para>
    /// </summary>
    public bool AdvertiseLocalAddresses { get; set; } = true;

    /// <summary>
    /// Extra beacons to advertise, each <c>host:port</c> — a second route to this node, or an IPv6 address beside
    /// an IPv4 one. Listed after <see cref="PublicHost"/>, in the order given.
    ///
    /// <para>IPv6 goes in brackets: <c>[2001:db8::1]:47654</c>. A malformed entry stops the node at startup rather
    /// than being skipped, because an address silently dropped is one a visitor cannot reach and nobody is told
    /// about.</para>
    /// </summary>
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

    /// <summary>
    /// A TCP port on which this node serves Pilgrims directly, or null to serve none. <c>0</c> takes an
    /// OS-assigned port, readable afterwards from <see cref="NodestarApplication.ShrineEndPoint"/>.
    ///
    /// <para><b>This is not the overlay port, and it must not be.</b> A connection to <see cref="ListenPort"/> is
    /// answered by the NODE, presenting its own Sigil, because that port exists to pair overlay peers — so a visitor
    /// pinning the site's address fails there, and one pinning the node's Sigil succeeds into a session with no
    /// Shrine behind it. On this port every connection is a Pilgrimage and the Signet is presented unconditionally,
    /// which inverts that: the wrong key now fails at the handshake, where a mistake belongs.</para>
    ///
    /// <para>Off by default because opening a port is a deployment decision, and because the two paths that matter
    /// most need no port at all — a browser arrives over WebRTC, and the gateway never leaves the process. Set it
    /// for a desktop or service client over TCP, or for a test harness that would otherwise write its own accept
    /// loop.</para>
    /// </summary>
    public int? ShrinePort { get; set; }

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
    /// Serve the site over plain HTTP as well (Mode 2, the L2→HTTP gateway). This is what makes a Cloudflare tunnel
    /// or an onion work at all, since neither can carry inbound WebRTC UDP. It renders a point-in-time snapshot and
    /// cannot push updates — live data is a Mode-1 feature. Scoped to this node's <b>own</b> Shrine.
    /// </summary>
    public bool EnableGateway { get; set; } = true;

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
