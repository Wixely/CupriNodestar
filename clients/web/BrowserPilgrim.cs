using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Rites;
using CupriNet.Vessel;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// The Pilgrim half without the node: Toll → Noise (pinning the Signet) → the site rites, over a vessel the caller
/// supplies.
///
/// <para><b>Why this exists.</b> Upstream's Pilgrim entry (<c>CupriNode.PilgrimageOverVesselAsync</c>) is an instance
/// method, and constructing a <c>CupriNode</c> binds a TCP listener — which in a browser is
/// <c>PlatformNotSupportedException: System.Net.Sockets</c>, measured in Chromium, not assumed. Yet the method's body
/// touches nothing of the node but its <c>Suite</c> and <c>Network</c>: a Pilgrim mints a throwaway identity per
/// visit precisely so that no node state is involved.</para>
///
/// <para><b>This is a faithful transcription of that body over public upstream types</b> — <c>Toll.SolveAsync</c>,
/// <c>NoiseConjunction.InitiateAsync(expectedPeer: signet)</c>, then the exact <c>ShrineSession</c> wiring (a
/// <c>VesselMux</c>, Oracle on stream 5, Auspice on stream 7, no Veil because the Noise vessel is already the
/// confidentiality boundary). The one thing it could not reuse is <c>ShrineSession</c> itself: its constructor is
/// <c>internal</c>.</para>
///
/// <para><b>The right home for this is CupriNet</b>, by upstream's own reasoning when it took ownership of the
/// Auspice — "the Pilgrim half runs inside the client stack, including where that stack is compiled to WASM". A
/// static, node-free Pilgrim entry (or a public <c>ShrineSession</c> constructor) upstream deletes this file. Until
/// then it must be kept in lockstep with <c>CupriNode.Shrine.cs</c>, and it is deliberately small enough to diff by
/// eye.</para>
/// </summary>
internal sealed class BrowserPilgrim : IAsyncDisposable
{
    private readonly IVessel _vessel;
    private readonly VesselMux _mux;
    private readonly OracleSession _oracle;
    private readonly AuspiceSession _auspices;

    private BrowserPilgrim(IVessel vessel)
    {
        _vessel = vessel;
        // Mirrors ShrineSession: no Veil on either rite — the Pilgrimage vessel (Noise) is already the
        // confidentiality + authenticity boundary.
        _mux = new VesselMux(vessel, ownsVessel: false);
        _oracle = new OracleSession(_mux.Stream(OracleSession.RequestStream));
        _auspices = new AuspiceSession(_mux.Stream(AuspiceSession.AuspiceStream));
    }

    /// <summary>
    /// Dials the Shrine over <paramref name="vessel"/>: solves the Toll, then runs the Noise handshake under a
    /// <b>throwaway per-visit identity</b>, pinning <paramref name="expectedSignet"/> — so only the holder of the
    /// site's key can complete it, and the visitor reveals nothing durable about themselves.
    /// </summary>
    public static async Task<BrowserPilgrim> PilgrimageAsync(
        IVessel vessel, Sigil expectedSignet, Concordium network, ICryptoSuite suite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vessel);
        try
        {
            await Toll.SolveAsync(vessel, cancellationToken).ConfigureAwait(false);
            var pilgrim = NodeIdentity.Generate(suite);
            var conjunction = await NoiseConjunction.InitiateAsync(
                vessel, pilgrim, network, suite, expectedPeer: expectedSignet, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new BrowserPilgrim(conjunction.Vessel);
        }
        catch
        {
            await vessel.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Consults the Shrine: one Oracle request, one response.</summary>
    public Task<OracleResponse> ConsultAsync(OracleRequest request, CancellationToken cancellationToken = default)
        => _oracle.ConsultAsync(request, cancellationToken);

    /// <summary>Attends a named live feed: a snapshot, then updates, concurrently with <see cref="ConsultAsync"/>.</summary>
    public IAsyncEnumerable<AuspiceFrame> AttendAsync(string topic, CancellationToken cancellationToken = default)
        => _auspices.AttendAllAsync(topic, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _mux.DisposeAsync().ConfigureAwait(false);
        await _vessel.DisposeAsync().ConfigureAwait(false);
    }
}
