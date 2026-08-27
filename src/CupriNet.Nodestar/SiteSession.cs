using CupriNet.Rites;

namespace CupriNet.Nodestar;

/// <summary>
/// One visitor's raw session: whole frames in, whole frames out, for as long as they stay.
///
/// <para>This is the plain-naming adapter over the Conduit rite, in the same spirit as <c>Serve</c> over the Oracle
/// and <c>Feed</c> over the Auspice. What it removes is the rite's envelope: a site author sending a protocol frame
/// should not have to build a <see cref="ConduitFrame"/>, choose a schema version, or learn which flag bits belong
/// to them. They send bytes and receive bytes, and the discriminator is declared once when the session is
/// registered.</para>
///
/// <para><b>Message-framed, not a byte stream.</b> One <see cref="SendAsync"/> is one
/// <see cref="ReceiveAsync"/> at the far end — no length prefix invented on top, because the rite and the
/// DataChannel beneath it both preserve message boundaries already. A protocol that already frames itself keeps its
/// own framing and adds nothing.</para>
///
/// <para><b>Each frame is capped at <see cref="MaxFrameBytes"/></b> (192 KiB on a visitor's Pilgrimage) — a ceiling
/// the browser's SCTP association sets, not us, and measured on the payload before padding. Sessions carry protocol
/// traffic; for anything bulky, the Relic rite is the transfer built for it.</para>
/// </summary>
public sealed class SiteSession
{
    /// <summary>
    /// Stamped on every frame this side sends. The rite does not interpret it — <c>ProtocolId</c> is the
    /// discriminator, and a protocol that needs to version itself is better off doing so inside its own payload,
    /// where it controls the rules.
    /// </summary>
    private const uint SchemaVersion = 1;

    private readonly ConduitSession _conduit;
    private bool _ended;

    internal SiteSession(ConduitSession conduit, uint protocolId)
    {
        _conduit = conduit;
        ProtocolId = protocolId;
    }

    /// <summary>The protocol this session speaks. Frames carrying any other id are refused, not ignored.</summary>
    public uint ProtocolId { get; }

    /// <summary>The largest frame this session will carry, in bytes, measured before padding.</summary>
    public int MaxFrameBytes => _conduit.MaxPayloadBytes;

    /// <summary>
    /// Why the session ended, when the far end said. Null while it is still running, and null when the visitor
    /// simply went away without a word — which is the ordinary case, not a fault.
    /// </summary>
    public string? EndReason { get; private set; }

    /// <summary>Sends one frame. Frames never interleave, so this is safe to call from more than one task.</summary>
    /// <remarks>
    /// <c>Flags</c> is sent as zero. The top bit is the rite's, reserved for the seal that ends a session, and the
    /// low 31 are the application's — but a seam that carries bytes has nothing to put in them, and a protocol that
    /// wants flags has its own header to put them in. Dropping to <c>OnSession(IConduitHandler)</c> reaches them.
    /// </remarks>
    public Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        => _conduit.SendAsync(
            new ConduitFrame
            {
                ProtocolId = ProtocolId,
                SchemaVersion = SchemaVersion,
                Flags = 0,
                Payload = frame.ToArray(),
            },
            cancellationToken);

    /// <summary>
    /// Receives one frame, or null once the session is over.
    ///
    /// <para><b>A clean close is null, not an exception.</b> Someone closing a tab is the most ordinary thing that
    /// happens to a session, and a transport that reports it by throwing pushes every consumer into writing the
    /// same catch-and-classify block to tell "they left" apart from "something broke". Null is that answer, and it
    /// latches: once this has returned null it keeps returning null.</para>
    ///
    /// <para>Everything else still propagates, deliberately — an over-ceiling frame, a broken vessel, a bug in the
    /// handler above are all real and all reported. Only the departure is quiet.</para>
    /// </summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (_ended) return null;

        var frame = await _conduit.ReceiveAsync(cancellationToken).ConfigureAwait(false);

        // The visitor went away. Nothing was said and nothing needs to be.
        if (frame is null) return End(null);

        // The far end ended the session and gave a reason. Still a close, not a fault — the reason is worth
        // surfacing, which is what EndReason is for, but it does not belong on the error path.
        if (frame.IsSealed) return End(frame.SealReason);

        // Someone dialled a different protocol down this conduit. Answering rather than ignoring is the rite's own
        // convention, and it is the difference between a peer learning it reached the wrong site and a peer waiting
        // on a reply that was never going to come.
        if (frame.ProtocolId != ProtocolId)
        {
            // CancellationToken.None: the token that brought us here may already be cancelled, and a peer owed an
            // explanation should still get one.
            await _conduit.SendSealedAsync("unknown protocol", CancellationToken.None).ConfigureAwait(false);
            return End("unknown protocol");
        }

        return frame.Payload;
    }

    /// <summary>
    /// Ends the session from this side, telling the visitor why — an authentication failure, a room that closed, a
    /// protocol violation. Their next receive returns null and the reason travels with it.
    /// </summary>
    public async Task EndAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (_ended) return;

        await _conduit.SendSealedAsync(reason, cancellationToken).ConfigureAwait(false);
        End(reason);
    }

    private byte[]? End(string? reason)
    {
        _ended = true;
        EndReason = reason;
        return null;
    }
}
