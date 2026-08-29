using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using CupriNet.Core;
using CupriNet.Vessel;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// The browser end of the on-ramp: an <see cref="IDataChannel"/> backed by a real <c>RTCPeerConnection</c>.
///
/// <para>This is the <b>only</b> new plumbing the browser tier needs. Everything above it — the Toll, the Noise
/// handshake, the Pilgrimage, Oracle and Auspice — is the same C# that runs on the node, reached through
/// <see cref="DataChannelVessel"/>. Nothing above this class learns that WebRTC exists.</para>
///
/// <para>Interop is <c>DllImport("js")</c> into an Emscripten JS library rather than <c>[JSImport]</c>, because
/// NativeAOT-LLVM has no Mono runtime and therefore no JS interop layer to borrow.</para>
/// </summary>
internal sealed partial class BrowserDataChannel : IDataChannel
{
    /// <summary>
    /// The inbound buffer, and the largest message this adapter will hand to the channel.
    ///
    /// <para>Sized at 256 KiB because that is what this node's association offers, and a receive buffer has to be
    /// allocated before anything is negotiated. Distinct from <see cref="MaxMessageBytes"/>, which is what the two
    /// ends actually agreed and is only knowable once the channel is open.</para>
    /// </summary>
    private const int BufferBytes = 256 * 1024;

    private readonly byte[] _buffer = new byte[BufferBytes];
    private bool _disposed;

    [LibraryImport("js", EntryPoint = "cupri_connect")]
    private static partial void Connect(IntPtr parametersJson);

    [LibraryImport("js", EntryPoint = "cupri_state")]
    private static partial int State();

    [LibraryImport("js", EntryPoint = "cupri_sctp_max")]
    private static partial int SctpMax();

    /// <summary>
    /// The largest message this association negotiated, or 0 before the channel opens.
    ///
    /// <para>This is the number CupriNet 0.6.0 added <c>IDataChannel.MaxMessageBytes</c> for, and it is not the same
    /// number as a rite's ceiling — only one of them is a constant. A rite advertises 192 KiB whatever it runs over;
    /// what a DataChannel will carry is whatever the two ends agreed. <c>DataChannelVessel</c> emits one message per
    /// frame and never fragments, so nothing in between reconciles them, and a peer negotiating the 64 KiB
    /// interoperable floor would refuse a frame the rite called legal.</para>
    ///
    /// <para><b>Reporting it truthfully is something this adapter can do and the .NET one currently cannot.</b>
    /// CupriWebRTC's association knows its own figure, but <c>WebRtcChannel</c> does not expose it, so that adapter
    /// answers 0 — the honest answer, since guessing would refuse pairings that work. A browser hands the same value
    /// over for free as <c>RTCSctpTransport.maxMessageSize</c>, so on this path
    /// <c>RiteTransport.EnsureCarriesRiteFrames</c> gets a real number to check and a low-negotiating peer fails at
    /// connect with both figures named, rather than on the wire. See CupriNet#6.</para>
    ///
    /// <para>0 before the channel opens, which the contract reads as "unknown" — so a check that runs too early is
    /// skipped rather than answered with a guess.</para>
    /// </summary>
    public int MaxMessageBytes => SctpMax();

    /// <summary>The same figure, for logging before the vessel exists.</summary>
    public static int NegotiatedMaxMessageBytes => SctpMax();

    [LibraryImport("js", EntryPoint = "cupri_seed")]
    private static partial int SeedLink(IntPtr buffer, int capacity);

    [LibraryImport("js", EntryPoint = "cupri_close")]
    private static partial void CloseCore();

    [LibraryImport("js", EntryPoint = "cupri_refresh_seed")]
    private static partial void RefreshSeedCore();

    [LibraryImport("js", EntryPoint = "cupri_seed_serial")]
    private static partial int SeedSerialCore();

    [LibraryImport("js", EntryPoint = "cupri_send")]
    private static partial int Send(IntPtr data, int length);

    [LibraryImport("js", EntryPoint = "cupri_recv")]
    private static partial int Receive(IntPtr buffer, int capacity);

    /// <summary>
    /// The node's own signed link, which the page fetched before this module loaded.
    ///
    /// <para>Read through the shim rather than with <c>HttpClient</c> on purpose: without Mono there is no browser
    /// HTTP handler behind <c>HttpClient</c>, so it would compile cleanly and fail at runtime.</para>
    /// </summary>
    /// <summary>
    /// Asks the page to re-fetch the serving node's link. Returns immediately — the fetch is asynchronous, so
    /// <see cref="SeedSerial"/> is how a caller learns whether it landed.
    ///
    /// <para>Needed because a restarted node is unreachable at its old coordinates: ICE credentials and the DTLS
    /// certificate are generated per process and persisted nowhere, so the link this client booted with dies with
    /// the process that minted it. Reconnecting means re-fetching, not re-dialling.</para>
    /// </summary>
    public static void RequestSeedRefresh() => RefreshSeedCore();

