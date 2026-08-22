namespace CupriNet.Nodestar;

/// <summary>One file of the served browser client: its bytes and the content type to serve it with.</summary>
/// <remarks>
/// <c>application/wasm</c> is load-bearing rather than cosmetic. Served as <c>application/octet-stream</c> the browser
/// refuses to stream-instantiate the module and the client simply fails to start, with no obvious cause.
/// </remarks>
public sealed record ClientAsset(byte[] Content, string ContentType);
