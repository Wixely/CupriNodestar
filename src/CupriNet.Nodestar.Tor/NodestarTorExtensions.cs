using CupriNet.Tor;
using Microsoft.Extensions.Logging;

namespace CupriNet.Nodestar.Tor;

/// <summary>Adds the onion face to a Nodestar.</summary>
public static class NodestarTorExtensions
{
    /// <summary>The secret-store key for the face onion's identity — the thing that makes its address stable.</summary>
    private const string FaceServiceKeyId = "nodestar/tor-face-service-key";

    /// <summary>
    /// Supplies the onion transport, so <c>EnableTor</c> and <c>TorOnly</c> have something to be satisfied by.
    ///
    /// <para>Two distinct onions can come out of this, and they serve different visitors:</para>
    /// <list type="bullet">
    ///   <item><description>The <b>overlay onion</b>, always. It carries CupriNet's own traffic, which is how another
    ///   <i>node</i> conjoins and makes a Pilgrimage to this Shrine over Tor.</description></item>
    ///   <item><description>The <b>face onion</b>, when <see cref="NodestarOptions.TorFacePort"/> is set. It forwards
    ///   to the HTTP front, which is how a <i>browser</i> reaches the site — necessarily through the Mode-2 gateway,
    ///   because WebRTC is clearnet UDP and does not cross a Tor circuit.</description></item>
    /// </list>
    ///
    /// <para>Without a face port, an onion-only Nodestar is reachable by other nodes and by nobody with a browser.
    /// That is a legitimate deployment, and it is also an easy thing to configure by accident, so it is warned about
    /// rather than assumed.</para>
    /// </summary>
    /// <param name="onionOnly">
    /// Forces <see cref="NodestarOptions.TorOnly"/>: no clearnet beacons and no WebRTC on-ramp. Leave it false to run
    /// dual-stack, or to let configuration decide.
    /// </param>
    public static NodestarApplicationBuilder UseTor(this NodestarApplicationBuilder builder, bool onionOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (onionOnly) builder.Node.TorOnly = true;

        // Calling UseTor() is a statement of intent, so it opts in — but only where the operator has not already
        // spoken. An explicit EnableTor=false in appsettings or the environment must be able to turn Tor off without
        // a recompile, which it could not if a code path silently overrode it.
        if (!builder.Node.EnableTor && !builder.Node.TorOnly && !TorConfigured(builder))
            builder.Node.EnableTor = true;

        // Captured so the post-start callback can publish the face onion on the same Tor client. Building a second
        // one would mean a second bootstrap — minutes, and a second set of guards.
        CupriTorOnionTransport? transport = null;

        builder.ConfigureOnionTransport(async (_, secrets, status, cancellationToken) =>
        {
            // CreateAsync does NOT touch the network: it loads or mints the onion identity in the secret store and
            // constructs the client. Bootstrap happens later, when the node starts the transport — which is what
            // makes subscribing to Status here early enough to catch all of it.
            var created = await CupriTorOnionTransport.CreateAsync(secrets, cancellationToken).ConfigureAwait(false);
            created.Status += status;
            transport = created;
            return created;
        });

        builder.OnStarted(async (app, cancellationToken) =>
        {
            var log = app.Logger;

            if (builder.Node.TorFacePort is not int facePort)
            {
                if (builder.Node.TorOnly)
                    log.LogWarning(
                        "Onion-only, but no TorFacePort is set. Other nodes can reach this Shrine over Tor; a browser "
                        + "cannot reach it at all, because the HTTP front is not published as an onion service. Set "
                        + "TorFacePort to give it a door.");
                return;
            }

            if (!builder.Node.EnableWebFront)
            {
                log.LogWarning(
                    "TorFacePort is set but the web front is disabled, so there is nothing behind that port. Not "
                    + "publishing a face onion that would forward to a closed door.");
                return;
            }

            if (transport is null) return; // Tor was never requested; the factory did not run.

            var address = await transport.PublishAuxiliaryOnionAsync(FaceServiceKeyId, facePort, cancellationToken)
                .ConfigureAwait(false);

            log.LogInformation("Tor face: http://{Address}/", address);
            log.LogInformation(
                "That address serves the site through the Mode-2 gateway — a point-in-time snapshot. Live feed "
                + "updates need Mode 1, which does not cross Tor.");
        });

        return builder;
    }

    /// <summary>
    /// Whether configuration has an opinion about Tor, under either binding shape the builder supports (the
    /// <c>Nodestar</c> section, or the root — which is where <c>CUPRINET_NODESTAR_*</c> lands once its prefix is
    /// stripped).
    /// </summary>
    private static bool TorConfigured(NodestarApplicationBuilder builder)
    {
        string[] keys =
        [
            nameof(NodestarOptions.EnableTor),
            nameof(NodestarOptions.TorOnly),
            $"{NodestarOptions.SectionName}:{nameof(NodestarOptions.EnableTor)}",
            $"{NodestarOptions.SectionName}:{nameof(NodestarOptions.TorOnly)}",
        ];

        return Array.Exists(keys, key => builder.Configuration[key] is not null);
    }
}
