using ShiroBot.Model.QQ;

namespace ShiroBot.LagrangeAdapter;

internal sealed class LagrangeFlashProtocol(LagrangeProtocol protocol)
{
    private const uint FileType = 11;
    private int _requestId = Random.Shared.Next(1, int.MaxValue / 2);

    internal sealed record Entry(string Id, string Name, uint Size, string Sha1, string Md5, string Token, uint Type)
    {
        public QFlashFile ToModel() => new(Name, Size);
    }

    internal sealed record FileSet(QFlashFileSet Model, IReadOnlyList<Entry> Entries);
    internal sealed record UploadTicket(string UKey, string Token, ulong Time, ulong Ttl);

    internal async Task<string> ResolveIdAsync(string value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        value = value.Trim();
        if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            !url.Host.Equals("qfile.qq.com", StringComparison.OrdinalIgnoreCase) ||
            !url.AbsolutePath.StartsWith("/q/", StringComparison.Ordinal))
            throw new ArgumentException("闪传链接应为 qfile.qq.com/q/ 下的分享链接。", nameof(value));

        var code = url.AbsolutePath[3..].TrimEnd('/');
        if (code.Length is 0 or > 128 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("闪传链接缺少有效的分享码。", nameof(value));
        var response = await protocol.Oidb(0x93eb, 1, new ProtoWire().Text(1, code), token, reserved: true);
        return RequiredText(response, 1, "文件集 ID");
    }

    internal async Task<FileSet> GetAsync(string value, CancellationToken token)
    {
        var id = await ResolveIdAsync(value, token);
        var response = await protocol.Oidb(0x93d3, 1, new ProtoWire().Text(1, id).UInt(2, 7), token, reserved: true);
        var info = ProtoWire.Child(response, 1);
        if (info.Count == 0) throw new InvalidDataException("QQ 未返回闪传文件集详情。");
        var totalSize = checked((long)ProtoWire.Number(info, 5));
        var entries = await ListAsync(id, token);
        var listedSize = entries.Sum(file => (long)file.Size);
        if (listedSize < totalSize)
            throw new InvalidDataException("QQ 返回的闪传文件列表不完整。");

        var model = new QFlashFileSet
        {
            FileSetId = id,
            Title = ProtoWire.String(info, 2),
            ShareLink = ProtoWire.String(ProtoWire.Child(info, 8), 1),
            TotalSize = totalSize == 0 ? listedSize : totalSize,
            Files = [.. entries.Select(file => file.ToModel())]
        };
        return new FileSet(model, entries);
    }

    private async Task<List<Entry>> ListAsync(string id, CancellationToken token)
    {
        var files = new List<Entry>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenCookies = new HashSet<string>(StringComparer.Ordinal);
        byte[] cookie = [];
        do
        {
            var paging = new ProtoWire().Bytes(1, cookie).UInt(2, 1).UInt(3, 18).Bytes(4, [])
                .Message(5, new ProtoWire().UInt(1, 0)).Message(6, new ProtoWire().UInt(1, 0).UInt(2, 0));
            var req = new ProtoWire().Text(1, id).Message(2, paging).UInt(3, 2).UInt(4, 1);
            var response = await protocol.Oidb(0x93d4, 1, req, token, reserved: true);
            var page = ProtoWire.Child(response, 1);
            if (page.Count == 0 && response.All(field => field is not { Number: 1, Wire: 2 }))
                throw new InvalidDataException("QQ 未返回闪传文件列表。");
            foreach (var wireFile in page.Where(field => field is { Number: 3, Wire: 2 }))
            {
                var file = ReadEntry(ProtoWire.Parse(wireFile.Bytes));
                if (seenFiles.Add(file.Id)) files.Add(file);
            }

            cookie = ProtoWire.Data(page, 1);
            if (cookie.Length > 0 && !seenCookies.Add(Convert.ToBase64String(cookie)))
                throw new InvalidDataException("QQ 返回了重复的闪传分页游标。");
        } while (cookie.Length > 0);
        return files;
    }

    internal async Task<Entry> GetEntryAsync(string set, string id, CancellationToken token)
    {
        var req = new ProtoWire().Text(1, id).Text(2, set).UInt(3, 1);
        var response = await protocol.Oidb(0x93e5, 4, req, token, reserved: true);
        return ReadEntry(ProtoWire.Child(ProtoWire.Child(response, 1), 2));
    }

