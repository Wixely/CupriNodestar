using CupriNet.Hosting;
using QRCoder;

namespace CupriNet.Nodestar;

/// <summary>An immutable snapshot of the node's current connection link and its rendered QR code.</summary>
public sealed record LinkSnapshot(string Link, string QrDataUri, DateTimeOffset GeneratedAt);

/// <summary>
/// Caches the node's minted connection link (and its QR) per transport class, re-minting only after a refresh
/// interval rather than on every request. A fresh mint rotates the nonce/timestamp and re-snapshots reachability, so
/// the served link stays current without a page view costing a mint.
///
/// <para>Each transport class is cached <b>separately</b> for a security reason, not a performance one: the clearnet
/// face and the Tor face must never be served the same link. See <see cref="Current"/>.</para>
/// </summary>
public sealed class NodestarLinkProvider(
    CupriNode node,
    TimeSpan lifetime,
    TimeSpan refreshInterval,
    Func<DateTimeOffset>? clock = null)
{
    private readonly CupriNode _node = node ?? throw new ArgumentNullException(nameof(node));
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();
    private readonly Dictionary<LinkTransports, LinkSnapshot> _cache = [];

    /// <summary>
    /// The cached link for a transport class, minted afresh only when that class's entry is missing or stale.
    ///
    /// <para><b>Why the class matters.</b> A visitor who reached this page over Tor must not be handed the node's
    /// clearnet beacons — that would expose the IP the onion exists to hide, and push them off their own Tor path. So
    /// the onion face asks for <see cref="LinkTransports.OnionOnly"/> and the clearnet face for
    /// <see cref="LinkTransports.ClearnetOnly"/>, and the two are never interchangeable.</para>
    /// </summary>
    public LinkSnapshot Current(LinkTransports transports = LinkTransports.All)
    {
        var now = _clock();
        lock (_gate)
        {
            if (_cache.TryGetValue(transports, out var cached) && now - cached.GeneratedAt < refreshInterval)
                return cached;

            var link = _node.IntoneUri(lifetime, now, transports);
            var snapshot = new LinkSnapshot(link, RenderQr(link), now);
            _cache[transports] = snapshot;
            return snapshot;
        }
    }

    private static string RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));
    }
}
