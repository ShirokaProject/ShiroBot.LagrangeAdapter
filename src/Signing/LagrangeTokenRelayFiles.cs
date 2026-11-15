using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeTokenRelayFiles
{
    internal static string LoadOrCreateKey(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!File.Exists(path))
        {
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            try { WritePrivate(path, Encoding.UTF8.GetBytes(key + "\n")); }
            catch (IOException) when (File.Exists(path)) { /* Another creator won. */ }
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode)0x3f) != 0)
            throw new IOException("Token relay 密钥必须为独立文件，Unix 权限需设为 0600。");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 32 or > 512) throw new InvalidDataException("Token relay 密钥长度无效。");
        using var reader = new StreamReader(file, Encoding.UTF8);
        var value = reader.ReadToEnd().Trim();
        if (value.Length is < 32 or > 256 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("Token relay 密钥必须为 32–256 字符 URL-safe 串。");
        return value;
    }
    internal static void WriteDiscovery(string path, int port, string keyFile)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            WritePrivate(temporary, JsonSerializer.SerializeToUtf8Bytes(new {
                endpoint = $"http://127.0.0.1:{port}/debug/shiroSignToken", keyFile = Path.GetFullPath(keyFile)
            }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
    private static void WritePrivate(string path, byte[] content)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options);
        file.Write(content);
        file.Flush(flushToDisk: true);
    }
}
