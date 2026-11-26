using Lagrange.Core;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;

namespace ShiroBot.LagrangeAdapter;

internal sealed class LagrangeProtocol
{
    private readonly Func<string, byte[], CancellationToken, Task<byte[]>> _send;
    internal LagrangeProtocol(Func<string, byte[], CancellationToken, Task<byte[]>> send) => _send = send;
    internal LagrangeProtocol(BotContext context) : this(async (command, body, token) =>
    {
        token.ThrowIfCancellationRequested();
        var result = await context.SendPacket(new BotSsoPacket(command, body)).AsTask().WaitAsync(token);
        if (result.RetCode != 0) throw new InvalidOperationException($"{command}: SSO {result.RetCode} {result.Extra}");
        return result.Data.ToArray();
    }) { }

    internal async Task<List<ProtoWire.Field>> Oidb(uint command, uint subcommand, ProtoWire body,
        CancellationToken token, bool reserved = false)
    {
        var envelope = new ProtoWire().UInt(1, command).UInt(2, subcommand).Bytes(4, body.ToArray());
        if (reserved) envelope.UInt(12, 1);
        var response = ProtoWire.Parse(await _send($"OidbSvcTrpcTcp.0x{command:x}_{subcommand}", envelope.ToArray(), token));
        RequireSuccess(response, 3, 5);
        if (response.Count == 0) throw new InvalidDataException("OIDB response envelope is missing.");
        return ProtoWire.Child(response, 4);
    }
    internal async Task<List<ProtoWire.Field>> Send(string command, ProtoWire body, CancellationToken token) =>
        ProtoWire.Parse(await _send(command, body.ToArray(), token));
    internal static void RequireSuccess(IEnumerable<ProtoWire.Field> fields, int code = 1, int message = 2)
    {
        var result = ProtoWire.Number(fields, code);
        if (result != 0) throw new InvalidOperationException($"Protocol error {result}: {ProtoWire.String(fields, message)}");
    }
    internal Task<List<ProtoWire.Field>> Like(string uid, int count, CancellationToken token) =>
        Oidb(0x7e5, 104, new ProtoWire().Text(11, uid).UInt(12, 71).UInt(13, checked((ulong)count)), token);

    internal static async Task<string> ResolveLikeTargetUid(BotContext context, long uin, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (uin == context.BotUin && !string.IsNullOrEmpty(context.Keystore.Uid)) return context.Keystore.Uid;
        if (context.GetCachedContactUid(uin) is { Length: > 0 } cachedUid) return cachedUid;

        // The stranger profile API does not provide a UID. Refresh the friend list on a cache miss.
        await context.FetchFriends(refresh: true).WaitAsync(token);
        return context.GetCachedContactUid(uin) is { Length: > 0 } uid
            ? uid
            : throw new InvalidOperationException($"无法解析 QQ {uin} 的 UID，请确认目标是好友或已缓存的群成员。");
    }

