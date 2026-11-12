using System.Text.Json;
using Lagrange.Core.Common;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeStorage
{
    internal static bool EnsurePath(LagrangeAdapterConfig config, string configPath)
    {
        if (!string.IsNullOrWhiteSpace(config.KeystorePath)) return false;
        var relative = Path.Combine("sessions", Guid.NewGuid().ToString("N"), "keystore.json");
        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var legacy = Path.Combine(directory, "lagrange-keystore.json");
        // Only migrate a legacy shared file when the configured account identifies its owner.
        if (config.AccountUin > 0 && File.Exists(legacy))
        {
            var saved = JsonSerializer.Deserialize<BotKeystore>(File.ReadAllText(legacy));
            if (saved?.Uin == config.AccountUin)
            {
                var target = Path.Combine(directory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(legacy, target, false);
            }
        }
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

    internal static async Task SaveAsync(string path, BotKeystore keystore)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(keystore));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
