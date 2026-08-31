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
/// <para>No <c>Css</c>: an L2 document carries its own <c>&lt;style&gt;</c>, which the engine collects from the
/// markup. Keeping it there is what holds a page to a single Oracle consult, where a linked stylesheet would cost a
/// second full round trip.</para>
/// </summary>
internal sealed class SiteApp(string html, float designWidth, float designHeight, Func<float> density) : CupriApp
{
    public override string Html => html;

    /// <summary>The size this site was authored for, which <see cref="Present"/> fits the viewport against.</summary>
    public override int Width => (int)designWidth;

    public override int Height => (int)designHeight;

    /// <summary>
    /// Hybrid zoom: fit the tighter axis, let the longer one reflow.
    ///
    /// <para>This is the hook that replaces a <c>Zoom()</c> this client used to compute and apply by hand, and the
    /// arithmetic is deliberately identical. What changes is who owns it: the host now scales the canvas AND divides
    /// incoming pointer positions by the same number, so painting and hit-testing cannot drift apart. They were kept
    /// in step before by both calling one private method — a discipline rather than a guarantee.</para>
    ///
    /// <para>Two factors are folded into the scale, and both matter. <b>Device pixel ratio</b>, without which the
    /// document lays out in device pixels and every glyph on a 2x display ends up half its intended physical size.
    /// And <b>the fit itself</b>, so a viewport narrower than the design scales the page down rather than clipping
    /// it. The clamp stops a very small or very large window turning the page into confetti.</para>
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
        if (cssWidth <= 0 || cssHeight <= 0 || designWidth <= 0 || designHeight <= 0)
            return new PresentInfo(windowWidth, windowHeight, 1f);

        var zoom = Math.Clamp(Math.Min(cssWidth / designWidth, cssHeight / designHeight), 0.25f, 4f);
        var present = scale * zoom;

        return new PresentInfo(windowWidth / present, windowHeight / present, present);
    }
}