    private static Entry ReadEntry(List<ProtoWire.Field> fields)
    {
        var id = RequiredText(fields, 2, "文件 UUID");
        var name = RequiredText(fields, 8, "文件名");
        var size = checked((uint)ProtoWire.Number(fields, 11));
        var type = checked((uint)ProtoWire.Number(fields, 7));
        var download = ProtoWire.Child(fields, 13);
        return new Entry(id, name, size,
            Prefer(ProtoWire.String(fields, 20), ProtoWire.String(download, 3)),
            Prefer(ProtoWire.String(fields, 25), ProtoWire.String(download, 5)),
            Prefer(ProtoWire.String(ProtoWire.Child(fields, 14), 1), ProtoWire.String(download, 1)),
            type == 0 ? FileType : type);
    }

    internal async Task<QFlashShare> CreateAsync(string title, IReadOnlyList<Entry> entries, string uin,
        string uid, string nickname, bool reshare, CancellationToken token)
    {
        var total = checked((uint)entries.Sum(entry => (long)entry.Size));
        var uploader = new ProtoWire().Text(1, uin).Text(2, nickname).Text(3, uid).Bytes(4, []);
        var meta = new ProtoWire().Text(2, title).Text(3, title).UInt(4, 1).UInt(5, total)
            .Message(10, uploader).UInt(16, 1).UInt(20, 0).UInt(21, 0).UInt(23, 0)
            .Message(24, new ProtoWire().UInt(2, 0).Bytes(3, []));
        var req = new ProtoWire().UInt(1, (uint)entries.Count).Message(2, meta).UInt(3, reshare ? 21u : 20u);
        var response = await protocol.Oidb(0x93cf, 1, req, token, reserved: true);
        var expires = ProtoWire.Number(response, 4);
        var expiresAt = expires != 0
            ? DateTimeOffset.FromUnixTimeSeconds(checked((long)expires))
            : DateTimeOffset.UtcNow.AddSeconds(ProtoWire.Number(response, 5));
        return new QFlashShare(RequiredText(response, 1, "文件集 ID"), RequiredText(response, 3, "分享链接"), expiresAt);
    }

    internal Task RegisterAsync(string set, Entry file, CancellationToken token)
    {
        var entry = new ProtoWire().Text(1, set).Text(2, file.Id).UInt(3, 0).Bytes(4, [])
            .UInt(5, 1).UInt(6, 1).UInt(7, FileType).Text(8, file.Name).Text(9, file.Name)
            .UInt(10, 0).UInt(11, file.Size).UInt(12, 0).Text(20, file.Sha1).Bytes(24, []).Text(25, file.Md5);
        var req = new ProtoWire().UInt(1, 1).Text(2, set).Text(3, set).Message(4, entry).UInt(5, 1).UInt(6, 1);
        return protocol.Oidb(0x93d0, 1, req, token, reserved: true);
    }

    internal Task PrepareAsync(string set, CancellationToken token) =>
        protocol.Oidb(0x93db, 1, new ProtoWire().Text(1, set).Bytes(2, []), token, reserved: true);

    internal Task FinalizeAsync(string set, CancellationToken token) =>
        protocol.Oidb(0x93d1, 1, new ProtoWire().Text(1, set).UInt(2, 6), token, reserved: true);

    internal async Task<UploadTicket> PreflightAsync(Entry file, CancellationToken token)
    {
        var info = new ProtoWire().Message(1, FileInfo(file, download: false)).UInt(2, 0);
        var extra = new ProtoWire().Message(1, new ProtoWire().UInt(1, 0).Bytes(2, []));
        var upload = new ProtoWire().Message(1, info).UInt(2, 1).UInt(3, 0).UInt(4, 0).UInt(5, 0).Message(6, extra);
        var response = await protocol.Oidb(0x12a9, 100, MediaRequest(100).Message(2, upload), token);
        CheckMedia(response);
        var body = ProtoWire.Child(response, 2);
        var entry = ProtoWire.Child(ProtoWire.Child(ProtoWire.Child(body, 6), 1), 1);
        return new UploadTicket(ProtoWire.String(body, 1), ProtoWire.String(entry, 2),
            ProtoWire.Number(entry, 4), ProtoWire.Number(entry, 5));
    }

    internal async Task CommitAsync(string set, Entry file, UploadTicket ticket, CancellationToken token)
    {
        var summary = new ProtoWire().Message(1, FileInfo(file, download: false)).Text(2, ticket.Token)
            .UInt(3, 1).UInt(4, ticket.Time).UInt(5, ticket.Ttl).UInt(6, 0);
        var target = new ProtoWire().Text(1, set).Text(2, set).Text(3, file.Id).UInt(4, 1).UInt(5, 0).UInt(6, 0)
            .UInt(7, FileType).Bytes(8, []).UInt(9, 0).UInt(10, 0).UInt(11, 0).UInt(12, 0).UInt(13, 0).UInt(14, 0);
        var commit = new ProtoWire().Message(1, summary).Message(2, new ProtoWire().UInt(1, 2))
            .Message(3, new ProtoWire().UInt(1, 0).UInt(2, 0)).Message(10, target);
        var response = await protocol.Oidb(0x12a9, 103, MediaRequest(103).Message(12, commit), token);
        if (response.Count > 0) CheckMedia(response);
    }

