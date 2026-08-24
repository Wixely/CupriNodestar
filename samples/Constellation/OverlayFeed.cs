using System.Text;
using System.Text.Json.Nodes;
using CupriNet.Abstractions;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;

namespace Constellation;

/// <summary>
/// The live feed: this node's own view of the overlay, published over the Auspice rite.
///
/// <para>It is real data, which is the point: start a second <b>node</b> and it appears here within a poll. It is
/// also the reason this file is mostly about <b>what not to publish</b>.</para>
///
/// <para><b>Peers are nodes, not viewers.</b> This projects the L1 overlay map, so an entry means another CupriNode:
/// a durable identity, an anchored overlay presence, a signed <c>PeerRecord</c>. A browser visitor is none of those
/// — a Pilgrim mints a throwaway identity per visit and <i>skips the overlay join entirely</i>, so it has no record
/// to project and will never show up here no matter how many tabs are open. Verified: with a browser client fully
/// connected and streaming this very feed, the count read <c>0 of 0</c> until a second node started.</para>
/// </summary>
/// <remarks>
/// The node is resolved lazily. A feed has to be registered before the application starts, but the node it reports
/// on does not exist until after — and since the delegate only runs once a visitor attends, by then it does.
/// </remarks>
internal sealed class OverlayFeed(
    Func<CupriNode> node, string network, Func<string?> siteAddress, NodeTelemetry telemetry)
{
    /// <summary>
    /// How many peers a message carries. An Auspice message is capped at 192 KiB and a Constellation holds up to
    /// 2000 records, so an uncapped projection would eventually exceed the ceiling and fail at the rite. The cap is
    /// <b>not silent</b>: the payload always reports the true total beside the number shown.
    /// </summary>
    private const int MaxPeersPublished = 64;

    /// <summary>
    /// One second, and it now ticks on every one of them.
    ///
    /// <para>This used to poll every two seconds and <b>suppress the send unless the peer set had changed</b>, which
    /// was right when the payload was only the Constellation: an idle node stayed quiet. It is wrong now. The payload
    /// carries live telemetry, so it differs on essentially every tick and the comparison would never once suppress
    /// anything — it would just be dead code claiming a property the feed no longer has.</para>
    ///
    /// <para>The cost is honest and worth naming: this node now emits about one message per second per viewer
    /// whether or not anything interesting happened, where before an idle node emitted nothing at all. That is a
    /// deliberate trade for a demo whose entire job is to show a live stream — a real deployment publishing rarely-
    /// changing data should keep the old suppression.</para>
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task EmanateAsync(IAuspicePublisher publisher, CancellationToken cancellationToken)
    {
        // Counted for the duration of this session's emanation, which is what makes "viewers" real: the rite starts
        // one of these per attending browser, so the count is the number of people actually watching right now.
        using var viewer = telemetry.EnterViewer();

        // Snapshot first: a viewer attending a feed already in progress has no state, so the opening message is what
        // stops the view being connected-but-empty.
        await PublishAsync(publisher, snapshot: true, cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            await PublishAsync(publisher, snapshot: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(IAuspicePublisher publisher, bool snapshot, CancellationToken cancellationToken)
    {
        var message = Encode(Project());

        // Counted before the send, so the figure includes the message reporting it. The alternative always reads one
        // behind and invites exactly the "why is this off by one" question a demo does not need.
        telemetry.CountPush(message.Length);

        if (snapshot) await publisher.SnapshotAsync(message, cancellationToken).ConfigureAwait(false);
        else await publisher.UpdateAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serves the same projection to the Mode-2 gateway, so an HTTP visitor sees the snapshot too.</summary>
    public byte[] SnapshotBytes() => Encode(Project());

    private static byte[] Encode(JsonNode payload) => Encoding.UTF8.GetBytes(payload.ToJsonString());

    /// <summary>
    /// Projects the Constellation into something safe to hand an anonymous visitor.
    ///
    /// <para><b>This is an allow-list, deliberately.</b> The obvious implementation — serialise
    /// <c>ConstellationEntry</c> and strip the private fields — is a deny-list, and a deny-list silently starts
    /// leaking the day someone adds a field upstream. Naming each published field means a new one has to be
    /// deliberately added here before it can escape.</para>
    ///
    /// <para><b>What must never be published, and why.</b> <c>Bucket</c>, <c>Standing</c> and <c>Taint</c> are this
    /// node's <i>private judgements</i> about a peer, not facts about it — publishing them would tell a Sybil
    /// operator whether its identities had been noticed or quarantined. <c>Source</c> (how we learned of a peer)
    /// exposes our own discovery topology, and <c>Slash24</c> is derived from a peer's address and buckets them by
    /// subnet. All five sit on the same object as the signed, redistributable <c>Record</c>, which is exactly what
    /// makes serialising the entry wholesale so easy to get wrong.</para>
    ///
    /// <para>The rule of thumb: <i>if the control plane wouldn't serve it to a peer, don't push it to an anonymous
    /// visitor.</i></para>
    /// </summary>
    /// <remarks>
    /// The telemetry is merged in here rather than inside the static projection below, and deliberately: that method
    /// is the redaction boundary and the thing the sample's tests pin, so it stays a pure function of the
    /// Constellation. Process counters are not peer data and have no business inside it.
    /// </remarks>
    private JsonNode Project()
    {
        var current = node();
        var payload = Project(current.Constellation, current.Identity.Sigil, network, siteAddress()).AsObject();

        foreach (var section in telemetry.Sample())
            payload[section.Key] = section.Value?.DeepClone();

        return payload;
    }

    /// <inheritdoc cref="Project()"/>
    /// <remarks>
    /// Pure, and separated from the node on purpose: the redaction boundary is the part of this sample most worth
    /// testing, and a test should not have to stand up an overlay node to check what a payload contains.
    /// </remarks>
    internal static JsonNode Project(
        CupriNet.Concordance.Constellation constellation, Sigil self, string network, string? site)
    {
        var entries = constellation.Entries;
        var peers = new JsonArray();

        foreach (var entry in entries.Take(MaxPeersPublished))
        {
            // Only entry.Record — the peer's OWN signed claim about itself, which exists to be redistributed and is
            // already sampled into every Intonation's Litany. Never the entry.
            var record = entry.Record;

            peers.Add(new JsonObject
            {
                // The fingerprint a user can compare. Public by construction: it is derived from a public key.
                ["sigil"] = Bech32.Fingerprint(record.Sigil),

                // Signed by its owner but NEVER verified by CupriNet — a display hint only, so the viewer marks it
                // as unverified rather than presenting it as a name.
                ["moniker"] = record.Moniker,

                ["capabilities"] = record.Capabilities.ToString(),

                // A COUNT, not the addresses. The signed record does carry endpoints and the control plane hands
                // them to peers, so publishing them would be within the rule — but a public page is a broader
                // audience than overlay gossip, and a sample is the code people copy. The count still shows
                // reachability changing, which is all the demo needs.
                ["endpoints"] = record.Endpoints.Count,

                // An observation of connectivity rather than a judgement about the peer, so it is publishable — and
                // it is what makes the view feel live.
                ["lastSeen"] = entry.LastSeen.ToString("O"),
            });
        }

        return new JsonObject
        {
            ["node"] = new JsonObject
            {
                ["network"] = network,
                ["site"] = site,
                ["self"] = Bech32.Fingerprint(self),
            },
            ["peers"] = peers,
            ["shown"] = peers.Count,
            ["total"] = entries.Count,      // the cap is visible, never silent
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
    }
}
