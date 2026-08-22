using CupriNet.Rites;
using Xunit;

namespace CupriNet.Nodestar.Tests;

public sealed class SiteBuilderTests
{
    [Fact]
    public async Task An_unconfigured_site_answers_404_rather_than_nothing()
    {
        // The failure this guards against is a visitor hanging on a Pilgrimage that will never answer. A node with
        // no content should still say so.
        var site = new SiteBuilder();

        Assert.False(site.IsConfigured);
        var response = await site.Handler.HandleAsync(OracleRequest.Get("/"), CancellationToken.None);
        Assert.Equal(404u, response.Status);
    }

    [Fact]
    public async Task Serve_delegate_answers_the_request_it_was_given()
    {
        var site = new SiteBuilder();
        site.Serve(request => OracleResponse.Text($"you asked for {request.Path}"));

        var response = await site.Handler.HandleAsync(
            OracleRequest.Get("/hello"), CancellationToken.None);

        Assert.Equal(200u, response.Status);
        Assert.Equal("you asked for /hello", response.AsText());
    }

    [Fact]
    public void Feeds_are_keyed_by_name_and_the_last_registration_wins()
    {
        var site = new SiteBuilder();
        site.Feed("ticks", (_, _) => Task.CompletedTask);
        site.Feed("other", (_, _) => Task.CompletedTask);
        site.Feed("ticks", (_, _) => Task.CompletedTask);

        Assert.Equal(2, site.Feeds.Count);
        Assert.Contains("ticks", site.Feeds.Keys);
        Assert.Contains("other", site.Feeds.Keys);
    }

    [Fact]
    public void A_site_with_only_a_feed_still_counts_as_configured()
    {
        // Live data with no page is a legitimate site — the warning about serving nothing must not fire for it.
        var site = new SiteBuilder();
        site.Feed("ticks", (_, _) => Task.CompletedTask);

        Assert.True(site.IsConfigured);
    }

    [Fact]
    public void Feed_names_are_compared_ordinally_so_case_is_significant()
    {
        // Auspice compares topics ordinally on the wire, so the sugar must not fold case and hand a visitor a feed
        // they did not ask for.
        var site = new SiteBuilder();
        site.Feed("Ticks", (_, _) => Task.CompletedTask);
        site.Feed("ticks", (_, _) => Task.CompletedTask);

        Assert.Equal(2, site.Feeds.Count);
    }

    [Fact]
    public void Static_files_and_a_delegate_are_mutually_exclusive_last_one_wins()
    {
        var site = new SiteBuilder();
        site.ServeStaticFiles(Path.GetTempPath());
        site.Serve(_ => OracleResponse.Text("delegate"));

        Assert.IsType<DelegateOracleHandler>(site.Handler);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_feed_must_have_a_real_name(string? name)
    {
        var site = new SiteBuilder();
        Assert.ThrowsAny<ArgumentException>(() => site.Feed(name!, (_, _) => Task.CompletedTask));
    }
}
