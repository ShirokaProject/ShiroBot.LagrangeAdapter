using System.Text.Json;
using System.Web;
using ShiroBot.Model.QQ;

namespace ShiroBot.LagrangeAdapter;

public static partial class LagrangeMapper
{
    internal static QIncomingFlashFile? TryReadFlashCard(string markdown)
    {
        const string prefix = "[闪传](";
        if (!markdown.StartsWith(prefix, StringComparison.Ordinal) || !markdown.EndsWith(')') ||
            !Uri.TryCreate(markdown[prefix.Length..^1], UriKind.Absolute, out var url) || url.Scheme != "mqqapi")
            return null;

        var json = HttpUtility.ParseQueryString(url.Query)["json"];
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (ReadText(root, "busId") != "FlashTransfer") return null;
            var file = FindView(GetChild(root, "attributes"), "file");
            if (!Uri.TryCreate(ReadText(file, "schema"), UriKind.Absolute, out var schema)) return null;
            var query = HttpUtility.ParseQueryString(schema.Query);
            if (query["fileset_id"] is not { Length: > 0 } id) return null;
            var title = ReadText(FindView(file, "title"), "text") ?? string.Empty;
            _ = int.TryParse(query["scene_type"], out var scene);
            return new QIncomingFlashFile(id, title) { SceneType = scene };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement FindView(JsonElement parent, string viewId)
    {
        var views = GetChild(parent, "attributes");
        if (views.ValueKind == JsonValueKind.Array)
            foreach (var view in views.EnumerateArray())
                if (ReadText(view, "viewId") == viewId) return view;
        return default;
    }

    private static JsonElement GetChild(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var child) ? child : default;

    private static string? ReadText(JsonElement parent, string key)
    {
        var child = GetChild(parent, key);
        return child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    }
}
