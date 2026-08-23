using System.Collections;
using System.Text.Json.Nodes;
using CupriFace.Binding;

namespace CupriNet.Nodestar.Client;

/// <summary>
/// Binds a site's live feed payload to its markup — <c>{{ node.site }}</c>, <c>data-repeat="peers"</c> — without the
/// client knowing anything about the site's shape.
///
/// <para><b>Why a JSON wrapper rather than a typed model.</b> The client is generic: it renders whatever site it was
/// pointed at, and that site defines both its feed payload and the paths its markup binds. A concrete C# model would
/// mean the client shipping a copy of every site's schema, which is absurd. Wrapping the payload lets the site own
/// its own contract, and the client stays a renderer.</para>
///
/// <para><b>Why <see cref="IBindableAccessor"/> and not reflection.</b> CupriFace's binder falls back to reflection
/// for ordinary models, and its own source says that fallback is <i>trimmed away in a published AOT build</i> — at
/// which point binding silently stops resolving and every value renders empty. This client is published with
/// <c>TrimMode=full</c>, so the reflection path is not available to it. Implementing the accessor is the supported
/// AOT-safe route, and it happens to be exactly what a JSON-backed model wants anyway.</para>
/// </summary>
internal sealed class FeedModel(JsonObject payload) : IBindableAccessor
{
    /// <summary>Parses a feed message, or null if it is not a JSON object.</summary>
    public static FeedModel? Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(utf8)) is JsonObject o ? new FeedModel(o) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;   // a malformed feed message must not take the page down
        }
    }

    public object? GetBindable(string name) => Wrap(payload[name]);

    /// <summary>The feed is model → view only; a site cannot write back into it.</summary>
    public bool SetBindable(string name, object? value) => false;

    /// <summary>
    /// Presents JSON to the binder in the shapes it understands: nested objects as accessors of their own, arrays as
    /// enumerables (so <c>data-repeat</c> works), and leaves as plain values.
    /// </summary>
    private static object? Wrap(JsonNode? node) => node switch
    {
        JsonObject o => new FeedModel(o),
        JsonArray a => new FeedList(a),
        JsonValue v => v.GetValue<object>() is System.Text.Json.JsonElement e ? Unwrap(e) : v.ToString(),
        _ => null,
    };

    /// <summary>Numbers and booleans arrive as <c>JsonElement</c>; give the binder real values, not their JSON text.</summary>
    private static object? Unwrap(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => null,
        _ => element.ToString(),
    };

    /// <summary>A JSON array as something <c>data-repeat</c> can walk, each item bindable in its own right.</summary>
    private sealed class FeedList(JsonArray array) : IEnumerable
    {
        public IEnumerator GetEnumerator()
        {
            foreach (var item in array)
                yield return Wrap(item)!;
        }
    }
}
