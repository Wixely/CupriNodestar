using System.Net;
using CupriNet.WebRtc;

namespace CupriNet.Nodestar.WebRtc;

/// <summary>Adds the browser on-ramp (Mode 1) to a Nodestar.</summary>
public static class NodestarWebRtcExtensions
{
    /// <summary>
    /// Gives the node a WebRTC endpoint that browsers can dial, and serves the WASM client that dials it.
    ///
    /// <para>There is no signalling server, and there is no place to put one: the node publishes its ICE credentials
    /// and DTLS fingerprint inside its own <b>signed</b> Intonation, so a browser that has the link already holds the
    /// remote description. The page is served with that link inlined, which is why it can dial back with no user
    /// input and no third party learning that the visit happened.</para>
    ///
    /// <para>Clearnet UDP, so it is skipped in onion-only mode — using it there would publish the node's clearnet IP
    /// and push the visitor off their own Tor path, which is exactly what the onion exists to prevent.</para>
    /// </summary>
    public static NodestarApplicationBuilder UseWebRtc(this NodestarApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureTransport((options, log) =>
        {
            if (options.TorOnly)
            {
                log("WebRTC is ignored in onion-only mode (it is a clearnet UDP transport).");
                return null;
            }

            if (!options.EnableWebRtc) return null;

            var port = options.WebRtcPort ?? options.ListenPort;
            try
            {
                // Best-effort by design: the browser on-ramp is on by default, so a busy UDP port must degrade to
                // "no Mode 1 today" rather than stop a node from serving its site over the gateway.
                var transport = new CupriWebRtcTransport(new IPEndPoint(IPAddress.Parse(options.ListenAddress), port));
                log($"WebRTC endpoint on UDP :{transport.LocalEndPoint.Port} (DTLS 1.3, 1.2 fallback).");
                return transport;
            }
            catch (Exception ex)
            {
                log($"Could not bind the WebRTC endpoint on UDP :{port} ({ex.Message}); continuing without it.");
                return null;
            }
        });

        // Deliberately does NOT serve a client. Accepting browser DataChannels and deciding what runs in the browser
        // are separate concerns, and this package only owns the first. Add ServeCupriFaceClient() for the reference
        // client, or ServeClient(...) for your own — a node with a WebRTC endpoint and no client is a perfectly
        // coherent thing, reachable by any client someone else is running.
        return builder;
    }
}
