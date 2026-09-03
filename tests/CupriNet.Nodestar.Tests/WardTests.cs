using CupriNet.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// The Wards: the bounds and deadlines that stop one visitor taking a node off the air.
///
/// <para><b>Asserted against a fresh <c>CupriNodeOptions</c> rather than against numbers written here.</b> That is
/// the point of the whole design and it is what these tests exist to hold. If this file said
/// <c>Assert.Equal(8, node.MaxPilgrimagesPerAddress)</c>, it would pass today, keep passing after CupriNet changed
/// that default, and quietly document a number nobody had chosen. Comparing against a default-constructed instance
/// asks the real question — "did we leave it alone?" — and keeps asking it across upgrades.</para>
///
/// <para>The failure this guards is specific and has happened here before, in another form: an empty beacon list
/// suppressed the node's own address discovery, because "nothing" and "nothing, deliberately" were sent as the
/// same value. A Ward defaulted in this repository would do the same to a security limit — override an upstream
/// fix with a stale copy, while looking entirely correct.</para>
/// </summary>
public class WardTests
{
    /// <summary>What CupriNet chose, for whichever CupriNet is actually referenced.</summary>
    private static CupriNodeOptions Untouched => new() { Concordium = "wards" };

    /// <summary>
    /// A node whose Wards are nothing like any default, standing in for a future CupriNet.
    ///
    /// <para><b>This exists because comparing against a fresh instance was not enough</b>, which a mutation run
    /// proved rather than anyone reasoning it out. Replacing the implementation with
    /// <c>MaxPilgrimagesPerAddress = wards.MaxPilgrimagesPerAddress ?? 8</c> — a default frozen into this
    /// repository, the exact mistake the design exists to prevent — left every test passing, because 8 is what
    /// CupriNet happens to choose today. The test would have started failing only once CupriNet moved that number,
    /// which is precisely the moment nobody is looking at it.</para>
    ///
    /// <para>Every value here is deliberately implausible, so a frozen default cannot coincide with one.</para>
    /// </summary>
    private static CupriNodeOptions NothingLikeTheDefaults => new()
    {
        Concordium = "wards",
        MaxConcurrentPilgrimages = 4242,
        MaxPilgrimagesPerAddress = 4243,
        PilgrimageIdleTimeout = TimeSpan.FromSeconds(4244),
        MaxConcurrentControlConnections = 4245,
        MaxControlConnectionsPerPeer = 4246,
        MaxConcurrentHandshakes = 4247,
        MaxControlRequestsPerWindow = 4248,
        MaxFerrymanReservations = 4249,
        ConsecrationTimeout = TimeSpan.FromSeconds(4250),
        CandidateConnectTimeout = TimeSpan.FromSeconds(4251),
        ControlWindowSeconds = 4252,
        TributeDifficulty = 4253,
        RequiredTributeDifficulty = 4254,
        EnableToll = false,
    };

    [Fact]
    public void Setting_nothing_leaves_every_ward_as_CupriNet_chose_it()
    {
        // Started from values no default could match, so "left alone" is distinguishable from "overwritten with
        // the number that happens to be the default today". See NothingLikeTheDefaults for why that distinction
        // is the whole of this.
        var start = NothingLikeTheDefaults;
        var applied = NodestarApplication.ApplyWards(new NodestarWards(), start);

        // Compared as a whole record rather than property by property: a Ward added upstream and forwarded here by
        // accident would slip past a list of named assertions, and this catches it without anyone remembering to
        // extend the list.
        Assert.Equal(start, applied);
    }

    [Fact]
    public void The_site_wards_reach_the_node()
    {
        var applied = NodestarApplication.ApplyWards(
            new NodestarWards
            {
                MaxConcurrentPilgrimages = 32,
                MaxPilgrimagesPerAddress = 64,
                PilgrimageIdleTimeout = TimeSpan.FromMinutes(30),
            },
            Untouched);

        Assert.Equal(32, applied.MaxConcurrentPilgrimages);
        Assert.Equal(64, applied.MaxPilgrimagesPerAddress);
        Assert.Equal(TimeSpan.FromMinutes(30), applied.PilgrimageIdleTimeout);
    }

