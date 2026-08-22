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
    /// <summary>Matches the 256 KiB the node's SCTP association will accept; anything larger is refused at the rite.</summary>
    private const int MaxMessageBytes = 256 * 1024;

    private readonly byte[] _buffer = new byte[MaxMessageBytes];
    private bool _disposed;

    [LibraryImport("js", EntryPoint = "cupri_connect")]
    private static partial void Connect(IntPtr parametersJson);

    [LibraryImport("js", EntryPoint = "cupri_state")]
    private static partial int State();

    [LibraryImport("js", EntryPoint = "cupri_seed")]
    private static partial int SeedLink(IntPtr buffer, int capacity);

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
        if (message.Length > MaxMessageBytes)
            throw new InvalidOperationException(
                $"A DataChannel message may not exceed {MaxMessageBytes} bytes; this one is {message.Length}.");

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

    public ValueTask DisposeAsync()
    {
        _disposed = true;
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