    internal async Task<Uri> GetDownloadUrlAsync(string set, Entry file, CancellationToken token)
    {
        var info = new ProtoWire().Message(1, FileInfo(file, download: true)).Text(2, file.Token)
            .UInt(3, 0).UInt(4, 0).UInt(5, 0).UInt(6, 0);
        var caps = new ProtoWire().UInt(1, 0xfffffffe).UInt(3, 0xffffffff).UInt(5, 111)
            .Message(6, new ProtoWire().UInt(1, 3409274228).Bytes(2, []).Bytes(3, []).UInt(4, 0));
        var target = new ProtoWire().Text(1, set).Text(2, file.Id).UInt(3, file.Type).Text(4, file.Id);
        var client = new ProtoWire().Message(2, caps).Message(4, new ProtoWire().UInt(1, 0)).Message(10, target);
        var req = new ProtoWire().Message(1, info).Message(2, client).UInt(3, 0);
        var response = await protocol.Oidb(0x12a9, 200, MediaRequest(200).Message(3, req), token, reserved: true);
        CheckMedia(response);
        var body = ProtoWire.Child(response, 3);
        var url = ProtoWire.Child(body, 3);
        var host = RequiredText(url, 1, "下载主机");
        var path = RequiredText(url, 2, "下载路径");
        if (!path.StartsWith('/') ||
            !Uri.TryCreate($"https://{host}{path}{ProtoWire.String(body, 1)}", UriKind.Absolute, out var result) ||
            result.Port != 443 || result.UserInfo.Length != 0 || !result.IdnHost.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("QQ 返回了无效的闪传下载地址。");
        return result;
    }

    internal async Task SendCardAsync(string set, long peer, string? friendUid, CancellationToken token)
    {
        var target = friendUid is null
            ? new ProtoWire().UInt(1, 2).Message(3, new ProtoWire().UInt(1, checked((ulong)peer)))
            : new ProtoWire().UInt(1, 1).Message(2, new ProtoWire().Text(1, friendUid));
        var req = new ProtoWire().Message(1, target).Text(2, set);
        var response = await protocol.Oidb(0x93d7, 1, req, token, reserved: true);
        var status = ProtoWire.Child(response, 1);
        LagrangeProtocol.RequireSuccess(status);
        if (ProtoWire.Child(status, 3).Count == 0)
            throw new InvalidDataException("QQ 未返回闪传卡片的目标发送状态。");
    }

    private ProtoWire MediaRequest(uint command) => new ProtoWire().Message(1, new ProtoWire()
        .Message(1, new ProtoWire().UInt(1, (uint)Interlocked.Increment(ref _requestId)).UInt(2, command))
        .Message(2, new ProtoWire().UInt(101, 2).UInt(102, 4).UInt(103, 22).UInt(200, 5))
        .Message(3, new ProtoWire().UInt(1, 1)));

    private static ProtoWire FileInfo(Entry file, bool download)
    {
        // This endpoint shares the media FileInfo schema. QQ expects SHA-1 as hexadecimal UTF-8.
        var info = new ProtoWire().UInt(1, file.Size).Bytes(2, []).Text(3, file.Sha1).Text(4, file.Name);
        return info.Message(5, new ProtoWire().UInt(1, 0).UInt(2, 0).UInt(3, 0).UInt(4, 0))
            .UInt(6, 0).UInt(7, 0).UInt(8, 0).UInt(9, download ? 0u : 1u);
    }

    internal static byte[] DecodeHash(string text, int size)
    {
        if (text.Length != size * 2) return [];
        try { return Convert.FromHexString(text); }
        catch (FormatException) { return []; }
    }

    private static void CheckMedia(List<ProtoWire.Field> response)
    {
        var head = ProtoWire.Child(response, 1);
        if (head.Count == 0) throw new InvalidDataException("QQ 未返回闪传媒体响应头。");
        LagrangeProtocol.RequireSuccess(head, 3, 4);
        var code = ProtoWire.String(head, 3);
        if (code.Length > 0 && code != "0" && !code.Equals("success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"QQ 闪传媒体错误 {code}。");
    }

    private static string Prefer(string value, string fallback) => value.Length == 0 ? fallback : value;

    private static string RequiredText(List<ProtoWire.Field> fields, int tag, string name)
    {
        var value = ProtoWire.String(fields, tag);
        return value.Length == 0 ? throw new InvalidDataException($"QQ 未返回闪传{name}。") : value;
    }
}
