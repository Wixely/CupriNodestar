using System.Net;
using System.Text;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Hosting;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// A real Pilgrim reaching a real site over a real vessel — the thing nothing had done before.
///
/// <para>Every other test in this repository stops short of the transport: the rites are exercised over an in-memory
/// channel, and the browser gate proves Mode 1 but only for the Oracle and the Auspice. That left a gap wide enough
/// for <see href="https://github.com/Wixely/CupriNodestar/issues/2">#2</see> to live in, where a conduit opened over
/// TCP was closed immediately and <c>OnSession</c> never ran. The cause was not the conduit: a TCP connection to the
/// node's listen port reaches the NODE, so no rite reached a site — the Oracle failed the same way, which is what
/// showed the conduit was never the problem.</para>
///
/// <para>These tests pin the fix and the diagnosis at once. They pin the site's <b>Signet</b> from the client, which
/// is the thing that could not be pinned before, and they check the Oracle beside the conduit so that a future
/// regression says which of the two broke.</para>
/// </summary>
public class PilgrimageOverVesselTests : IAsyncLifetime
{
    private const uint ProtocolId = 0xB4A7E5;

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "nodestar-tests-" + Guid.NewGuid().ToString("N")[..12]);

    private NodestarApplication _app = null!;
    private VesselListener _listener = null!;
    private CupriNet.Abstractions.Sigil _siteSignet;
    private CupriNet.Abstractions.Concordium _network;
    private byte[] _relic = [];
    private volatile bool _handlerRan;

    public async Task InitializeAsync()
    {
        var builder = NodestarApplication.CreateBuilder([]);
        builder.Node.DataDirectory = _dataDirectory;
        builder.Node.ListenAddress = "127.0.0.1";
        builder.Node.ListenPort = 0;
        builder.Node.EnableWebRtc = false;
        builder.Node.EnableTor = false;
        builder.Node.EnableWebFront = false;
        builder.Node.EnableLanDiscovery = false;
        builder.Node.EnablePortMapping = false;
        builder.Node.EnableFerryman = false;
        builder.Node.AdvertiseSiteInLink = true;

        // A Shrine port of the node's own, alongside the caller-owned listener below. Both paths are exercised
        // because both are supported and they fail differently: this one is what a desktop client uses, the other is
        // what WASM and in-process tests use.
        builder.Node.ShrinePort = 0;

        builder.Site.Serve(_ => OracleResponse.Text("<h1>hello</h1>", "text/html"));

        // Deliberately larger than one rite frame. A relic that fitted in a single message would prove nothing:
        // the chunking, and the manifest that makes it verifiable, are the whole point of the rite.
        _relic = new byte[500_000];
        Random.Shared.NextBytes(_relic);
        builder.Site.ServeRelic("app/big.bin", _relic);
        builder.Site.OnSession(ProtocolId, async (session, cancellationToken) =>
        {
            _handlerRan = true;
            while (await session.ReceiveAsync(cancellationToken) is { } frame)
                await session.SendAsync(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(frame)), cancellationToken);
        });

        _app = builder.Build();
        await _app.StartAsync();

        // The site's own Signet, read the way a visitor reads it — out of the node's signed link.
        var intonation = _app.Node.Intone(TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, []);
        _siteSignet = intonation.Shrine!.Value;
        _network = intonation.Network;

        // The caller owns accepting; the application owns what the site answers with.
        _listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var vessel = await _listener.AcceptAsync(CancellationToken.None).ConfigureAwait(false);
                _ = Task.Run(() => _app.AcceptPilgrimageAsync(vessel));
            }
        });
    }

    public async Task DisposeAsync()
    {
        await _listener.DisposeAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Opens a Pilgrimage pinning the SITE's Signet — the thing #2 could not do.</summary>
    private async Task<ShrineSession> VisitAsync(CancellationToken cancellationToken)
    {
        var vessel = await TcpVessel.ConnectAsync("127.0.0.1", _listener.LocalEndPoint.Port);
        return await Pilgrimage.OverVesselAsync(
            vessel, _siteSignet, _network, new BouncyCastleSuite(), cancellationToken: cancellationToken);
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(30));

    /// <summary>
    /// A relic fetched whole, over the same visit, and verified on the way.
    ///
    /// <para>This is the answer to the 192 KiB ceiling, so the payload here is deliberately ~500 KB — nearly three
    /// frames. It cannot arrive as one message, which means what is being tested is the chunking and the manifest
    /// rather than a large-ish response.</para>
    ///
    /// <para>The equality assertion is doing more work than it looks: <c>FetchRelicAsync</c> verifies every chunk
    /// against the manifest as it arrives and the whole file before returning any bytes, so bytes coming back at all
    /// means the verification passed. That property — integrity proven <i>before</i> anything runs the content — is
    /// the reason to use a relic rather than a big Oracle body.</para>
    /// </summary>
    [Fact]
    public async Task A_relic_is_fetched_whole_and_verified()
    {
        using var deadline = Deadline();
        await using var shrine = await VisitAsync(deadline.Token);

        var fetched = await shrine.FetchRelicAsync("app/big.bin", cancellationToken: deadline.Token);

        Assert.Equal(_relic.Length, fetched.Length);
        Assert.Equal(_relic, fetched);
        Assert.True(fetched.Length > ConduitCodec.MaxPayloadBytes,
            "the relic must exceed one rite frame or this proves nothing about chunking");
    }

    /// <summary>A relic this site does not name is refused, rather than answered with something else or with silence.</summary>
    [Fact]
    public async Task An_unknown_relic_is_refused()
    {
        using var deadline = Deadline();
        await using var shrine = await VisitAsync(deadline.Token);

        await Assert.ThrowsAnyAsync<Exception>(
            () => shrine.FetchRelicAsync("app/not-a-thing.bin", cancellationToken: deadline.Token));
    }

    /// <summary>
    /// The path with no accept loop in it at all — CupriNet 0.5.0's own Shrine listener, reached by dialling.
    ///
    /// <para>This is what #2 was really asking for. The caller-owned vessel is still the seam everything else is
    /// built on, but a desktop or service client should not have to write a <c>VesselListener</c> loop to visit a
    /// site, and our consumer wrote one because there was nothing else.</para>
    ///
    /// <para>It also inverts the failure that caused #2. On the node's L1 port, pinning the node's Sigil succeeded
    /// into a session with no Shrine behind it; here the host presents only the Signet, so the site's address is the
    /// key that works and a wrong one cannot complete at all.</para>
    /// </summary>
    [Fact]
    public async Task A_pilgrim_can_dial_the_shrine_port_directly()
    {
        using var deadline = Deadline();

        var endpoint = _app.ShrineEndPoint;
        Assert.NotNull(endpoint);

        var vessel = await TcpVessel.ConnectAsync("127.0.0.1", endpoint!.Port);
        await using var shrine = await Pilgrimage.OverVesselAsync(
            vessel, _siteSignet, _network, new BouncyCastleSuite(), cancellationToken: deadline.Token);

        var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), deadline.Token);
        Assert.Equal(200u, page.Status);

        await shrine.Conduits.SendAsync(new ConduitFrame
        {
            ProtocolId = ProtocolId,
            SchemaVersion = 1,
            Flags = 0,
            Payload = Encoding.UTF8.GetBytes("PORT"),
        }, deadline.Token);

        var reply = await shrine.Conduits.ReceiveAsync(deadline.Token);
        Assert.NotNull(reply);
        Assert.Equal("echo:PORT", Encoding.UTF8.GetString(reply.Payload));
    }

    /// <summary>
    /// The regression that matters most. In #2 this threw — the peer answering was the node, so the site's own
    /// Signet did not match it. That it now succeeds is the whole fix: a site can be addressed over a raw vessel.
    /// </summary>
    [Fact]
    public async Task A_pilgrim_can_pin_the_sites_signet()
    {
        using var deadline = Deadline();
        await using var shrine = await VisitAsync(deadline.Token);
        Assert.NotNull(shrine);
    }

    /// <summary>
    /// Checked beside the conduit deliberately. In #2 the Oracle failed identically, and noticing that is what
    /// proved the conduit was never the broken thing. If this ever fails again, the fault is the transport.
    /// </summary>
    [Fact]
    public async Task The_oracle_answers_over_a_vessel()
    {
        using var deadline = Deadline();
        await using var shrine = await VisitAsync(deadline.Token);

        var page = await shrine.ConsultAsync(OracleRequest.Get("/index.html"), deadline.Token);

        Assert.Equal(200u, page.Status);
        Assert.Contains("hello", page.AsText());
    }

    /// <summary>A conduit frame crossing a real transport and coming back — the first time that has happened.</summary>
    [Fact]
    public async Task A_conduit_frame_round_trips_over_a_vessel()
    {
        using var deadline = Deadline();
        await using var shrine = await VisitAsync(deadline.Token);

        await shrine.Conduits.SendAsync(new ConduitFrame
        {
            ProtocolId = ProtocolId,
            SchemaVersion = 1,
            Flags = 0,
            Payload = Encoding.UTF8.GetBytes("JOIN #cupri"),
        }, deadline.Token);

        var reply = await shrine.Conduits.ReceiveAsync(deadline.Token);

        Assert.NotNull(reply);
        Assert.Equal("echo:JOIN #cupri", Encoding.UTF8.GetString(reply.Payload));
        Assert.True(_handlerRan, "the OnSession handler never ran");
    }

    /// <summary>
    /// The Pilgrim's half of the ceiling. #2 reported there was nowhere to read it and hard-coded 196608; there is,
    /// and this pins that it agrees with what the site is willing to send.
    /// </summary>
    [Fact]
    public async Task A_pilgrim_can_read_its_own_frame_ceiling()
    {
        using var deadline = Deadline();
        await using var shrine = await VisitAsync(deadline.Token);

        Assert.Equal(ConduitCodec.MaxPayloadBytes, shrine.Conduits.MaxPayloadBytes);
    }

    /// <summary>
    /// Refusing an unrecognised protocol, over a real transport rather than an in-memory channel — a peer that
    /// dialled the wrong site is told so, instead of waiting on a reply that is never coming.
    /// </summary>
    [Fact]
    public async Task A_frame_for_another_protocol_is_sealed_over_a_vessel()
    {
        using var deadline = Deadline();
        await using var shrine = await VisitAsync(deadline.Token);

        await shrine.Conduits.SendAsync(new ConduitFrame
        {
            ProtocolId = 0x0FFBEA7,
            SchemaVersion = 1,
            Flags = 0,
            Payload = Encoding.UTF8.GetBytes("hello?"),
        }, deadline.Token);

        var answer = await shrine.Conduits.ReceiveAsync(deadline.Token);

        Assert.NotNull(answer);
        Assert.True(answer.IsSealed);
        Assert.Equal("unknown protocol", answer.SealReason);
    }
}
