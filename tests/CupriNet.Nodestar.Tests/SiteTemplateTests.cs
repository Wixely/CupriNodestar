using System.Text.Json.Nodes;
using Xunit;

namespace CupriNet.Nodestar.Tests;

/// <summary>
/// The Mode-2 binder: what the gateway does with a page no client will bind.
///
/// <para>The escaping tests are the important ones. In Mode 1 a feed value reaches CupriFace, which has no script
/// engine, so an injected <c>&lt;script&gt;</c> is inert. Mode 2 hands the same value to a real browser, which runs
/// it — the identical payload is harmless on one path and script injection on the other.</para>
/// </summary>
public class SiteTemplateTests
{
    private static string Bind(string html, string json) => SiteTemplate.Bind(html, JsonNode.Parse(json));

    [Fact]
    public void It_substitutes_a_dotted_path()
        => Assert.Equal("<p>cupri1abc</p>", Bind("<p>{{ node.site }}</p>", """{"node":{"site":"cupri1abc"}}"""));

    [Fact]
    public void It_substitutes_numbers_without_json_quoting()
        => Assert.Equal("<p>42</p>", Bind("<p>{{ live.viewers }}</p>", """{"live":{"viewers":42}}"""));

    /// <summary>A page outlives its feed's shape, so a path that no longer exists must not throw or leave braces.</summary>
    [Fact]
    public void A_missing_path_becomes_empty_rather_than_braces()
        => Assert.Equal("<p></p>", Bind("<p>{{ node.gone }}</p>", """{"node":{}}"""));

    [Fact]
    public void A_page_with_no_model_at_all_still_renders()
        => Assert.Equal("<p></p>", SiteTemplate.Bind("<p>{{ anything }}</p>", null));

    // ---- escaping: the security boundary --------------------------------------------------------------------

