namespace CupriNet.Nodestar;

/// <summary>
/// The Wards: the bounds and deadlines that stop one visitor taking a node off the air.
///
/// <para><b>What a Ward is.</b> CupriNet's own term for a limit placed where the damage would land rather than
/// upstream on trust — a decode-side ceiling, a per-source cap, an idle deadline. They exist because an anonymous
/// visitor is allowed to arrive and is not required to behave: the Shrine's review found that one address could
/// complete every available handshake, then go quiet, and hold every visit slot for as long as it liked. The cost
/// to the attacker was that many Toll solves; the cost to the site was total unavailability, with no detection and
/// no recovery.</para>
///
/// <para><b>Why a Nodestar has to expose them.</b> CupriNet's defaults are chosen for a general node, and the right
/// number depends on what a deployment is actually for. A node behind a corporate NAT sees every visitor arrive
/// from one address, so the per-address cap that protects a public site is the thing that breaks an internal one.
/// A site serving long-lived feeds wants a longer idle deadline than one serving pages. Before this, none of these
/// were reachable from a Nodestar at all — not from configuration, not from code, because the options object is
/// built internally.</para>
///
/// <para><b>Every setting here is nullable, and null means "whatever CupriNet chose".</b> That is deliberate and
/// it is the whole design. Copying the current defaults into this class would freeze them: a CupriNet release that
/// raised a limit, or lowered one because it turned out to be exploitable, would be silently overridden by our
/// stale copy — and a security default that quietly does not apply is worse than not having the setting. So an
/// unset Ward is not written at all, and the defaults documented below are stated as OBSERVATIONS of CupriNet
/// 0.6.2 rather than as this class's own values.</para>
///
/// <para>Bound from <c>Nodestar:Wards:*</c> in <c>appsettings.json</c>, from
/// <c>CUPRINET_NODESTAR_WARDS_*</c> environment variables, and from the command line. Timeouts take the usual
/// <c>hh:mm:ss</c> form — <c>00:05:00</c> is five minutes.</para>
/// </summary>
public sealed class NodestarWards
{
    // ---- The site: what an anonymous visitor can take ------------------------------------------------------
    //
    // These are the ones that matter most for a Nodestar, because a Pilgrim is anonymous by design. There is no
    // Sigil to key a budget on — only the address it arrived from — so these are weaker against someone
    // distributed and are what stops a single host taking the site off the air.

    /// <summary>
    /// How many visits the site will hold open at once, across everyone. <c>256</c> on CupriNet 0.6.2.
    ///
    /// <para>Taken before the accept, so a flood queues in the kernel's backlog rather than stalling the listen
    /// loop. Note it is a VISIT slot rather than a handshake slot: a visitor holds a page fetch, a live feed and a
    /// raw session for as long as it likes, so this bounds visitors present rather than arrivals per second.</para>
    /// </summary>
    public int? MaxConcurrentPilgrimages { get; set; }

    /// <summary>
    /// How many concurrent visits one source address may hold. <c>8</c> on CupriNet 0.6.2.
    ///
    /// <para>Checked before the handshake, so a refused caller pays for its own refusal.</para>
    ///
    /// <para><b>Raise this for a node whose visitors share an address</b> — behind a corporate NAT, a CGNAT, a
    /// reverse proxy that does not preserve the client address, or a browser gateway. Every visitor then looks
    /// like one host, and the ninth concurrent one is turned away by a defence aimed at somebody else.</para>
    /// </summary>
    public int? MaxPilgrimagesPerAddress { get; set; }

    /// <summary>
    /// How long a visit may go quiet before it is closed. <c>00:05:00</c> on CupriNet 0.6.2.
    ///
    /// <para><b>Traffic in EITHER direction keeps a visit alive</b>, which is what makes this safe for a live feed:
    /// a Pilgrim attends an Auspice, sends nothing further, and listens for as long as it likes. That is the rite
    /// working rather than an idle connection, and CupriNet has a test asserting a feed survives well past this
    /// deadline. So raising it is about visitors who are genuinely idle — a page left open in a tab — not about
    /// feeds.</para>
    /// </summary>
    public TimeSpan? PilgrimageIdleTimeout { get; set; }

    // ---- The overlay: what a peer can take -----------------------------------------------------------------
    //
    // The L1 side, which has defended against this shape for longer — it holds a global cap AND a per-peer budget
    // so one Sigil cannot monopolise the control budget. A Nodestar carries every Lodestar duty, so these apply
    // to it whether or not it is hosting a site.

    /// <summary>How many control connections the overlay will hold at once. <c>256</c> on CupriNet 0.6.2.</summary>
    public int? MaxConcurrentControlConnections { get; set; }

    /// <summary>
    /// How many of those one peer may hold. <c>8</c> on CupriNet 0.6.2. Keyed on the peer's Sigil rather than its
    /// address, so unlike the Pilgrimage cap it is not confounded by a shared NAT.
    /// </summary>
    public int? MaxControlConnectionsPerPeer { get; set; }

