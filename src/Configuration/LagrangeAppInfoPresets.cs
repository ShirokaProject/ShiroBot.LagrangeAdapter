using System.Text.Json.Nodes;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeAppInfoPresets
{
    internal static string? Protocol(string preset) => preset switch
    {
        "Mac 7.0.3-54395" => "MacOs",
        "AndroidPad 9.3.80-16750" => "AndroidPad",
        "自定义" => null,
        _ => throw new ArgumentException("未知 AppInfo 预设。")
    };
    internal static JsonObject? Get(string preset)
    {
        var file = preset switch {
            "Mac 7.0.3-54395" => "mac-7.0.3-54395",
            "AndroidPad 9.3.80-16750" => "android-pad-9.3.80-16750",
            "自定义" => null,
            _ => throw new ArgumentException("未知 AppInfo 预设。")
        };
        if (file is null) return null;
        using var stream = typeof(LagrangeAppInfoPresets).Assembly.GetManifestResourceStream(
            $"ShiroBot.LagrangeAdapter.Configuration.Presets.{file}.json")
            ?? throw new InvalidDataException("内置 AppInfo 缺失。");
        return JsonNode.Parse(stream)!.AsObject();
    }
}
