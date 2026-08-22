using System.Text.Json.Nodes;
using CupriNet.Abstractions;
using CupriNet.Concordance;
using CupriNet.Core;
using Xunit;
using ConstellationMap = CupriNet.Concordance.Constellation;

namespace Constellation.Tests;

/// <summary>
/// Locks the redaction boundary the sample exists to teach.
///
/// <para><c>ConstellationEntry</c> carries a peer's signed, redistributable <c>Record</c> on the same object as this
/// node's <b>private judgements</b> about that peer. Serialising the entry leaks them by accident, and the leak is
/// silent — the payload still looks reasonable. So these tests set those judgements to values that could not appear
/// by chance and assert the published payload never contains them.</para>
/// </summary>
public sealed class OverlayRedactionTests
{
    // Distinctive on purpose: if any of these turn up in the payload it is because the projection emitted them, not
    // because a timestamp or a count happened to collide.
    private const int TaintMarker = 4242;
    private const int RewardMarker = 3737;
    private const string SourceMarker = "SOURCE-MARKER-DO-NOT-PUBLISH";

    [Fact]
    public void The_payload_never_carries_this_nodes_private_judgements_about_a_peer()
    {
        var payload = ProjectOnePeerWithJudgements().ToJsonString();

        // Bucket / Standing / Taint — judgements, not facts. Publishing them tells a Sybil operator whether its
        // identities have been noticed or quarantined.
        Assert.DoesNotContain(TaintMarker.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(RewardMarker.ToString(), payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Excommunicate", payload, StringComparison.Ordinal);

        // Source — how we came to know this peer, i.e. our own discovery topology.
        Assert.DoesNotContain(SourceMarker, payload, StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_never_carries_a_peers_address_or_its_subnet()
    {
        var payload = ProjectOnePeerWithJudgements().ToJsonString();

        // The endpoint host, and the /24 the Constellation derives from it for diversity accounting.
        Assert.DoesNotContain("203.0.113", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("slash", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_payload_never_carries_raw_key_material_or_signatures()
    {
        var payload = ProjectOnePeerWithJudgements().ToJsonString();

        Assert.DoesNotContain("sealPublicKey", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signature", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void It_still_publishes_what_the_view_actually_needs()
    {
        // The counterpart to the assertions above: redaction that removed everything would pass them and be useless.
        var payload = ProjectOnePeerWithJudgements();
        var peer = payload["peers"]!.AsArray()[0]!;

        Assert.StartsWith("cupri1", peer["sigil"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("a-peer", peer["moniker"]!.GetValue<string>());
        Assert.Equal(1, peer["endpoints"]!.GetValue<int>());     // a count, never the address
        Assert.NotNull(peer["lastSeen"]);
    }

    [Fact]
    public void The_peer_cap_is_reported_rather_than_applied_silently()
    {
        // A truncated list that claims to be complete reads as "this node knows 64 peers" when it may know 2000.
        var map = new ConstellationMap();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 80; i++)
            map.Admit(Record(i, endpointHost: $"198.51.{i}.10"), PeerBucket.Wayfarers, now);

        var payload = OverlayFeed.Project(map, SelfSigil(), "net", site: null);

        var shown = payload["shown"]!.GetValue<int>();
        var total = payload["total"]!.GetValue<int>();
        Assert.True(shown < total, "the cap should have bitten for this many peers");
        Assert.Equal(shown, payload["peers"]!.AsArray().Count);
    }

    private static JsonNode ProjectOnePeerWithJudgements()
    {
        var map = new ConstellationMap();
        var record = Record(0, endpointHost: "203.0.113.7", moniker: "a-peer");

        map.Admit(record, PeerBucket.Wayfarers, DateTimeOffset.UtcNow, SourceMarker);

        // Drive the private judgements to the marker values, and move the peer to the bucket that would be most
        // damaging to reveal — a node that knows it has been quarantined is a node that knows to rotate identity.
        map.Taint(record.Sigil, TaintMarker);
        map.Reward(record.Sigil, RewardMarker);
        map.Promote(record.Sigil, PeerBucket.Excommunicate);

        return OverlayFeed.Project(map, SelfSigil(), "test-net", site: "cupri1site");
    }

    private static Sigil SelfSigil() => Sigil.FromSealPublicKey(Key(0xAA));

    private static PeerRecord Record(int index, string endpointHost, string? moniker = null) => new()
    {
        SealPublicKey = Key((byte)index),
        Endpoints = [new Beacon(EndpointKind.Host, endpointHost, 47654)],
        SequenceNumber = 1,
        IssuedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Capabilities = PeerCapabilities.Relay,
        Moniker = moniker,
        // Admit does not verify signatures, so a placeholder is enough to build a record the Constellation accepts.
        Signature = new byte[64],
    };

    private static byte[] Key(byte seed)
    {
        var key = new byte[32];
        Array.Fill(key, seed);
        return key;
    }
}