    internal Task<List<ProtoWire.Field>> DeleteFriend(string uid, CancellationToken token) =>
        Oidb(0x126b, 0, new ProtoWire().Message(1, new ProtoWire().Text(1, uid).Message(2,
            new ProtoWire().UInt(1, 130).UInt(2, 109).Message(3, new ProtoWire().UInt(1, 8).UInt(2, 8).UInt(3, 50)))), token);
    internal Task<List<ProtoWire.Field>> FriendRequest(string selfUid, string targetUid, bool accept, bool filtered, CancellationToken token) =>
        filtered ? Oidb(0xd72, 0, new ProtoWire().Text(1, selfUid).Text(2, targetUid), token) :
        Oidb(0xb5d, 44, new ProtoWire().UInt(1, accept ? 3ul : 5ul).Text(2, targetUid), token);
    internal Task<List<ProtoWire.Field>> SetAdmin(ulong group, string uid, bool enabled, CancellationToken token) =>
        Oidb(0x1096, 1, new ProtoWire().UInt(1, group).Text(2, uid).UInt(3, enabled ? 1ul : 0ul), token);
    internal Task<List<ProtoWire.Field>> Profile(ulong self, uint key, string value, CancellationToken token) =>
        Oidb(0x112a, 2, new ProtoWire().UInt(1, self).Message(2, new ProtoWire().UInt(1, key).Text(2, value)), token);
    internal Task<List<ProtoWire.Field>> Pins(CancellationToken token) => Oidb(0x12b3, 0, new ProtoWire(), token);
    internal Task<List<ProtoWire.Field>> Invitation(ulong group, ulong sequence, bool accept, CancellationToken token) =>
        Oidb(0x10c8, 1, new ProtoWire().UInt(1, accept ? 1ul : 2ul).Message(2,
            new ProtoWire().UInt(1, sequence).UInt(2, 2).UInt(3, group).Text(4, "")), token);
    internal async Task<List<string>> Faces(ulong self, CancellationToken token)
    {
        var result = await Send("Faceroam.OpReq", new ProtoWire().Message(1, new ProtoWire().UInt(1, 1))
            .UInt(2, self).UInt(3, 1).UInt(6, 1), token);
        RequireSuccess(result);
        var info = ProtoWire.Child(result, 4); var bid = ProtoWire.String(info, 3);
        return info.Where(f => f.Number == 1 && f.Wire == 2).Select(f =>
            $"https://p.qpic.cn/{Uri.EscapeDataString(bid)}/{self}/{Uri.EscapeDataString(System.Text.Encoding.UTF8.GetString(f.Bytes))}/0").ToList();
    }
    internal Task<List<ProtoWire.Field>> Read(ulong peer, string? uid, ulong sequence, ulong time, CancellationToken token) =>
        Send("trpc.msg.msg_svc.MsgService.SsoReadedReport", uid is null
            ? new ProtoWire().Message(1, new ProtoWire().UInt(1, peer).UInt(2, sequence))
            : new ProtoWire().Message(2, new ProtoWire().Text(2, uid).UInt(3, time).UInt(4, sequence)), token);
    internal async Task RenameFile(ulong group, string fileId, string parent, string name, CancellationToken token)
    {
        var result = await Oidb(0x6d6, 4, new ProtoWire().Message(5, new ProtoWire().UInt(1, group).UInt(2, 7)
            .UInt(3, 102).Text(4, fileId).Text(5, parent).Text(6, name)), token, true);
        RequireSuccess(ProtoWire.Child(result, 5));
    }
    internal async Task PersistFile(ulong group, string fileId, CancellationToken token)
    {
        var result = await Oidb(0x6d9, 0, new ProtoWire().Message(1,
            new ProtoWire().UInt(1, group).UInt(3, 102).Text(4, fileId)), token, true);
        RequireSuccess(ProtoWire.Child(result, 1));
    }

    internal static async Task<string> Uid(BotContext context, long uin, CancellationToken token)
    {
        if (uin == context.BotUin && !string.IsNullOrEmpty(context.Keystore.Uid)) return context.Keystore.Uid;
        var stranger = await context.FetchStranger(uin).WaitAsync(token);
        if (string.IsNullOrEmpty(stranger.Uid)) throw new InvalidOperationException("无法解析目标账号的 UID。");
        return stranger.Uid;
    }
    internal async Task<string> PrivateFile(string receiverUid, string fileId, string hash, CancellationToken token)
    {
        var result = await Oidb(0xe37, 1200, new ProtoWire().UInt(1, 1200).UInt(2, 1)
            .Message(14, new ProtoWire().Text(10, receiverUid).Text(20, fileId).UInt(30, 2).Text(60, hash))
            .UInt(101, 3).UInt(102, 103).UInt(200, 1).Bytes(99999, [0xc0, 0x85, 0x2c, 1]), token, true);
        var download = ProtoWire.Child(result, 14);
        var data = ProtoWire.Child(download, 30);
        var server = ProtoWire.String(data, 20); var port = ProtoWire.Number(data, 40); var path = ProtoWire.String(data, 50);
        if (server.Length == 0 || port is < 1 or > 65535 || !path.StartsWith('/'))
            throw new InvalidDataException("Private file response did not contain a download URL.");
        return new UriBuilder("http", server, (int)port) { Path = path.Split('?')[0], Query =
            (path.Contains('?') ? path[(path.IndexOf('?') + 1)..] + "&" : "") + "isthumb=0" }.Uri.AbsoluteUri;
    }
}
