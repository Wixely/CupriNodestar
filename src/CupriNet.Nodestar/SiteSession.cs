using System.Threading.Channels;
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
///
/// <para><b>Delivery is ordered and never torn — but it is not retried.</b> That is the Conduit's contract, and it
/// has a trap in it that this class exists partly to close. A receiver that lets frames queue past the mux's
/// per-stream limit loses the ones past it <i>silently</i>: the sender's write reports success, and no field in a
/// frame reveals the gap. Only the Epistle has a Vigil to recover from that; a conduit frame is simply gone, and
/// neither end finds out.</para>
///
/// <para>So this <b>drains</b>. A background reader takes frames off the rite as fast as they arrive and parks them
/// here, which keeps the queue that drops silently empty. If the author's handler then falls behind, it is
/// <i>this</i> queue that fills — and it says so, loudly, instead of quietly losing a protocol's messages. See
/// <see cref="ReceiveAsync"/>.</para>
/// </summary>
public sealed class SiteSession
{
    /// <summary>
    /// Stamped on every frame this side sends. The rite does not interpret it — <c>ProtocolId</c> is the
    /// discriminator, and a protocol that needs to version itself is better off doing so inside its own payload,
    /// where it controls the rules.
    /// </summary>
    private const uint SchemaVersion = 1;

    /// <summary>
    /// How far a handler may fall behind before the session fails rather than pretends.
    ///
    /// <para>Smaller than the 1,024 the mux allows, on purpose: the point is to fail <i>earlier and more cheaply</i>
    /// than the transport would, and to fail loudly where the transport would not. A session with this many frames
    /// outstanding does not have a burst, it has a reader that is losing — and 256 frames of up to 192 KiB is
    /// already a great deal of memory to be holding on someone's behalf.</para>
    /// </summary>
    private const int Backlog = 256;

    private readonly ConduitSession _conduit;
    private readonly Channel<byte[]> _inbox =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Backlog) { SingleReader = true, SingleWriter = true });

    private Task? _draining;
    private bool _ended;

    /// <summary>Set when the handler fell behind, so <see cref="ReceiveAsync"/> can say so instead of ending quietly.</summary>
    private bool _overrun;

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

    /// <summary>Begins draining. Called before the author's handler runs, so nothing queues on the rite.</summary>
    internal void Start(CancellationToken cancellationToken) => _draining ??= Task.Run(() => DrainAsync(cancellationToken));

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
    /// <para><b>Falling behind is an exception, and deliberately so.</b> Frames arrive whether or not the handler is
    /// ready for them. If enough pile up unread the session ends with an <see cref="InvalidOperationException"/>
    /// rather than continuing minus the frames it could not hold — because a protocol that silently loses messages
    /// does not fail, it corrupts, and the far end has no way to notice. Take each frame and come straight back for
    /// the next; do the work elsewhere.</para>
    ///
    /// <para>Everything else still propagates too — an over-ceiling frame, a broken vessel, a bug in the handler
    /// above are all real and all reported. Only the departure is quiet.</para>
    /// </summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        // A caller that never went through the site builder — a test, or someone driving this directly — still gets
        // a drained session rather than the trap this class exists to close.
        Start(CancellationToken.None);

        try
        {
            if (await _inbox.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                && _inbox.Reader.TryRead(out var frame))
            {
                return frame;
            }
        }
        catch (ChannelClosedException)
        {
            // The drain finished between the wait and the read; fall through to the ending below.
        }

        if (_overrun)
        {
            throw new InvalidOperationException(
                $"The session fell behind by more than {Backlog} frames and has been ended. Frames arrive whether or "
                + "not the handler is ready, and continuing would drop them silently — take each frame and return "
                + "for the next, doing the work on another task.");
        }

        return null;
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

    /// <summary>
    /// Takes frames off the rite as fast as they arrive, so the queue that drops silently stays empty.
    ///
    /// <para>Every decision about what a frame <i>means</i> lives here rather than in <see cref="ReceiveAsync"/>,
    /// because they have to be made at the speed frames arrive rather than at the speed the handler reads.</para>
    /// </summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _conduit.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                // The visitor went away. Nothing was said and nothing needs to be.
                if (frame is null) { End(null); return; }

                // The far end ended the session and gave a reason. Still a close, not a fault — the reason is worth
                // surfacing, which is what EndReason is for, but it does not belong on the error path.
                if (frame.IsSealed) { End(frame.SealReason); return; }

                // Someone dialled a different protocol down this conduit. Answering rather than ignoring is the
                // rite's own convention, and it is the difference between a peer learning it reached the wrong site
                // and a peer waiting on a reply that was never going to come.
                if (frame.ProtocolId != ProtocolId)
                {
                    // CancellationToken.None: the token that brought us here may already be cancelled, and a peer
                    // owed an explanation should still get one.
                    await _conduit.SendSealedAsync("unknown protocol", CancellationToken.None).ConfigureAwait(false);
                    End("unknown protocol");
                    return;
                }

                if (!_inbox.Writer.TryWrite(frame.Payload))
                {
                    // The handler is not keeping up. Stopping here is the whole point: carrying on would mean
                    // dropping this frame and every one after it with nothing said, which is what the rite does on
                    // its own and what this class exists to prevent.
                    _overrun = true;
                    End("receiver fell behind");
                    return;
                }
            }
        }
        catch (Exception ex) when (IsDeparture(ex))
        {
            End(null);
        }
        catch (Exception ex)
        {
            // A real fault has to reach the handler rather than looking like a close. Parking it on the channel is
            // how it gets there: the next ReceiveAsync throws it, on the caller's own thread.
            _ended = true;
            _inbox.Writer.TryComplete(ex);
        }
    }

    /// <summary>Whether an exception means "the peer went away" rather than "something is wrong".</summary>
    /// <remarks>
    /// The transport's closed-vessel exception is matched by NAME rather than by type, for the same reason
    /// <c>SiteBuilder</c> does it: the type lives in a CupriNet assembly this package does not reference.
    /// </remarks>
    private static bool IsDeparture(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException or ObjectDisposedException) return true;
            if (current.GetType().Name is "VesselClosedException") return true;
        }

        return false;
    }

    private void End(string? reason)
    {
        _ended = true;
        EndReason = reason;
        _inbox.Writer.TryComplete();
    }
}