    [Fact]
    public void The_overlay_wards_and_the_deadlines_reach_the_node()
    {
        var applied = NodestarApplication.ApplyWards(
            new NodestarWards
            {
                MaxConcurrentControlConnections = 512,
                MaxControlConnectionsPerPeer = 2,
                MaxConcurrentHandshakes = 16,
                MaxControlRequestsPerWindow = 240,
                ControlWindowSeconds = 20,
                MaxFerrymanReservations = 4,
                ConsecrationTimeout = TimeSpan.FromSeconds(45),
                CandidateConnectTimeout = TimeSpan.FromSeconds(12),
                EnableToll = false,
            },
            Untouched);

        Assert.Equal(512, applied.MaxConcurrentControlConnections);
        Assert.Equal(2, applied.MaxControlConnectionsPerPeer);
        Assert.Equal(16, applied.MaxConcurrentHandshakes);
        Assert.Equal(240, applied.MaxControlRequestsPerWindow);
        Assert.Equal(20, applied.ControlWindowSeconds);
        Assert.Equal(4, applied.MaxFerrymanReservations);
        Assert.Equal(TimeSpan.FromSeconds(45), applied.ConsecrationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), applied.CandidateConnectTimeout);
        Assert.False(applied.EnableToll);
    }

    /// <summary>
    /// The Toll's cost, both halves of it.
    ///
    /// <para>Asserted separately from the other numbers because they are the pair most easily confused: one is
    /// what this node asks of arrivals, the other what it insists on from theirs. A forwarding that crossed them
    /// would look entirely healthy and quietly turn away every peer whose Toll was minted at the old figure.</para>
    /// </summary>
    [Fact]
    public void Both_halves_of_the_Toll_reach_the_node()
    {
        var applied = NodestarApplication.ApplyWards(
            new NodestarWards { TributeDifficulty = 20, RequiredTributeDifficulty = 12 }, Untouched);

        Assert.Equal(20, applied.TributeDifficulty);
        Assert.Equal(12, applied.RequiredTributeDifficulty);
    }

    // ---- The subnet fence ----------------------------------------------------------------------------------
    //
    // Not a Ward - it is a list of addresses rather than a bound or a deadline - but the same kind of control and
    // the same kind of mistake, so it is guarded beside them.

    /// <summary>
    /// <b>An unconfigured fence must be null, not empty.</b>
    ///
    /// <para>An allow-list is "only these", so an EMPTY one reads as "allow nothing" — a node that talks to
    /// nobody, arrived at by an operator who configured no fence at all. This repository has already shipped that
    /// exact confusion once in the other direction: an empty beacon list went where null was meant and suppressed
    /// the node's own address discovery.</para>
    /// </summary>
    [Fact]
    public void An_unconfigured_fence_is_absent_rather_than_empty()
    {
        var options = new NodestarOptions();

        Assert.Null(NodestarApplication.Fence(options.AllowedSubnets));
        Assert.Null(NodestarApplication.Fence(options.DeniedSubnets));
    }

    [Fact]
    public void A_configured_fence_is_forwarded_as_given()
    {
        var options = new NodestarOptions();
        options.AllowedSubnets.Add("10.0.0.0/8");
        options.AllowedSubnets.Add("192.168.0.0/16");
        options.DeniedSubnets.Add("10.1.2.0/24");

        // Forwarded verbatim: CupriNet parses these, and a second parser here would only be a second place for a
        // CIDR to be rejected with a different message.
        Assert.Equal(["10.0.0.0/8", "192.168.0.0/16"], NodestarApplication.Fence(options.AllowedSubnets));
        Assert.Equal(["10.1.2.0/24"], NodestarApplication.Fence(options.DeniedSubnets));
    }

    /// <summary>
    /// The fence is a copy, so configuration edited after the node started cannot silently move it.
    /// </summary>
    [Fact]
    public void A_forwarded_fence_does_not_track_later_edits()
    {
        var options = new NodestarOptions();
        options.AllowedSubnets.Add("10.0.0.0/8");

        var fence = NodestarApplication.Fence(options.AllowedSubnets);
        options.AllowedSubnets.Add("0.0.0.0/0");

        Assert.Equal(["10.0.0.0/8"], fence);
    }

    [Fact]
    public void The_fence_binds_from_configuration()
    {
        var options = new NodestarOptions();

        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nodestar:AllowedSubnets:0"] = "10.0.0.0/8",
                ["Nodestar:AllowedSubnets:1"] = "172.16.0.0/12",
                ["Nodestar:DeniedSubnets:0"] = "10.9.9.0/24",
            })
            .Build()
            .GetSection(NodestarOptions.SectionName)
            .Bind(options);

        Assert.Equal(["10.0.0.0/8", "172.16.0.0/12"], options.AllowedSubnets);
        Assert.Equal(["10.9.9.0/24"], options.DeniedSubnets);
    }

    /// <summary>
    /// Setting one Ward must not disturb the others.
    ///
    /// <para>Worth its own test because of how this is implemented: each Ward is applied with a <c>with</c>
    /// expression that copies the whole record. A mistake there — reassigning from a stale copy, or building from
    /// a fresh instance instead of the one passed in — would reset everything except the Ward being written, and
    /// every other test here would still pass.</para>
    /// </summary>
    [Fact]
    public void Setting_one_ward_disturbs_no_other()
    {
        var expected = Untouched with { MaxPilgrimagesPerAddress = 99 };
        var applied = NodestarApplication.ApplyWards(
            new NodestarWards { MaxPilgrimagesPerAddress = 99 }, Untouched);

        Assert.Equal(expected, applied);
    }

    /// <summary>
    /// A Ward set to the same value CupriNet already had is still a value the operator chose.
    ///
    /// <para>It cannot be told apart from unset in the result, and it should not be — the node behaves identically.
    /// What must survive is the record of the choice, so the startup line reports it and an operator reading the
    /// log sees the number they pinned rather than wondering whether their configuration was read at all.</para>
    /// </summary>
    [Fact]
    public void A_ward_pinned_to_its_own_default_is_still_reported_as_set()
    {
        var start = Untouched;
        var wards = new NodestarWards { MaxPilgrimagesPerAddress = start.MaxPilgrimagesPerAddress };

        Assert.True(wards.AnySet);
        Assert.Equal(start, NodestarApplication.ApplyWards(wards, start));
    }

    [Fact]
    public void Nothing_set_is_nothing_to_report()
    {
        Assert.False(new NodestarWards().AnySet);
    }

    /// <summary>
    /// A Ward set the way an operator would actually set it: from configuration.
    ///
    /// <para><b>The forwarding tests above would all pass with the binding broken.</b> They construct
    /// <see cref="NodestarWards"/> in C#, which is not how anyone deploying this will reach it — a container gets
    /// an environment variable and a service gets an <c>appsettings.json</c> section. <see cref="NodestarOptions.Wards"/>
    /// is a get-only property, which binds by having its properties filled in rather than by being replaced, and
    /// that is exactly the shape that silently binds nothing if it is wrong.</para>
    ///
    /// <para>A <c>TimeSpan</c> is asserted alongside the integers because it is parsed rather than converted, so
    /// it is the one most likely to bind as a default while the rest work.</para>
    /// </summary>
    [Fact]
    public void Wards_bind_from_configuration()
    {
        var options = new NodestarOptions();

        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nodestar:Wards:MaxPilgrimagesPerAddress"] = "64",
                ["Nodestar:Wards:PilgrimageIdleTimeout"] = "00:30:00",
                ["Nodestar:Wards:EnableToll"] = "false",
            })
            .Build()
            .GetSection(NodestarOptions.SectionName)
            .Bind(options);

        Assert.Equal(64, options.Wards.MaxPilgrimagesPerAddress);
        Assert.Equal(TimeSpan.FromMinutes(30), options.Wards.PilgrimageIdleTimeout);
        Assert.False(options.Wards.EnableToll);

        // And one left out of the configuration stays unset, rather than binding to a zero.
        Assert.Null(options.Wards.MaxConcurrentPilgrimages);
    }

    /// <summary>
    /// The same, through the environment, because that is what a container has.
    ///
    /// <para>The prefix is stripped and the remainder is a configuration path, so the separator is what decides
    /// whether a nested section is reachable at all — a flat name would bind nothing and say nothing.</para>
    /// </summary>
    [Fact]
    public void Wards_bind_from_environment_variables()
    {
        var options = new NodestarOptions();

        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // What AddEnvironmentVariables(prefix: "CUPRINET_NODESTAR_") leaves behind for
                // CUPRINET_NODESTAR_Wards__MaxPilgrimagesPerAddress.
                ["Wards:MaxPilgrimagesPerAddress"] = "16",
                ["Wards:ConsecrationTimeout"] = "00:01:00",
            })
            .Build()
            .Bind(options);

        Assert.Equal(16, options.Wards.MaxPilgrimagesPerAddress);
        Assert.Equal(TimeSpan.FromMinutes(1), options.Wards.ConsecrationTimeout);
    }

    /// <summary>
    /// Every Ward this class offers must actually be forwarded.
    ///
    /// <para>The gap this closes is a property added to <see cref="NodestarWards"/> and forgotten in
    /// <c>ApplyWards</c> — configuration that binds, validates, appears in the startup line, and reaches nothing.
    /// That exact failure has happened in this repository before: <c>PublicHost</c> and <c>PublicPort</c> both
    /// existed and were wired to nothing, so an operator's setting was inert while looking correct.</para>
    ///
    /// <para>Done by reflection rather than by a list, because a list is the thing that gets forgotten.</para>
    /// </summary>
    [Fact]
    public void Every_ward_offered_is_a_ward_forwarded()
    {
        var wards = new NodestarWards();
        var properties = typeof(NodestarWards).GetProperties()
            .Where(p => p.CanWrite && Nullable.GetUnderlyingType(p.PropertyType) is not null)
            .ToList();

        Assert.NotEmpty(properties);

        var inert = new List<string>();

        foreach (var property in properties)
        {
            var underlying = Nullable.GetUnderlyingType(property.PropertyType)!;

            // A value deliberately unlike any plausible default, so "it was forwarded" cannot be confused with
            // "it happened to match".
            object value = underlying == typeof(TimeSpan) ? TimeSpan.FromSeconds(4242)
                         : underlying == typeof(bool) ? false
                         : 4242;

            var single = new NodestarWards();
            property.SetValue(single, value);

            var applied = NodestarApplication.ApplyWards(single, Untouched);
            var mirror = typeof(CupriNodeOptions).GetProperty(property.Name);

            if (mirror is null) { inert.Add($"{property.Name} (no such property on CupriNodeOptions)"); continue; }
            if (!Equals(mirror.GetValue(applied), value)) inert.Add(property.Name);

            // And it must count as set, or the startup line will not mention it.
            Assert.True(single.AnySet, $"{property.Name} is not counted by AnySet");
        }

        Assert.True(inert.Count == 0,
            "these Wards can be configured and reach nothing: " + string.Join(", ", inert));
    }
}
