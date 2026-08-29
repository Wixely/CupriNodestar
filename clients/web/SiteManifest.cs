namespace CupriNet.Nodestar.Client;

/// <summary>
/// The few things a site can tell the client about itself, read out of its own markup.
///
/// <para><b>Why the site has to say, and why in markup.</b> The client attended a feed called <c>"overlay"</c> and
/// nothing else, so every Document-tier site had to call its feed that — a client whose whole job is rendering
/// whatever site it is pointed at should not know any particular site's feed names. Markup rather than a response
/// header because <c>ServeStaticFiles</c> is the common case and it cannot set headers: the file on disk is the
/// only thing the author controls, so that is where the declaration has to live.</para>
///
/// <para><b>The gateway holds a copy of <see cref="DeclaredFeed"/>.</b> It reads the same tag when it binds a page
/// server-side, so Mode 1 and Mode 2 agree on which feed a page is about instead of both guessing. The two cannot
/// share an implementation — this is a wasm build carrying CupriFace and the server assembly must stay free of a UI
/// runtime — so they are deliberately the same algorithm written twice rather than two ways of reading one tag.
/// Keep them in step; <c>DeclaredFeedTests</c> pins the format both have to agree on.</para>
/// </summary>
internal static class SiteManifest
{
    /// <summary>
    /// What a site's feed is called when it does not say. Kept because every site written before this existed
    /// assumes it, and attending nothing would be a worse answer than attending the old default.
    /// </summary>
    public const string DefaultFeed = "overlay";

    /// <summary>The feed this page is about: what it declares, or <see cref="DefaultFeed"/> when it declares nothing.</summary>
    public static string FeedName(string html) => DeclaredFeed(html) ?? DefaultFeed;

    /// <summary>
    /// The feed a page declares through <c>&lt;meta name="cupri-feed" content="…"&gt;</c>, or null when it says
    /// nothing.
    ///
    /// <para>Scanned rather than parsed. The client has a real HTML parser a moment later, but the name is needed
    /// <i>before</i> the document is built in order to attend, and standing one up to read a single attribute would
    /// mean parsing the page twice. The scan is deliberately narrow: it matches the one tag it looks for and treats
    /// anything it does not understand as absent, so a page it cannot read still renders on the default.</para>
    /// </summary>
    public static string? DeclaredFeed(string html)
    {
        var index = 0;

        while (true)
        {
            index = html.IndexOf("<meta", index, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;

            var close = html.IndexOf('>', index);
            if (close < 0) return null;      // an unclosed tag: nothing dependable left to read

            var tag = html[index..close];
            index = close + 1;

            if (!string.Equals(Attribute(tag, "name"), "cupri-feed", StringComparison.OrdinalIgnoreCase)) continue;

            var feed = Attribute(tag, "content").Trim();
            if (feed.Length > 0) return feed;
        }
    }

    /// <summary>One quoted attribute out of a tag, or empty. Single and double quotes; anything else is not a match.</summary>
    private static string Attribute(string tag, string attribute)
    {
        var at = tag.IndexOf(attribute, StringComparison.OrdinalIgnoreCase);

        while (at >= 0)
        {
            // Whitespace before, or "name" would match inside another attribute's value — which is how a page with
            // <meta name="description" content="…rooms…"> would otherwise be read as declaring a feed.
            var boundary = at > 0 && char.IsWhiteSpace(tag[at - 1]);
            var cursor = at + attribute.Length;

            if (boundary)
            {
                while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor])) cursor++;

                if (cursor < tag.Length && tag[cursor] == '=')
                {
                    cursor++;
                    while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor])) cursor++;

                    if (cursor < tag.Length && (tag[cursor] == '"' || tag[cursor] == '\''))
                    {
                        var quote = tag[cursor++];
                        var end = tag.IndexOf(quote, cursor);
                        if (end >= 0) return tag[cursor..end];
                    }
                }
            }

            at = tag.IndexOf(attribute, at + 1, StringComparison.OrdinalIgnoreCase);
        }

        return string.Empty;
    }
}
