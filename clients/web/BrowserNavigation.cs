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

    [LibraryImport("js", EntryPoint = "cupri_suggest_link")]
    private static partial void SuggestLinkCore(IntPtr utf8);

    /// <summary>
    /// Offers a link back into the address bar, for the visitor to send again if they want it.
    ///
    /// <para>A node reached by a pasted link cannot be reconnected to automatically: a restarted node regenerates
    /// its ICE credentials and DTLS fingerprint, so the link that reached it is permanently dead, and the only way
    /// to obtain a fresh one is an HTTP fetch — which this page can do with its origin and nowhere else. So the
    /// client genuinely cannot get back on its own.</para>
    ///
    /// <para>What it can do is not make the visitor go and find the link again. Putting the dead one back in the
    /// field turns "hunt for wherever that link came from" into one click once the node returns. The page refuses
    /// to overwrite anything already typed there, so a suggestion can never cost an edit in progress.</para>
    /// </summary>
    public static void SuggestLink(string link)
    {
        var utf8 = Encoding.UTF8.GetBytes(link + '\0');
        fixed (byte* pointer = utf8)
            SuggestLinkCore((IntPtr)pointer);
    }

    [LibraryImport("js", EntryPoint = "cupri_take_back")]
    private static partial int TakeBack();

    [LibraryImport("js", EntryPoint = "cupri_set_can_back")]
    private static partial void SetCanGoBackCore(int can);

    /// <summary>
    /// Whether the visitor pressed Back. <b>Takes</b> rather than peeks, like a submitted link: one press is one
    /// step, however often this is polled.
    /// </summary>
    public static bool TakeBackRequest() => TakeBack() != 0;

    /// <summary>
    /// Tells the chrome whether there is anywhere to go back to.
    ///
    /// <para>Driven from here because the history lives here. A Back button that is enabled over an empty history
    /// is a control that lies about what it can do, and the visitor learns to distrust the chrome — which is the one
    /// part of this page they are supposed to be able to trust.</para>
    /// </summary>
    public static void SetCanGoBack(bool can) => SetCanGoBackCore(can ? 1 : 0);

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
