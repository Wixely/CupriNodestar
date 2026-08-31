using CupriNet.Core;
using CupriNet.Abstractions;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// What a node tells visitors to reach it at.
///
/// <para><b>Why this is asserted on the link rather than on the options.</b> A link carries the WebRTC credentials
/// but no address of its own: the browser client takes the FIRST non-onion beacon out of the link and dials that.
/// With none, <c>BrowserDataChannel</c> throws "This node's link carries no clearnet beacon to dial" — there is no
/// fallback to the page's own origin. So a beacon is not metadata about the node, it is literally the dial target,
/// and the only honest test is to decode a real link and read what a client would find there.</para>
///
/// <para>Both options existed before this and were wired to nothing, so an operator's configuration was inert while
/// looking correct. What the node advertised instead was whatever its interfaces reported — inside a container, the
/// bridge address, which no visitor can reach.</para>
/// </summary>
public class AdvertisedAddressTests : IAsyncLifetime
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "nodestar-beacons-" + Guid.NewGuid().ToString("N")[..12]);

    private readonly List<NodestarApplication> _running = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _running) await app.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
    }

    /// <summary>A node configured as the caller asks, started, and asked for its own link.</summary>
    private async Task<Intonation> LinkAsync(Action<NodestarOptions> configure)
    {
        var builder = NodestarApplication.CreateBuilder([]);
        builder.Node.DataDirectory = Path.Combine(_dataDirectory, Guid.NewGuid().ToString("N")[..8]);
        builder.Node.ListenAddress = "127.0.0.1";
        builder.Node.ListenPort = 0;
        builder.Node.EnableWebRtc = false;
        builder.Node.EnableWebFront = false;
        builder.Node.EnableLanDiscovery = false;
        builder.Node.EnablePortMapping = false;
        builder.Node.EnableFerryman = false;
        builder.Node.AdvertiseSiteInLink = true;

        // Off unless a test says otherwise. What the interfaces report differs between a developer's machine, CI and
        // a container, so leaving it on would make every assertion below depend on where it ran.
        builder.Node.AdvertiseLocalAddresses = false;
        configure(builder.Node);

        builder.Site.Serve(_ => CupriNet.Rites.OracleResponse.Text("hi", "text/plain"));

        var app = builder.Build();
        _running.Add(app);
        await app.StartAsync();

        return app.Node.Intone(TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, []);
    }

    /// <summary>
    /// The client's own rule, copied deliberately rather than referenced: the first beacon that is not an onion.
    /// If this and <c>BrowserDataChannel.FirstClearnetHost</c> ever disagree, these tests are asserting something no
    /// visitor experiences.
    /// </summary>
    private static Beacon? WhatAClientWouldDial(Intonation intonation)
    {
        foreach (var beacon in intonation.Beacons)
            if (beacon.Kind != EndpointKind.Onion && !string.IsNullOrWhiteSpace(beacon.Host))
                return beacon;

        return null;
    }

    [Fact]
    public async Task A_configured_host_is_what_a_client_would_dial()
    {
        var link = await LinkAsync(o =>
        {
            o.PublicHost = "203.0.113.7";
            o.PublicPort = 51820;
        });

        var dialled = WhatAClientWouldDial(link);

        Assert.NotNull(dialled);
        Assert.Equal("203.0.113.7", dialled!.Host);
        Assert.Equal(51820, dialled.Port);
    }

    /// <summary>
    /// The regression this exists to prevent: an operator's address must not be able to go missing.
    ///
    /// <para>Asserted as an absence because the alternative is not assertable. What an unconfigured node advertises
    /// is whatever its interfaces report — a container bridge in Docker, a LAN address on a workstation, loopback
    /// here — so the only stable fact is that without configuration nothing DECLARES the node's address, and every
    /// visitor is at the mercy of whatever it happened to find.</para>
    /// </summary>
    [Fact]
    public async Task Without_configuration_nothing_declares_the_nodes_address()
    {
        var link = await LinkAsync(_ => { });

        Assert.DoesNotContain(link.Beacons, b => b.Kind == EndpointKind.Manual);
    }

    /// <summary>
    /// With nothing declared, the node is handed <b>null</b> rather than an empty list — and the difference is the
    /// whole of a regression that reached CI.
    ///
    /// <para>An empty list reads as "advertise nothing" and suppresses the node's own address discovery; null means
    /// "no opinion" and leaves it alone. The first version returned the empty list, and on a CI machine — which has
    /// a routable interface, unlike this one — the node stopped advertising the address it had always found, and
    /// all five browser-gate tests failed with "this node's link carries no clearnet beacon to dial".</para>
    ///
    /// <para><b>This asserts the shape of the answer on purpose.</b> Every other test here reads a real link, which
    /// is the honest way to check what a visitor gets — but it is exactly what could not catch this: on a machine
    /// that discovers no address, suppressed discovery and absent discovery produce identical links. The only thing
    /// that distinguishes them is what gets handed to the node, so that is what is asserted.</para>
    /// </summary>
    [Fact]
    public void Declaring_nothing_leaves_the_nodes_own_discovery_alone()
    {
        var options = new NodestarOptions();

        Assert.Null(NodestarApplication.DeclaredBeacons(options));
    }

    /// <summary>And declaring something still reaches the node, so the null above is not simply "never sets it".</summary>
    [Fact]
    public void Declaring_a_host_hands_the_node_exactly_one_beacon()
    {
        var options = new NodestarOptions { PublicHost = "203.0.113.7", PublicPort = 47654 };

        var declared = NodestarApplication.DeclaredBeacons(options);

        Assert.NotNull(declared);
        var only = Assert.Single(declared!);
        Assert.Equal("203.0.113.7", only.Host);
        Assert.Equal(47654, only.Port);
    }

    /// <summary>
    /// <c>Manual</c> rather than <c>Host</c>, because the kind records where an address came from. One an operator
    /// typed outranks one an interface guessed, and a peer that cannot tell them apart cannot make that judgement.
    /// </summary>
    [Fact]
    public async Task A_configured_address_is_marked_as_declared_rather_than_observed()
    {
        var link = await LinkAsync(o => o.PublicHost = "node.example");

        Assert.Equal(EndpointKind.Manual, WhatAClientWouldDial(link)!.Kind);
    }

    /// <summary>The port defaults to the overlay port, so naming only a host is a complete answer.</summary>
    [Fact]
    public async Task The_advertised_port_defaults_to_the_listen_port()
    {
        var link = await LinkAsync(o =>
        {
            o.ListenPort = 47999;
            o.PublicHost = "node.example";
        });

        Assert.Equal(47999, WhatAClientWouldDial(link)!.Port);
    }

    /// <summary>
    /// Order is meaning, not presentation: the client dials the first beacon and never reaches the second. A node
    /// with a public address and a backup route must offer the public one first, or the backup is what gets dialled.
    /// </summary>
    [Fact]
    public async Task Extra_addresses_are_advertised_after_the_public_host()
    {
        var link = await LinkAsync(o =>
        {
            o.PublicHost = "203.0.113.7";
            o.PublicPort = 47654;
            o.AdvertisedAddresses.Add("198.51.100.9:47654");
        });

        var hosts = link.Beacons.Where(b => b.Kind == EndpointKind.Manual).Select(b => b.Host).ToList();

        Assert.Equal(["203.0.113.7", "198.51.100.9"], hosts);
    }

    /// <summary>
    /// IPv6 is bracketed, and survives to the link.
    ///
    /// <para>The address here is a real routable one on purpose. CupriNet drops reserved IPv6 — the documentation
    /// range <c>2001:db8::/32</c> among it — before a beacon reaches the link, so a test written with a documentation
    /// address would assert that IPv6 does not work and be believed.</para>
    /// </summary>
    [Fact]
    public async Task An_ipv6_address_survives_to_the_link()
    {
        var link = await LinkAsync(o => o.AdvertisedAddresses.Add("[2606:4700:4700::1111]:47654"));

        var dialled = WhatAClientWouldDial(link);

        Assert.Equal("2606:4700:4700::1111", dialled!.Host);
        Assert.Equal(47654, dialled.Port);
    }

    /// <summary>
    /// A node that declares no address still finds one for itself, and a client still has something to dial.
    ///
    /// <para>This is the regression test for the CI break, expressed as an outcome rather than a shape. An earlier
    /// version handed the node an empty beacon list, which suppressed its own discovery; the assertion here is that
    /// something is dialable without any configuration at all, which is precisely what stopped being true.</para>
    ///
    /// <para>It asserts the beacon is <c>Host</c> — discovered — rather than a specific address, because what the
    /// interfaces report differs between this machine, CI and a container. That it is discovered rather than
    /// declared is the part that must hold everywhere.</para>
    /// </summary>
    [Fact]
    public async Task A_node_that_declares_nothing_still_advertises_what_it_found()
    {
        var link = await LinkAsync(o => o.EnableWebRtc = true);

        var dialled = WhatAClientWouldDial(link);

        Assert.NotNull(dialled);
        Assert.Equal(EndpointKind.Host, dialled!.Kind);
    }

    /// <summary>
    /// A malformed address stops the node instead of being skipped.
    ///
    /// <para>Skipping it would produce a node that looks configured and cannot be reached, with nothing said — the
    /// same class of failure as the options that were never wired at all.</para>
    /// </summary>
    [Theory]
    [InlineData("no-port-here")]
    [InlineData("host:not-a-number")]
    [InlineData(":47654")]
    [InlineData("host:70000")]
    public async Task A_malformed_advertised_address_refuses_to_start(string entry)
    {
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => LinkAsync(o => o.AdvertisedAddresses.Add(entry)));

        Assert.Contains(entry, failure.Message);
    }
}