    [Fact]
    public void A_value_containing_script_is_escaped()
    {
        var html = Bind("<p>{{ moniker }}</p>", """{"moniker":"<script>alert(1)</script>"}""");

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    /// <summary>An attribute-position injection escapes the quote rather than the angle bracket.</summary>
    [Fact]
    public void A_value_breaking_out_of_an_attribute_is_escaped()
    {
        var html = Bind("""<div title="{{ moniker }}"></div>""", """{"moniker":"\" onmouseover=\"alert(1)"}""");

        Assert.DoesNotContain("onmouseover=\"alert", html, StringComparison.Ordinal);
        Assert.Contains("&quot;", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// Template injection. A value that itself looks like a placeholder must be emitted as text, not re-bound on a
    /// later pass — otherwise a feed could reach into parts of the model the page never referenced.
    /// </summary>
    [Fact]
    public void A_value_that_looks_like_a_placeholder_is_not_rebound()
    {
        var html = Bind(
            "<p>{{ moniker }}</p>",
            """{"moniker":"{{ secret }}","secret":"leaked"}""");

        Assert.DoesNotContain("leaked", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_item_value_that_looks_like_a_placeholder_is_not_rebound()
    {
        var html = Bind(
            """<ul><li data-repeat="rows">{{ name }}</li></ul>""",
            """{"rows":[{"name":"{{ secret }}"}],"secret":"leaked"}""");

        Assert.DoesNotContain("leaked", html, StringComparison.Ordinal);
    }

    // ---- data-repeat ----------------------------------------------------------------------------------------

    [Fact]
    public void It_repeats_an_element_per_item()
    {
        var html = Bind(
            """<ul><li data-repeat="rows">{{ name }}</li></ul>""",
            """{"rows":[{"name":"one"},{"name":"two"}]}""");

        Assert.Equal("<ul><li>one</li><li>two</li></ul>", html);
    }

    /// <summary>The repeat attribute must not survive into the copies, or expansion would never terminate.</summary>
    [Fact]
    public void The_repeat_attribute_is_stripped_from_the_copies()
        => Assert.DoesNotContain("data-repeat", Bind(
            """<ul><li data-repeat="rows">{{ name }}</li></ul>""", """{"rows":[{"name":"one"}]}"""),
            StringComparison.Ordinal);

    /// <summary>An empty or missing collection renders nothing rather than leaving the template element behind.</summary>
    [Fact]
    public void An_empty_collection_renders_no_items()
        => Assert.Equal("<ul></ul>", Bind("""<ul><li data-repeat="rows">{{ name }}</li></ul>""", """{"rows":[]}"""));

    [Fact]
    public void A_missing_collection_renders_no_items()
        => Assert.Equal("<ul></ul>", Bind("""<ul><li data-repeat="rows">{{ name }}</li></ul>""", """{}"""));

    /// <summary>Bars carry their value in a style attribute, so attribute binding inside a repeat is load-bearing.</summary>
    [Fact]
    public void It_binds_attributes_inside_a_repeated_element()
    {
        var html = Bind(
            """<div class="plot"><div class="bar" data-repeat="bars" style="transform: scaleY({{ scale }})"></div></div>""",
            """{"bars":[{"scale":0.5},{"scale":1}]}""");

        Assert.Contains("scaleY(0.5)", html, StringComparison.Ordinal);
        Assert.Contains("scaleY(1)", html, StringComparison.Ordinal);
    }

    /// <summary>The sparklines are a repeat inside a repeat, each level scoped to its own item.</summary>
    [Fact]
    public void It_expands_a_repeat_inside_a_repeat()
    {
        var html = Bind(
            """<div data-repeat="charts"><h2>{{ label }}</h2><span data-repeat="bars">{{ v }}</span></div>""",
            """{"charts":[{"label":"CPU","bars":[{"v":1},{"v":2}]},{"label":"Mem","bars":[{"v":3}]}]}""");

        Assert.Equal("<div><h2>CPU</h2><span>1</span><span>2</span></div><div><h2>Mem</h2><span>3</span></div>", html);
    }

    /// <summary>Nesting of the SAME tag has to be counted, or the element ends at the first close tag seen.</summary>
    [Fact]
    public void It_matches_the_right_closing_tag_when_the_same_tag_nests()
    {
        var html = Bind(
            """<div data-repeat="rows"><div class="inner">{{ name }}</div></div>""",
            """{"rows":[{"name":"one"},{"name":"two"}]}""");

        Assert.Equal(
            """<div><div class="inner">one</div></div><div><div class="inner">two</div></div>""", html);
    }

    [Fact]
    public void Content_outside_the_repeat_is_preserved_on_both_sides()
    {
        var html = Bind(
            """<p>before</p><ul><li data-repeat="rows">{{ name }}</li></ul><p>{{ after }}</p>""",
            """{"rows":[{"name":"x"}],"after":"tail"}""");

        Assert.Equal("<p>before</p><ul><li>x</li></ul><p>tail</p>", html);
    }

    /// <summary>Guessing at malformed markup is how a binder starts corrupting pages, so it declines instead.</summary>
    [Fact]
    public void Unbalanced_markup_is_left_alone_rather_than_guessed_at()
    {
        const string broken = """<ul><li data-repeat="rows">{{ name }}</ul>""";

        Assert.Contains("data-repeat", SiteTemplate.Bind(broken, JsonNode.Parse("""{"rows":[]}""")),
            StringComparison.Ordinal);
    }

    // ---- the cheap path -------------------------------------------------------------------------------------

    /// <summary>An ordinary static site must not pay for a feed snapshot it has no use for.</summary>
    [Fact]
    public void A_page_with_nothing_to_bind_is_recognised()
    {
        Assert.False(SiteTemplate.NeedsBinding("<html><body><p>plain</p></body></html>"));
        Assert.True(SiteTemplate.NeedsBinding("<p>{{ x }}</p>"));
        Assert.True(SiteTemplate.NeedsBinding("""<li data-repeat="rows"></li>"""));
    }
}
