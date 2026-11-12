using System.Text.Json;
using Lagrange.Core.Common;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeStorage
{
    internal static bool EnsurePath(LagrangeAdapterConfig config, string configPath)
    {
        if (!string.IsNullOrWhiteSpace(config.KeystorePath)) return false;
        var relative = Path.Combine("sessions", Guid.NewGuid().ToString("N"), "keystore.json");
        config.KeystorePath = relative;
        return true;
    }

    internal static FileStream Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Host shadow-copies package directories when another instance starts. Keep the lease
        // outside that tree so a running instance does not block its sibling's shadow copy.
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        var lockRoot = Path.Combine(Path.GetTempPath(), "ShiroBot", "lagrange-session-locks");
        Directory.CreateDirectory(lockRoot);
        try { return new FileStream(Path.Combine(lockRoot, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("登录凭据路径已被其他实例使用，请为每个实例设置独立的凭据路径。", ex); }
    }

    internal static BotKeystore? ReadSession(ShiroBot.SDK.Config.IConfigContext context, LagrangeAdapterConfig config)
    {
        var values = context.Load<Dictionary<string, object>>();
        if (values.TryGetValue("session_store", out var stored))
        {
            if (stored is not string json) throw new InvalidDataException("session_store 必须是 JSON 字符串。");
            return string.IsNullOrWhiteSpace(json) ? null
                : JsonSerializer.Deserialize<BotKeystore>(json) ?? throw new InvalidDataException("session_store 内容为空。");
        }
        var root = Path.GetDirectoryName(Path.GetFullPath(context.ConfigPath))!;
        var legacyPath = string.IsNullOrWhiteSpace(config.KeystorePath)
            ? Path.Combine(root, "lagrange-keystore.json") : Path.GetFullPath(config.KeystorePath, root);
        BotKeystore? legacy = null;
        if (File.Exists(legacyPath))
        {
            var saved = JsonSerializer.Deserialize<BotKeystore>(File.ReadAllText(legacyPath));
            if (saved is not null && (!string.IsNullOrWhiteSpace(config.KeystorePath) || config.AccountUin > 0 && saved.Uin == config.AccountUin))
                legacy = saved;
        }
        // An explicit empty value prevents a later deleted session being reimported from a legacy file.
        context.SetValue("session_store", legacy is null ? "" : JsonSerializer.Serialize(legacy));
        return legacy;
    }

    internal static void SaveSession(ShiroBot.SDK.Config.IConfigContext context, BotKeystore keystore) =>
        context.SetValue("session_store", JsonSerializer.Serialize(keystore));
}
