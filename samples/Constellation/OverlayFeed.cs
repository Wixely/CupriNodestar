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
/// <para>It is real data, which is the point — open a second viewer and you watch yourself appear. It is also the
/// reason this file is mostly about <b>what not to publish</b>.</para>
/// </summary>
/// <remarks>
/// The node is resolved lazily. A feed has to be registered before the application starts, but the node it reports
/// on does not exist until after — and since the delegate only runs once a visitor attends, by then it does.
/// </remarks>
internal sealed class OverlayFeed(Func<CupriNode> node, string network, Func<string?> siteAddress)
{
    /// <summary>
    /// How many peers a message carries. An Auspice message is capped at 192 KiB and a Constellation holds up to
    /// 2000 records, so an uncapped projection would eventually exceed the ceiling and fail at the rite. The cap is
    /// <b>not silent</b>: the payload always reports the true total beside the number shown.
    /// </summary>
    private const int MaxPeersPublished = 64;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async Task EmanateAsync(IAuspicePublisher publisher, CancellationToken cancellationToken)
    {
        // Snapshot first: a viewer attending a feed already in progress has no state, so the opening message is what
        // stops the view being connected-but-empty.
        var previous = Project();
        await publisher.SnapshotAsync(Encode(previous), cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            // The Constellation exposes state, not change events, so this re-projects and compares. Sending only on
            // a real change keeps an idle node quiet — and a feed that ticks regardless of content is a feed whose
            // timing leaks nothing but also says nothing.
            var current = Project();
            if (current.ToJsonString() == previous.ToJsonString()) continue;

            await publisher.UpdateAsync(Encode(current), cancellationToken).ConfigureAwait(false);
            previous = current;
        }
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
    private JsonNode Project()
    {
        var current = node();
        return Project(current.Constellation, current.Identity.Sigil, network, siteAddress());
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