    /// <summary>Advances on each successful seed re-fetch; unchanged means the node is still unreachable.</summary>
    public static int SeedSerial() => SeedSerialCore();

    public static unsafe string Seed()
    {
        var buffer = new byte[4096];
        fixed (byte* pointer = buffer)
        {
            var length = SeedLink((IntPtr)pointer, buffer.Length);
            if (length <= 0) throw new InvalidOperationException("The host page supplied no seed link.");
            return Encoding.UTF8.GetString(buffer, 0, length);
        }
    }

    public EndPoint? RemoteEndPoint => null;   // the browser is not told its peer's address
    public EndPoint? LocalEndPoint => null;    // nor its own

    /// <summary>
    /// Dials the node described by <paramref name="intonation"/> and waits for the DataChannel to open.
    ///
    /// <para>The parameters come from the <b>signed</b> link, which is why there is no signalling round trip: the
    /// browser synthesises the answer locally and its first packet goes straight to the address the link named.</para>
    /// </summary>
    public static async Task<BrowserDataChannel> ConnectAsync(Intonation intonation, CancellationToken cancellationToken)
    {
        var webRtc = intonation.WebRtc
            ?? throw new InvalidOperationException("This node's link carries no WebRTC endpoint, so a browser cannot dial it.");

        var host = FirstClearnetHost(intonation)
            ?? throw new InvalidOperationException("This node's link carries no clearnet beacon to dial.");

        var parameters = new JsonObject
        {
            ["host"] = host,
            ["port"] = webRtc.Port,
            ["ufrag"] = webRtc.IceUfrag,
            ["password"] = webRtc.IcePassword,
            ["fingerprintAlgorithm"] = webRtc.FingerprintAlgorithm,
            ["fingerprint"] = Convert.ToHexString(webRtc.Fingerprint),
        }.ToJsonString();

        var channel = new BrowserDataChannel();
        var json = Marshal.StringToHGlobalAnsi(parameters);
        try
        {
            Connect(json);
        }
        finally
        {
            Marshal.FreeHGlobal(json);
        }

        // Yielding rather than blocking is not a style choice: wasm runs on the browser's single thread, so a spin
        // here would stop the very event loop that completes the handshake.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (State())
            {
                case 1: return channel;
                case 2: throw new InvalidOperationException("The WebRTC connection failed.");
                case 3: throw new InvalidOperationException("The WebRTC connection closed before it opened.");
            }

            await BrowserLoop.NextFrameAsync().ConfigureAwait(false);
        }
    }

    public unsafe ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.Length > BufferBytes)
            throw new InvalidOperationException(
                $"A DataChannel message may not exceed {BufferBytes} bytes; this one is {message.Length}.");

        fixed (byte* pointer = message.Span)
        {
            if (Send((IntPtr)pointer, message.Length) != 0)
                throw new InvalidOperationException("The DataChannel refused the message.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = ReceiveOne();
            if (length > 0) return _buffer[..length];
            if (length < 0) throw new InvalidOperationException("An inbound message exceeded the channel's limit.");

            // Nothing waiting. State 2/3 means the channel will never deliver again, so report closure rather than
            // polling a dead connection forever.
            var state = State();
            if (state is 2 or 3) return null;

            await BrowserLoop.NextFrameAsync().ConfigureAwait(false);
        }
    }

    private unsafe int ReceiveOne()
    {
        fixed (byte* pointer = _buffer)
            return Receive((IntPtr)pointer, _buffer.Length);
    }

    /// <summary>
    /// Ends the visit and closes the underlying peer connection.
    ///
    /// <para>Closing the JS side is the part that matters. Marking this object disposed stops MANAGED reads, but the
    /// browser's <c>RTCPeerConnection</c> outlives it happily — handlers attached, still delivering into the shared
    /// inbox. The next visit then met frames from the previous one mid-handshake, which surfaced as a Noise failure
    /// naming a stream the handshake has no business seeing.</para>
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        CloseCore();
        return ValueTask.CompletedTask;
    }

    /// <summary>The first clearnet host in the link. An onion beacon is useless to a browser, which cannot dial one.</summary>
    private static string? FirstClearnetHost(Intonation intonation)
    {
        foreach (var beacon in intonation.Beacons)
        {
            if (beacon.Kind == EndpointKind.Onion) continue;
            if (!string.IsNullOrWhiteSpace(beacon.Host)) return beacon.Host;
        }

        return null;
    }
}
