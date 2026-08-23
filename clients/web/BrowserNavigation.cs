using System.Runtime.InteropServices;
using System.Text;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// The address bar seam: links the visitor submits, and the status the client writes back to its own chrome.
///
/// <para><b>Why links and not bare <c>cupri1…</c> addresses.</b> A Signet names a site but does not say where to
/// reach it. In v1 "the site address travels <i>with its reachability</i>, carried in the link that delivered it"
/// (CupriNet's <c>websites-l2.md</c>), so a bare address cannot be dialled — resolving one to a moving host is L1
/// roaming, which is a later phase. The address bar therefore takes an intonation link, and says so, rather than
/// accepting input that could never work.</para>
/// </summary>
internal static unsafe partial class BrowserNavigation
{
    /// <summary>Generous: a link carries beacons, a Litany and optionally a Signet, so it is not small.</summary>
    private const int MaxLinkBytes = 8192;

    [LibraryImport("js", EntryPoint = "cupri_take_link")]
    private static partial int TakeLink(IntPtr buffer, int capacity);

    [LibraryImport("js", EntryPoint = "cupri_status")]
    private static partial void SetStatus(IntPtr utf8);

    /// <summary>
    /// The link the visitor submitted, or null. <b>Takes</b> rather than peeks — the JS side clears it — so one
    /// submit yields exactly one visit no matter how often this is polled.
    /// </summary>
    public static string? TakePendingLink()
    {
        var buffer = new byte[MaxLinkBytes];
        fixed (byte* pointer = buffer)
        {
            var length = TakeLink((IntPtr)pointer, buffer.Length);
            return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
        }
    }

    /// <summary>
    /// Writes a line into the client's own chrome.
    ///
    /// <para>Chrome, deliberately: it is the client saying where you are, in a region no site paints into. A site
    /// that could write here could claim to be a node it is not, which is the address-bar spoofing problem imported
    /// wholesale.</para>
    /// </summary>
    public static void Status(string message)
    {
        var utf8 = Encoding.UTF8.GetBytes(message + '\0');
        fixed (byte* pointer = utf8)
            SetStatus((IntPtr)pointer);
    }
}
