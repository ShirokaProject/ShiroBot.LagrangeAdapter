using Lagrange.Core;
using Lagrange.Core.Common.Entity;

namespace ShiroBot.LagrangeAdapter;

internal static class GroupFiles
{
    internal sealed record CopiedFile(string Id, uint BusId);

    internal static Task<CopiedFile> CopyAsync(BotContext context, long sourceGroup, BotFileEntry source,
        long target, bool toFriend, CancellationToken token) =>
        CopyAsync(new LagrangeProtocol(context), sourceGroup, source.FileId, source.BusId, target, toFriend, token);

    internal static async Task<CopiedFile> CopyAsync(LagrangeProtocol protocol, long sourceGroup, string fileId,
        uint sourceBus, long target, bool toFriend, CancellationToken token)
    {
        if (sourceBus == 0) throw new InvalidDataException("群文件列表未返回文件业务类型。");
        uint targetBus = toFriend ? 3u : 102u;
        var body = new ProtoWire().UInt(1, checked((ulong)sourceGroup)).UInt(2, 1).UInt(3, sourceBus)
            .Text(4, fileId).UInt(5, targetBus).UInt(6, checked((ulong)target));
        var response = await protocol.Oidb(0x6d9, 2, new ProtoWire().Message(3, body), token);
        var result = ProtoWire.Child(response, 3);
        LagrangeProtocol.RequireSuccess(result);
        var id = ProtoWire.String(result, 4);
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("QQ 群文件复制未返回目标文件 ID。");
        var bus = checked((uint)ProtoWire.Number(result, 5));
        return new CopiedFile(id, bus == 0 ? targetBus : bus);
    }

    internal static async Task<string> DownloadUrlAsync(BotContext context, long group, BotFileEntry file,
        CancellationToken token)
    {
        var body = new ProtoWire().UInt(1, checked((ulong)group)).UInt(2, 7).UInt(3, file.BusId).Text(4, file.FileId);
        var response = await new LagrangeProtocol(context).Oidb(0x6d6, 2, new ProtoWire().Message(3, body), token);
        var result = ProtoWire.Child(response, 3);
        LagrangeProtocol.RequireSuccess(result);
        var host = ProtoWire.String(result, 13);
        if (string.IsNullOrEmpty(host)) host = ProtoWire.String(result, 5);
        var key = ProtoWire.Data(result, 6);
        if (string.IsNullOrEmpty(host) || key.Length == 0)
            throw new InvalidDataException("QQ 未返回群文件下载地址。");
        return $"https://{host}/ftn_handler/{Convert.ToHexString(key)}/?fname={Uri.EscapeDataString(file.FileName)}";
    }

    internal static async Task SendAsync(BotContext context, long group, CopiedFile file, CancellationToken token)
    {
        var payload = new ProtoWire().UInt(1, file.BusId).Text(2, file.Id).UInt(3, (uint)Random.Shared.Next()).UInt(5, 1);
        var feed = new ProtoWire().UInt(1, checked((ulong)group)).UInt(2, 2).Message(3, payload);
        var response = await new LagrangeProtocol(context).Oidb(0x6d9, 4, new ProtoWire().Message(5, feed), token);
        var result = ProtoWire.Child(response, 5);
        if (result.Count == 0) throw new InvalidDataException("QQ 未返回群文件消息发送结果。");
        LagrangeProtocol.RequireSuccess(result);
        foreach (var entry in result.Where(field => field is { Number: 4, Wire: 2 }))
            LagrangeProtocol.RequireSuccess(ProtoWire.Parse(entry.Bytes));
    }

    internal static async Task MoveAsync(BotContext context, long group, CopiedFile file, string folder, CancellationToken token)
    {
        var body = new ProtoWire().UInt(1, checked((ulong)group)).UInt(2, 7).UInt(3, file.BusId)
            .Text(4, file.Id).Text(5, "/").Text(6, folder);
        var response = await new LagrangeProtocol(context).Oidb(0x6d6, 5, new ProtoWire().Message(6, body), token);
        var result = ProtoWire.Child(response, 6);
        if (result.Count == 0) throw new InvalidDataException("QQ 未返回群文件移动结果。");
        LagrangeProtocol.RequireSuccess(result);
    }
}
