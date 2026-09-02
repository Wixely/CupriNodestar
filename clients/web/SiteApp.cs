using CupriFace;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// One fetched L2 document, presented to CupriFace's browser host as an app.
///
/// <para><b>The impedance mismatch this resolves.</b> <c>CupriApp</c> is written for a program that IS a user
/// interface: it declares its markup, and the host builds a document from it once. This client is a browser — it
/// renders whatever an Oracle answered with, and replaces it when the visitor navigates. So a site becomes a
/// short-lived <c>CupriApp</c>, and navigating re-initialises the host with a new one. That works because
/// <c>WebHostCore.Init</c> is callable more than once and yields a fresh document each time, which was checked
/// before this was built on.</para>
///
/// <para>A site carries its own <c>&lt;style&gt;</c>, which the engine collects from the markup — that is what
/// holds a page to a single Oracle consult, where a linked stylesheet would cost a second full round trip. The one
/// rule this client adds is <see cref="Css"/>, and it is a default rather than a policy: a site that declares its
/// own overflow wins, which was measured before it was relied on.</para>
/// </summary>
internal sealed class SiteApp(string html, float designWidth, float designHeight, Func<float> density) : CupriApp
{
    public override string Html => html;

    /// <summary>
    /// The one rule this client supplies: a root that can scroll.
    ///
    /// <para><b>Without it a long page cannot be read.</b> The document is painted to a canvas, so there is no
    /// browser scrollbar around it — scrolling is the engine's, and the engine only scrolls a box that asks to.
    /// Measured: a page whose content runs well past the viewport does not move on a wheel at all unless something
    /// declares a scroll container. So this client's alternatives were to shrink every long page until it was
    /// unreadable, which is what it used to do, or to give the root one rule.</para>
    ///
    /// <para><b>A default, not a constraint.</b> Measured with the same probe: a page that declares
    /// <c>overflow:hidden</c> on its body keeps it and does not scroll. So a site that manages its own overflow —
    /// a full-bleed layout, a fixed dashboard — is unaffected, and this only reaches the pages that said nothing.
    /// A client stylesheet a site could not override would be a constraint, and would not be worth having.</para>
    /// </summary>
    public override string Css => "html, body { height: 100%; overflow: auto; }";

    /// <summary>The size this site was authored for, which <see cref="Present"/> fits the viewport against.</summary>
    public override int Width => (int)designWidth;

    public override int Height => (int)designHeight;

    /// <summary>
    /// Fit the WIDTH; let the page be as long as it is.
    ///
    /// <para><b>This used to fit both axes, and that was the bug.</b> A page twice the viewport's height rendered
    /// at half size, one four times as tall at a quarter, and a genuinely long page shrank until it was unreadable
    /// — while the whole of it sat there, complete and too small to use. Nothing was ever clipped, which is why it
    /// looked like a design decision rather than a defect.</para>
    ///
    /// <para><b>Fitting the width alone is only safe because the root can now scroll</b> — see <see cref="Css"/>.
    /// Measured: with a plain body the wheel moves nothing, so this change on its own would have replaced a page
    /// too small to read with one whose bottom half could not be reached at all. That is worse: a shrunken page at
    /// least shows everything.</para>
    ///
    /// <para>Two factors are folded into the scale, and both matter. <b>Device pixel ratio</b>, without which the
    /// document lays out in device pixels and every glyph on a 2x display ends up half its intended physical size.
    /// And <b>the width fit</b>, so a viewport narrower than the design scales the page down rather than cutting
    /// its right-hand side off — horizontal scrolling for want of a few pixels is the thing every reader hates.
    /// The clamp stops a very small or very large window turning the page into confetti.</para>
    ///
    /// <para>The declared design HEIGHT is therefore no longer part of the fit. It stays on <see cref="Height"/>
    /// because the engine lays out against it, and because a site that declares a shape is still entitled to
    /// one — what changes is that the shape is not squeezed into the window.</para>
    ///
    /// <para><paramref name="windowWidth"/> and <paramref name="windowHeight"/> arrive in DEVICE pixels, because
    /// that is what the host is told the surface is.</para>
    /// </summary>
    public override PresentInfo Present(float windowWidth, float windowHeight)
    {
        var scale = density();
        if (scale <= 0) scale = 1f;

        var cssWidth = windowWidth / scale;
        var cssHeight = windowHeight / scale;

        // designHeight is still checked even though the fit no longer uses it: a site declaring a zero or negative
        // one has said something incoherent about its own shape, and laying out against that is not better than
        // falling back.
        if (cssWidth <= 0 || cssHeight <= 0 || designWidth <= 0 || designHeight <= 0)
            return new PresentInfo(windowWidth, windowHeight, 1f);

        var zoom = Math.Clamp(cssWidth / designWidth, 0.25f, 4f);
        var present = scale * zoom;

        return new PresentInfo(windowWidth / present, windowHeight / present, present);
    }
}
