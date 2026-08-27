using System.Text;
using System.Threading.Channels;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// What a raw session does, driven over a real <see cref="ConduitSession"/> on both ends rather than a stand-in.
///
/// <para>The pair below is an in-memory <see cref="IStreamChannel"/>, so every test crosses the real codec, the real
/// seal handling and the real close semantics. That matters more here than in most places: the behaviour this class
/// exists to provide — a clean close arriving as null rather than as an exception — is exactly the behaviour a mock
/// would have been written to assume, and a test built on that assumption would have passed against the version of
/// the rite where the read never returned at all.</para>
/// </summary>
public class RawSessionTests
{
    private const uint Banter = 0xB4A7E5;
    private const uint SomethingElse = 0x0FFBEA7;

    /// <summary>Every wait is bounded. A session bug shows up as a hang, and a hung test says far less than a failed one.</summary>
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(10));

    /// <summary>The site's end and the visitor's end of one conduit, already connected.</summary>
    private static (SiteSession Site, ConduitSession Visitor, LoopbackChannel VisitorChannel) Connect(
        uint protocolId = Banter)
    {
        var toSite = Channel.CreateUnbounded<byte[]>();
        var toVisitor = Channel.CreateUnbounded<byte[]>();

        var siteChannel = new LoopbackChannel(toSite.Reader, toVisitor.Writer);
        var visitorChannel = new LoopbackChannel(toVisitor.Reader, toSite.Writer);

        var site = new ConduitSession(siteChannel, ConduitPadding.None, ConduitCodec.MaxPayloadBytes);
        var visitor = new ConduitSession(visitorChannel, ConduitPadding.None, ConduitCodec.MaxPayloadBytes);

        return (new SiteSession(site, protocolId), visitor, visitorChannel);
    }

    private static ConduitFrame Frame(uint protocolId, string text) => new()
    {
        ProtocolId = protocolId,
        SchemaVersion = 1,
        Flags = 0,
        Payload = Encoding.UTF8.GetBytes(text),
    };

    [Fact]
    public async Task A_frame_arrives_whole()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await visitor.SendAsync(Frame(Banter, "JOIN #cupri"), deadline.Token);

        var received = await site.ReceiveAsync(deadline.Token);

        Assert.NotNull(received);
        Assert.Equal("JOIN #cupri", Encoding.UTF8.GetString(received));
    }

    /// <summary>
    /// The property the seam is for: what goes in as one message comes out as one message. A byte-stream transport
    /// would let two sends coalesce and force the protocol above to re-frame them.
    /// </summary>
    [Fact]
    public async Task Frames_keep_their_boundaries()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await visitor.SendAsync(Frame(Banter, "ONE"), deadline.Token);
        await visitor.SendAsync(Frame(Banter, "TWO"), deadline.Token);

        Assert.Equal("ONE", Encoding.UTF8.GetString((await site.ReceiveAsync(deadline.Token))!));
        Assert.Equal("TWO", Encoding.UTF8.GetString((await site.ReceiveAsync(deadline.Token))!));
    }

    [Fact]
    public async Task The_site_can_answer()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await site.SendAsync(Encoding.UTF8.GetBytes(":server 001 welcome"), deadline.Token);

        var frame = await visitor.ReceiveAsync(deadline.Token);

        Assert.NotNull(frame);
        Assert.Equal(Banter, frame.ProtocolId);
        Assert.Equal(":server 001 welcome", Encoding.UTF8.GetString(frame.Payload));
    }

    /// <summary>
    /// A visitor closing their tab. This is the ordinary end of a session, and it must not be an exception — the
    /// whole point of the seam is that a handler can loop until null without a catch block around it.
    /// </summary>
    [Fact]
    public async Task A_visitor_who_vanishes_ends_the_session_with_null()
    {
        using var deadline = Deadline();
        var (site, _, visitorChannel) = Connect();

        visitorChannel.Close();

        Assert.Null(await site.ReceiveAsync(deadline.Token));
    }

    /// <summary>A departure has nothing to report, so there is no reason to report.</summary>
    [Fact]
    public async Task A_visitor_who_vanishes_leaves_no_reason()
    {
        using var deadline = Deadline();
        var (site, _, visitorChannel) = Connect();

        visitorChannel.Close();
        await site.ReceiveAsync(deadline.Token);

        Assert.Null(site.EndReason);
    }

    [Fact]
    public async Task A_sealed_session_ends_with_the_reason_it_was_given()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await visitor.SendSealedAsync("leaving", deadline.Token);

        Assert.Null(await site.ReceiveAsync(deadline.Token));
        Assert.Equal("leaving", site.EndReason);
    }

    /// <summary>
    /// Null latches. A handler that loops on receive will call it again after the close, and every one of those must
    /// answer null rather than blocking on a wire nobody is writing to.
    /// </summary>
    [Fact]
    public async Task Null_keeps_being_null()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await visitor.SendSealedAsync("leaving", deadline.Token);

        Assert.Null(await site.ReceiveAsync(deadline.Token));
        Assert.Null(await site.ReceiveAsync(deadline.Token));
        Assert.Null(await site.ReceiveAsync(deadline.Token));
        Assert.Equal("leaving", site.EndReason);
    }

    [Fact]
    public async Task A_frame_for_another_protocol_ends_the_session()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await visitor.SendAsync(Frame(SomethingElse, "hello?"), deadline.Token);

        Assert.Null(await site.ReceiveAsync(deadline.Token));
        Assert.Equal("unknown protocol", site.EndReason);
    }

    /// <summary>
    /// The half that matters to the peer. Ignoring a frame from the wrong protocol would leave whoever sent it
    /// waiting on a reply that is never coming; sealing tells them they reached the wrong thing.
    /// </summary>
    [Fact]
    public async Task A_frame_for_another_protocol_is_answered_rather_than_ignored()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await visitor.SendAsync(Frame(SomethingElse, "hello?"), deadline.Token);
        await site.ReceiveAsync(deadline.Token);

        var answer = await visitor.ReceiveAsync(deadline.Token);

        Assert.NotNull(answer);
        Assert.True(answer.IsSealed);
        Assert.Equal("unknown protocol", answer.SealReason);
    }

    /// <summary>
    /// The refusal has to latch locally. Sealing tells the peer, but nothing arrives back to tell us — so without
    /// the session remembering that it ended, a handler looping on receive would block here forever, which is the
    /// same shape of hang the rite itself had before the seal latched.
    /// </summary>
    [Fact]
    public async Task A_refused_protocol_does_not_leave_the_next_receive_hanging()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await visitor.SendAsync(Frame(SomethingElse, "hello?"), deadline.Token);

        Assert.Null(await site.ReceiveAsync(deadline.Token));
        Assert.Null(await site.ReceiveAsync(deadline.Token));
    }

    [Fact]
    public async Task Ending_from_the_site_tells_the_visitor_why()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await site.EndAsync("not authenticated", deadline.Token);

        var answer = await visitor.ReceiveAsync(deadline.Token);

        Assert.NotNull(answer);
        Assert.True(answer.IsSealed);
        Assert.Equal("not authenticated", answer.SealReason);
    }

    [Fact]
    public async Task Ending_twice_says_it_once()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        await site.EndAsync("not authenticated", deadline.Token);
        await site.EndAsync("not authenticated", deadline.Token);

        Assert.True((await visitor.ReceiveAsync(deadline.Token))!.IsSealed);
        Assert.Null(await visitor.ReceiveAsync(deadline.Token));
    }

    /// <summary>
    /// The ceiling a site author has to size against. It is the payload limit before padding, not the padded frame
    /// size — a distinction worth pinning, because chunking written against the wrong one fails only on big frames.
    /// </summary>
    [Fact]
    public void The_frame_ceiling_is_the_rites_payload_ceiling()
    {
        var (site, _, _) = Connect();

        Assert.Equal(ConduitCodec.MaxPayloadBytes, site.MaxFrameBytes);
        Assert.Equal(192 * 1024, site.MaxFrameBytes);
    }

    [Fact]
    public async Task A_frame_at_the_ceiling_still_crosses()
    {
        using var deadline = Deadline();
        var (site, visitor, _) = Connect();

        var big = new byte[site.MaxFrameBytes];
        Array.Fill(big, (byte)0x5A);

        await site.SendAsync(big, deadline.Token);

        var frame = await visitor.ReceiveAsync(deadline.Token);

        Assert.NotNull(frame);
        Assert.Equal(big, frame.Payload);
    }

    /// <summary>A site that serves only a raw session is a configured site, not an empty one.</summary>
    [Fact]
    public void A_site_that_serves_only_a_session_is_configured()
    {
        var site = new SiteBuilder();
        Assert.False(site.IsConfigured);

        site.OnSession(Banter, (_, _) => Task.CompletedTask);

        Assert.True(site.IsConfigured);
        Assert.NotNull(site.Conduit);
    }

    /// <summary>A site that never called OnSession hosts no conduit, which is what lets the Shrine seal one.</summary>
    [Fact]
    public void A_site_without_a_session_hosts_no_conduit()
        => Assert.Null(new SiteBuilder().Serve(_ => OracleResponse.NotFound()).Conduit);

    private static async Task<Exception?> RunHandlerAsync(Func<SiteSession, CancellationToken, Task> handler)
    {
        var site = new SiteBuilder();
        site.OnSession(Banter, handler);

        var toSite = Channel.CreateUnbounded<byte[]>();
        var toVisitor = Channel.CreateUnbounded<byte[]>();
        var conduit = new ConduitSession(
            new LoopbackChannel(toSite.Reader, toVisitor.Writer), ConduitPadding.None, ConduitCodec.MaxPayloadBytes);

        try
        {
            await site.Conduit!.AttendAsync(conduit, CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Stands in for the transport's own closed-vessel exception, which lives in a CupriNet assembly this package
    /// does not reference. The production code matches it by NAME for that reason.
    /// </summary>
    private sealed class VesselClosedException(string message) : Exception(message);

    /// <summary>
    /// A session is duplex, so a departure can be discovered by a send as easily as by a receive. The receive path
    /// answers null; this is the other half, where the author was mid-send when the visitor left.
    /// </summary>
    [Fact]
    public async Task A_departure_during_a_send_ends_the_session_quietly()
        => Assert.Null(await RunHandlerAsync((_, _) => throw new VesselClosedException("The vessel is closed.")));

    [Fact]
    public async Task A_cancelled_visit_ends_the_session_quietly()
        => Assert.Null(await RunHandlerAsync((_, _) => throw new OperationCanceledException()));

    [Fact]
    public async Task A_wrapped_departure_ends_the_session_quietly()
        => Assert.Null(await RunHandlerAsync((_, _) =>
            throw new InvalidOperationException("send failed", new VesselClosedException("The vessel is closed."))));

    /// <summary>
    /// The half that keeps the quiet path honest. A bug in a protocol handler must not look like a visitor closing
    /// a tab, or a session will end silently with nothing said about why.
    /// </summary>
    [Fact]
    public async Task A_real_fault_in_a_handler_still_propagates()
    {
        var error = await RunHandlerAsync((_, _) => throw new InvalidOperationException("the room is broken"));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("the room is broken", error.Message);
    }

    [Fact]
    public async Task A_handler_that_returns_completes_normally()
        => Assert.Null(await RunHandlerAsync((_, _) => Task.CompletedTask));

    /// <summary>
    /// One end of an in-memory conduit. Closing the writer is how a peer going away is expressed — the rite reads a
    /// null from the channel and reports the close, which is the path a real departure takes.
    /// </summary>
    private sealed class LoopbackChannel(ChannelReader<byte[]> inbound, ChannelWriter<byte[]> outbound)
        : IStreamChannel
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => outbound.WriteAsync(payload.ToArray(), cancellationToken);

        public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await inbound.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        /// <summary>The peer goes away.</summary>
        public void Close() => outbound.Complete();
    }
}
