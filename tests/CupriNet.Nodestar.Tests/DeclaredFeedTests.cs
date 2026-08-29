using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// A site naming its own feed.
///
/// <para>The client used to attend <c>"overlay"</c> and nothing else, and the gateway used to bind against whichever
/// feed happened to be registered first. Those agreed only because every site written so far called its feed
/// "overlay" — a coincidence doing the work of a contract. The declaration replaces it, and both ends read the same
/// tag.</para>
///
/// <para>The client carries its own copy of this scan (it is a wasm build with a UI runtime in it, and this assembly
/// must stay free of one), so what these tests really pin is the <i>format</i> both copies have to agree on.</para>
/// </summary>
public class DeclaredFeedTests
{
    [Fact]
    public void A_page_can_name_its_feed()
        => Assert.Equal("rooms", SiteTemplate.DeclaredFeed("""
            <html><head><meta name="cupri-feed" content="rooms"></head><body>hi</body></html>
            """));

    [Fact]
    public void Single_quotes_are_a_declaration_too()
        => Assert.Equal("rooms", SiteTemplate.DeclaredFeed("<meta name='cupri-feed' content='rooms'>"));

    [Fact]
    public void Attribute_order_does_not_matter()
        => Assert.Equal("rooms", SiteTemplate.DeclaredFeed("<meta content=\"rooms\" name=\"cupri-feed\">"));

    [Fact]
    public void The_tag_is_found_among_others()
        => Assert.Equal("rooms", SiteTemplate.DeclaredFeed("""
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width">
            <meta name="cupri-feed" content="rooms">
            """));

    /// <summary>A page that says nothing gets null, and the caller decides what to fall back to.</summary>
    [Fact]
    public void A_page_that_says_nothing_declares_nothing()
        => Assert.Null(SiteTemplate.DeclaredFeed("<html><head><title>hi</title></head></html>"));

    [Fact]
    public void An_empty_declaration_is_not_a_declaration()
        => Assert.Null(SiteTemplate.DeclaredFeed("""<meta name="cupri-feed" content="   ">"""));

    /// <summary>
    /// The one that would bite quietly. A site declaring nothing must not pick up a name from an unrelated meta tag
    /// — it would attend a feed the site does not host and sit empty with nothing said.
    /// </summary>
    [Fact]
    public void Another_metas_content_is_not_mistaken_for_a_feed()
        => Assert.Null(SiteTemplate.DeclaredFeed("""<meta name="description" content="a site about rooms">"""));

    [Fact]
    public void Case_does_not_matter_in_the_tag_or_the_name()
        => Assert.Equal("Rooms", SiteTemplate.DeclaredFeed("""<META NAME="Cupri-Feed" CONTENT="Rooms">"""));

    /// <summary>Malformed markup must not throw — it renders on the fallback like any page that says nothing.</summary>
    [Fact]
    public void An_unclosed_tag_is_survivable()
        => Assert.Null(SiteTemplate.DeclaredFeed("<meta name=\"cupri-feed\" content=\"rooms\""));
}
