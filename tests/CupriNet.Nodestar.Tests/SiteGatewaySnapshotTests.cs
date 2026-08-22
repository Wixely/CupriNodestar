using System.Text;
using CupriNet.Rites;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// The Mode-2 snapshot path. It has to run a live feed just far enough to capture its opening state and then stop
/// it — so the interesting cases are all about <b>not hanging</b> and <b>not leaking a running source</b>.
/// </summary>
public sealed class SiteGatewaySnapshotTests
{
    [Fact]
    public async Task Captures_the_snapshot_and_ignores_the_updates_that_follow()
    {
        var site = new SiteBuilder();
        site.Feed("ticks", async (publisher, ct) =>
        {
            await publisher.SnapshotAsync(Utf8("opening state"), ct);
            for (var i = 0; i < 1000 && !ct.IsCancellationRequested; i++)
                await publisher.UpdateAsync(Utf8($"tick {i}"), ct);
        });

        var snapshot = await new SiteGateway(site).SnapshotAsync("ticks", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("opening state", Encoding.UTF8.GetString(snapshot));
    }

    [Fact]
    public async Task A_feed_that_never_ends_is_stopped_rather_than_left_running()
    {
        // The source blocks forever after snapshotting, which is exactly what a real feed does. The gateway must
        // cancel it once it has what it needs, or every gateway page view would leak a live subscription.
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var site = new SiteBuilder();
        site.Feed("ticks", async (publisher, ct) =>
        {
            await publisher.SnapshotAsync(Utf8("state"), ct);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { stopped.TrySetResult(); }
        });

        var snapshot = await new SiteGateway(site).SnapshotAsync("ticks", CancellationToken.None);

        Assert.NotNull(snapshot);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task An_unknown_feed_is_null_not_an_exception()
    {
        var snapshot = await new SiteGateway(new SiteBuilder())
            .SnapshotAsync("nope", CancellationToken.None);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task A_source_that_updates_before_snapshotting_still_answers()
    {
        // Malformed — a well-formed source snapshots first. Taking the first message anyway is what stops one badly
        // written feed from stalling the gateway for everyone.
        var site = new SiteBuilder();
        site.Feed("ticks", async (publisher, ct) =>
        {
            await publisher.UpdateAsync(Utf8("update first"), ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        var snapshot = await new SiteGateway(site).SnapshotAsync("ticks", CancellationToken.None);

        Assert.Equal("update first", Encoding.UTF8.GetString(Assert.IsType<byte[]>(snapshot)));
    }

    [Fact]
    public async Task A_source_that_throws_before_snapshotting_does_not_take_the_request_down()
    {
        var site = new SiteBuilder();
        site.Feed("ticks", (_, _) => throw new InvalidOperationException("bad feed"));

        // The failure surfaces as the source's exception rather than a hang; what must not happen is the request
        // blocking until the snapshot timeout for a feed that already failed.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SiteGateway(site).SnapshotAsync("ticks", CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_request_gives_up_instead_of_waiting_for_the_timeout()
    {
        var site = new SiteBuilder();
        site.Feed("ticks", (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var snapshot = await new SiteGateway(site).SnapshotAsync("ticks", cancelled.Token);

        Assert.Null(snapshot);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
