namespace CupriNet.Nodestar.Client.CupriFace;

/// <summary>Serves Nodestar's reference browser client.</summary>
public static class NodestarCupriFaceClientExtensions
{
    /// <summary>
    /// Serves the reference client: the CupriNet client stack compiled to WebAssembly, rendered by
    /// <see href="https://github.com/Wixely/CupriFace">CupriFace</see>.
    ///
    /// <para><b>Opt-in on purpose.</b> CupriFace is this project's preference, not the platform's requirement — so
    /// it is a separate call in a separate package rather than something <c>UseWebRtc</c> decides for you. The
    /// transport accepts browser DataChannels without any opinion about what runs in the browser.</para>
    ///
    /// <para><b>What choosing it commits you to.</b> The renderer sets the site-authoring contract, not just the
    /// pixels: CupriFace embeds no JavaScript engine, so a site served to this client cannot script, and live values
    /// reach a page through CupriFace's <c>{{ }}</c> binding rather than through code the page runs. A site written
    /// against those conventions needs a CupriFace client to render it. The L2 protocol underneath stays
    /// renderer-neutral — an Oracle serves bytes with a content type — so a different client is free to make
    /// different choices.</para>
    ///
    /// <para>Use <see cref="NodestarApplicationBuilder.ServeClient"/> to supply your own instead.</para>
    /// </summary>
    public static NodestarApplicationBuilder ServeCupriFaceClient(this NodestarApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ServeClient(EmbeddedClientAssets.Get);
    }
}
