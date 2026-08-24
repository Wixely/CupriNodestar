using CupriNet.Rites;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// What a feed does when its visitor leaves.
///
/// <para>A feed publishes on its own schedule, so a departure is discovered by a send that raced the close and
/// threw. That must end the feed quietly — it is someone closing a tab, not a fault. The other half matters more:
/// everything that is NOT a departure has to keep propagating, or this becomes a blanket catch that hides an
/// over-ceiling payload or a broken projection.</para>
/// </summary>
public class FeedDepartureTests
{
    /// <summary>
    /// Stands in for the transport's own closed-vessel exception, which lives in a CupriNet assembly this package
    /// does not reference. The production code matches it by NAME for that reason, so a local type with the same
    /// name exercises exactly the path the real one takes.
    /// </summary>
    private sealed class VesselClosedException(string message) : Exception(message);

    private static async Task<Exception?> RunAsync(Func<IAuspicePublisher, CancellationToken, Task> feed)
    {
        var site = new SiteBuilder();
        site.Feed("overlay", feed);

        try
        {
            await site.Feeds["overlay"].EmanateAsync(new NullPublisher(), CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task A_closed_vessel_ends_the_feed_quietly()
        => Assert.Null(await RunAsync((_, _) => throw new VesselClosedException("The vessel is closed.")));

    [Fact]
    public async Task A_cancelled_visit_ends_the_feed_quietly()
        => Assert.Null(await RunAsync((_, _) => throw new OperationCanceledException()));

    /// <summary>A departure is often reported through a disposed session object rather than a closed vessel.</summary>
    [Fact]
    public async Task A_disposed_session_ends_the_feed_quietly()
        => Assert.Null(await RunAsync((_, _) => throw new ObjectDisposedException("session")));

    /// <summary>Departures arrive wrapped as often as not, so the check has to look down the chain.</summary>
    [Fact]
    public async Task A_wrapped_departure_ends_the_feed_quietly()
        => Assert.Null(await RunAsync((_, _) =>
            throw new InvalidOperationException("send failed", new VesselClosedException("The vessel is closed."))));

    /// <summary>
    /// The half that keeps this honest. If a real fault were swallowed, an over-ceiling payload or a null in a
    /// projection would look exactly like a visitor closing a tab — and the feed would go silent with nothing said.
    /// </summary>
    [Fact]
    public async Task A_real_fault_still_propagates()
    {
        var error = await RunAsync((_, _) => throw new InvalidOperationException("the projection is broken"));

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("the projection is broken", error.Message);
    }

    [Fact]
    public async Task An_over_ceiling_payload_still_propagates()
    {
        var error = await RunAsync((_, _) =>
            throw new ArgumentOutOfRangeException("payload", "the body exceeds the 192 KiB ceiling"));

        Assert.IsType<ArgumentOutOfRangeException>(error);
    }

    /// <summary>A feed that simply finishes is not a failure either.</summary>
    [Fact]
    public async Task A_feed_that_returns_completes_normally()
        => Assert.Null(await RunAsync((_, _) => Task.CompletedTask));

    private sealed class NullPublisher : IAuspicePublisher
    {
        public string Topic => "overlay";
        public Task SnapshotAsync(byte[] payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateAsync(byte[] payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
