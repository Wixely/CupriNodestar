using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CupriNet.Nodestar;

/// <summary>
/// Binds a Document-tier page's <c>{{ }}</c> placeholders and <c>data-repeat</c> lists against a feed payload, on the
/// server, for Mode 2.
///
/// <para><b>Why this exists.</b> A Document-tier page is a TEMPLATE: its live values are placeholders a CupriFace
/// client binds when the feed arrives. Mode 2 has no client — it hands the page to an ordinary browser, which has
/// never heard of <c>{{ node.site }}</c> and renders it as literal text. Without this the gateway serves a page
/// covered in visible braces: styled correctly and obviously broken, which is a worse first impression than a plain
/// page. It affects every Document-tier site, including the onion and tunnel deployments that are Mode 2 by
/// necessity.</para>
///
/// <para><b>Why it is not CupriFace.</b> The server side carries no UI runtime, by design and by CI enforcement, and
/// the deployments that most need Mode 2 are exactly the ones that rely on that. CupriFace also offers no way to
/// hand back a bound document — <c>BuildAriaHtml</c> emits landmarks and drops the content — so there is nothing to
/// reuse even at the cost of the dependency. This is string and JSON work; nothing here lays out or paints.</para>
///
/// <para><b>It is deliberately a subset</b> — dotted paths and <c>data-repeat</c>, which is the whole surface a
/// Document-tier page uses. It is not a template language and should not become one: a site that wants more than
/// this wants Mode 1, where the real binder runs.</para>
/// </summary>
internal static partial class SiteTemplate
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex Placeholder();

    /// <summary>Whether a document has anything to bind, so the gateway can skip the cost of running a feed.</summary>
    public static bool NeedsBinding(string html) =>
        html.Contains("{{", StringComparison.Ordinal) || html.Contains("data-repeat", StringComparison.Ordinal);

    /// <summary>
    /// Binds <paramref name="html"/> against <paramref name="model"/>.
    ///
    /// <para>A placeholder that resolves to nothing becomes EMPTY rather than being left as braces. A site whose
    /// feed is missing should look like a page with blank fields, not like a page that failed to render.</para>
    /// </summary>
    public static string Bind(string html, JsonNode? model)
    {
        var output = new StringBuilder(html.Length);
        var cursor = 0;

        // Repeats are expanded first and their output appended ALREADY BOUND, never rescanned. That is what stops a
        // feed value containing "{{ … }}" from being treated as a placeholder on a later pass — template injection,
        // which matters here in a way it never did in Mode 1.
        while (FindRepeat(html, cursor) is { } repeat)
        {
            output.Append(BindPlaceholders(html.AsSpan(cursor, repeat.Start - cursor), model));

            if (Resolve(model, repeat.Collection) is JsonArray items)
            {
                foreach (var item in items)
                {
                    // Bound in the ITEM's scope, including the element's own attributes — which is how
                    // style="transform: scaleY({{ scale }})" works — and recursively, so a repeat inside a repeat
                    // resolves against the right level.
                    output.Append(Bind(repeat.Element, item));
                }
            }

            cursor = repeat.End;
        }

        output.Append(BindPlaceholders(html.AsSpan(cursor), model));
        return output.ToString();
    }

    /// <summary>
    /// Substitutes placeholders, HTML-ESCAPING every value.
    ///
    /// <para>The escaping is the security boundary, not a nicety. In Mode 1 a feed value lands in CupriFace, which
    /// has no script engine, so an injected <c>&lt;script&gt;</c> is inert — one of the tier's better properties.
    /// Mode 2 hands the same value to a real browser, which will run it. The identical payload is harmless on one
    /// path and script injection on the other, so this is the layer that has to care.</para>
    /// </summary>
    private static string BindPlaceholders(ReadOnlySpan<char> template, JsonNode? model)
    {
        var text = template.ToString();
        if (!text.Contains("{{", StringComparison.Ordinal)) return text;

        return Placeholder().Replace(text, match =>
        {
            var value = Resolve(model, match.Groups[1].Value);
            return value is null ? string.Empty : WebUtility.HtmlEncode(Stringify(value));
        });
    }

    /// <summary>A JSON value as the page should show it — the value, never its JSON quoting.</summary>
    private static string Stringify(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node.ToJsonString().Trim('"');

    /// <summary>Walks a dotted path. A missing segment yields null rather than throwing: a page outlives its feed's shape.</summary>
    private static JsonNode? Resolve(JsonNode? model, string path)
    {
        var current = model;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out current)) return null;
        }

        return current;
    }

    private readonly record struct Repeat(int Start, int End, string Collection, string Element);

    /// <summary>
    /// Finds the next <c>data-repeat</c> element and the exact span it occupies.
    ///
    /// <para>Tag matching counts nesting of the SAME tag name, so a repeated <c>&lt;div&gt;</c> containing other
    /// <c>&lt;div&gt;</c>s ends at its own closing tag rather than the first one seen. Unbalanced markup returns
    /// null and is left untouched — guessing at a malformed document is how a binder starts corrupting pages.</para>
    /// </summary>
    private static Repeat? FindRepeat(string html, int from)
    {
        var attribute = html.IndexOf("data-repeat=\"", from, StringComparison.Ordinal);
        if (attribute < 0) return null;

        var valueStart = attribute + "data-repeat=\"".Length;
        var valueEnd = html.IndexOf('"', valueStart);
        if (valueEnd < 0) return null;
        var collection = html[valueStart..valueEnd];

        var tagStart = html.LastIndexOf('<', attribute);
        if (tagStart < 0) return null;

        var nameEnd = tagStart + 1;
        while (nameEnd < html.Length && (char.IsLetterOrDigit(html[nameEnd]) || html[nameEnd] == '-')) nameEnd++;
        var tag = html[(tagStart + 1)..nameEnd];
        if (tag.Length == 0) return null;

        var openEnd = html.IndexOf('>', valueEnd);
        if (openEnd < 0) return null;

        int end;
        if (html[openEnd - 1] == '/')
        {
            end = openEnd + 1;                       // self-closing: the element is just the open tag
        }
        else
        {
            var depth = 1;
            var scan = openEnd + 1;
            while (depth > 0)
            {
                var next = html.IndexOf("<" + tag, scan, StringComparison.OrdinalIgnoreCase);
                var close = html.IndexOf("</" + tag, scan, StringComparison.OrdinalIgnoreCase);
                if (close < 0) return null;

                if (next >= 0 && next < close)
                {
                    depth++;
                    scan = next + tag.Length + 1;
                }
                else
                {
                    depth--;
                    scan = close + tag.Length + 2;
                    if (depth == 0)
                    {
                        var closeEnd = html.IndexOf('>', scan);
                        if (closeEnd < 0) return null;
                        scan = closeEnd + 1;
                    }
                }
            }

            end = scan;
        }

        // The attribute is stripped from the copies, or expanding a copy would find it again and never terminate.
        // The whitespace in front of it goes too, so `<li data-repeat="x">` becomes `<li>` rather than `<li >`.
        var removeFrom = attribute;
        while (removeFrom > tagStart && char.IsWhiteSpace(html[removeFrom - 1])) removeFrom--;

        var element = html[tagStart..end].Remove(removeFrom - tagStart, valueEnd + 1 - removeFrom);
        return new Repeat(tagStart, end, collection, element);
    }
}
