using CupriNet.Nodestar.Tor;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// The Tor wiring, tested structurally.
///
/// <para><b>Nothing here bootstraps Tor or opens a circuit</b>, and that is a constraint rather than an oversight:
/// the development machine has no Tor access, so a test that dialled would fail for reasons unrelated to the code.
/// What can be checked without a network is the part that was actually written here — that requesting Tor now finds
/// a transport to satisfy it, that the opt-in does not trample an operator's explicit "no", and that the refusal
/// still fires when no transport was supplied. The circuit itself remains unverified, and is marked so in TODO.md.</para>
/// </summary>
public class TorWiringTests
{
    /// <summary>The point of the package: <c>EnableTor</c> now has something to be satisfied by.</summary>
    [Fact]
    public void UseTor_supplies_an_onion_transport()
    {
        var builder = NodestarApplication.CreateBuilder([]);
        Assert.Null(builder.OnionTransportFactory);

        builder.UseTor();

        Assert.NotNull(builder.OnionTransportFactory);
    }

    /// <summary>Calling it is a statement of intent, so it opts in rather than waiting to also be configured.</summary>
    [Fact]
    public void UseTor_opts_in_when_configuration_is_silent()
    {
        var builder = NodestarApplication.CreateBuilder([]);
        Assert.False(builder.Node.EnableTor);

        builder.UseTor();

        Assert.True(builder.Node.EnableTor);
    }

    /// <summary>
    /// The other half of that, and the more important half: an operator must be able to turn Tor off in
    /// configuration without editing code. If <c>UseTor()</c> overrode an explicit <c>false</c>, they could not.
    /// </summary>
    [Fact]
    public void UseTor_respects_an_explicit_configuration_opt_out()
    {
        var builder = NodestarApplication.CreateBuilder(["--EnableTor=false"]);

        builder.UseTor();

        Assert.False(builder.Node.EnableTor);
        Assert.NotNull(builder.OnionTransportFactory); // still available — configured off, not absent
    }

    /// <summary>Onion-only is a stronger statement than dual-stack, so the flag sets it outright.</summary>
    [Fact]
    public void UseTor_onionOnly_forces_TorOnly()
    {
        var builder = NodestarApplication.CreateBuilder([]);

        builder.UseTor(onionOnly: true);

        Assert.True(builder.Node.TorOnly);
    }

    /// <summary>
    /// The regression guard on the fail-fast. Requesting Tor with no transport must refuse to start: coming up
    /// clearnet-only while the configuration asks for an onion is the one failure an anonymity setting cannot have,
    /// because everything looks healthy while the protection is simply absent.
    /// </summary>
    [Fact]
    public async Task Requesting_Tor_without_a_transport_refuses_to_start()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "nodestar-tor-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = NodestarApplication.CreateBuilder([]);
            builder.Node.EnableTor = true;      // requested…
            builder.Node.DataDirectory = dataDir;
            builder.Node.EnableWebFront = false;
            // …and deliberately NOT calling UseTor().

            var app = builder.Build();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());

            Assert.Contains("UseTor", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
    }
}