    /// <summary>How many handshakes may be in flight at once. <c>64</c> on CupriNet 0.6.2.</summary>
    public int? MaxConcurrentHandshakes { get; set; }

    /// <summary>How many control requests one peer may make per window. <c>120</c> on CupriNet 0.6.2.</summary>
    public int? MaxControlRequestsPerWindow { get; set; }

    /// <summary>
    /// How long that window is, in seconds. <c>10</c> on CupriNet 0.6.2.
    ///
    /// <para><b>It belongs with the count above and shipping one without the other was a mistake.</b> A rate limit
    /// is the pair: raising the count alone does not lengthen the window, it raises the RATE — so an operator who
    /// meant "allow a burst now and then" and set only the count has quietly doubled what a peer may sustain
    /// forever. Both, or neither.</para>
    /// </summary>
    public int? ControlWindowSeconds { get; set; }

    /// <summary>
    /// How many Ferryman reservations this node will hold for others. <c>1024</c> on CupriNet 0.6.2.
    ///
    /// <para>Relevant because a Nodestar is a Ferryman by default — see <see cref="NodestarOptions.EnableFerryman"/>
    /// — so it is spending memory on behalf of peers it is relaying for. Turning the Ferryman off entirely makes
    /// this moot.</para>
    /// </summary>
    public int? MaxFerrymanReservations { get; set; }

    // ---- Deadlines -----------------------------------------------------------------------------------------

    /// <summary>
    /// How long the pairing exchange may take before it is abandoned. <c>00:00:30</c> on CupriNet 0.6.2.
    ///
    /// <para>A deadline rather than a bound, and the two fail differently: too low turns a slow but legitimate
    /// peer into a failure, where too high lets a peer that never finishes hold a handshake slot.</para>
    /// </summary>
    public TimeSpan? ConsecrationTimeout { get; set; }

    /// <summary>
    /// How long a single connection candidate is given before the next is tried. <c>00:00:06</c> on CupriNet 0.6.2.
    ///
    /// <para>This is a dial-out setting rather than a defence: it bounds how long this node waits on one address
    /// out of the several a link may carry. Worth raising only for peers genuinely far away.</para>
    /// </summary>
    public TimeSpan? CandidateConnectTimeout { get; set; }

    /// <summary>
    /// Whether an arriving peer must solve a Toll before it is served. <c>true</c> on CupriNet 0.6.2.
    ///
    /// <para><b>Turning this off removes the cost of arriving</b>, which is what makes every cap above expensive to
    /// exhaust rather than free. It is exposed because a closed network of known peers may reasonably not want the
    /// latency, and left alone it stays on. This is the one setting here where the wrong value is a security
    /// decision rather than a tuning one.</para>
    /// </summary>
    public bool? EnableToll { get; set; }

    /// <summary>
    /// How hard the Toll this node MINTS is. <c>16</c> on CupriNet 0.6.2.
    ///
    /// <para>Exposed for the same reason <see cref="ControlWindowSeconds"/> is: <see cref="EnableToll"/> without
    /// this is half a control. Whether arriving costs anything and how much it costs are the same decision, and an
    /// operator able to answer only the first has not really been given it.</para>
    /// </summary>
    public int? TributeDifficulty { get; set; }

    /// <summary>
    /// How hard a Toll this node will ACCEPT. <c>16</c> on CupriNet 0.6.2.
    ///
    /// <para>The other half of the pair, and the two are not interchangeable: one is what this node asks of
    /// arrivals, the other what it insists on from theirs. <b>Raising this above what peers mint turns them
    /// away</b> — a fence around your own node rather than a defence of it — so move it knowing which side of the
    /// exchange you are changing.</para>
    /// </summary>
    public int? RequiredTributeDifficulty { get; set; }

    // The subnet fence is NOT here. It is a list of addresses rather than a bound or a deadline, so it sits with
    // the other address settings on NodestarOptions - see NodestarOptions.AllowedSubnets. Worth knowing about
    // from here, because someone reading this class is asking how to lock a node down and that is the other half
    // of the answer.

    // MaxPageantsAsMember is deliberately NOT exposed. It defaults to 0, so unlike everything above it does not
    // bound something already happening — setting it appears to enable a capability rather than to fence one, and
    // nothing here has established what it costs. A Ward this class cannot describe is not one it should offer.

    /// <summary>
    /// Whether the operator set anything at all, which is what decides whether the node says so at startup.
    /// </summary>
    internal bool AnySet =>
        MaxConcurrentPilgrimages is not null
        || MaxPilgrimagesPerAddress is not null
        || PilgrimageIdleTimeout is not null
        || MaxConcurrentControlConnections is not null
        || MaxControlConnectionsPerPeer is not null
        || MaxConcurrentHandshakes is not null
        || MaxControlRequestsPerWindow is not null
        || MaxFerrymanReservations is not null
        || ConsecrationTimeout is not null
        || CandidateConnectTimeout is not null
        || ControlWindowSeconds is not null
        || EnableToll is not null
        || TributeDifficulty is not null
        || RequiredTributeDifficulty is not null;
}
