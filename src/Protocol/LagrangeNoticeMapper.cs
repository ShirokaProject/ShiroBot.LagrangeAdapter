using System.Buffers.Binary;
using Lagrange.Core.Events.EventArgs;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeNoticeMapper
{
    internal static async Task<PlatformEvent?> Map(BotExtendedNoticeEvent notice, string self, Func<string, Task<string>> resolve)
    {
        var fields = notice.Type == 732 && notice.SubType is 16 or 21 ? [] : ProtoWire.Parse(notice.Content);
        var now = DateTimeOffset.UtcNow;
        string kind; object raw; ulong group = 0;
        if (notice.Type == 44)
        {
            group = ProtoWire.Number(fields, 1); var body = ProtoWire.Child(fields, 4);
            var enabled = body.Any(f => f.Number == 2 && f.Wire == 2);
            var uid = ProtoWire.String(ProtoWire.Child(body, enabled ? 2 : 1), 1);
            if (uid.Length == 0) return null;
            kind = QEventKinds.GroupAdminChange;
            raw = new QGroupAdminChange { GroupId = group.ToString(), UserId = await resolve(uid), IsSet = enabled, Time = now, SelfId = self };
        }
        else if (notice.Type == 34 && ProtoWire.Number(fields, 4) == 129)
        {
            group = ProtoWire.Number(fields, 1); kind = QEventKinds.GroupDisband;
            var uid = ProtoWire.String(ProtoWire.Child(ProtoWire.Child(fields, 5), 1), 1);
            raw = new QGroupDisband { GroupId = group.ToString(), OperatorId = uid.Length == 0 ? null : await resolve(uid), Time = now, SelfId = self };
        }
        else if (notice.Type == 528 && notice.SubType == 39)
        {
            var body = ProtoWire.Child(ProtoWire.Child(ProtoWire.Child(fields, 1), 20), 1);
            if (body.Count == 0) return null;
            group = ProtoWire.Number(body, 2);
            var uid = ProtoWire.String(body, 1);
            if (group == 0 && uid.Length == 0) return null;
            var peer = group == 0 ? await resolve(uid) : group.ToString();
            kind = QEventKinds.PeerPinChange;
            raw = new QPeerPinChange { Scene = group == 0 ? QMessageScene.Friend : QMessageScene.Group,
                PeerId = peer, IsPinned = ProtoWire.Data(ProtoWire.Child(body, 400), 2).Length > 0, Time = now, SelfId = self };
            return new PlatformEvent { Platform = LagrangeMapper.PlatformId, SelfId = self, Kind = kind, Raw = raw,
                Channel = group == 0 ? Channel.Direct(peer) : new Channel(peer, ChannelType.Group) };
        }
        else if (notice.Type == 528 && notice.SubType == 290)
        {
            if (ProtoWire.Number(fields, 1) != 12) return null;
            var args = fields.Where(f => f.Number == 7 && f.Wire == 2).Select(f => ProtoWire.Parse(f.Bytes))
                .ToDictionary(f => ProtoWire.String(f, 1), f => ProtoWire.String(f, 2));
            kind = QEventKinds.FriendNudge;
            raw = new QFriendNudge { UserId = notice.Sender.ToString(), IsSelfSend = args.GetValueOrDefault("uin_str1") == self,
                IsSelfReceive = args.GetValueOrDefault("uin_str2") == self, DisplayAction = args.GetValueOrDefault("action_str") ?? args.GetValueOrDefault("alt_str1", ""),
                DisplaySuffix = args.GetValueOrDefault("suffix_str", ""), DisplayActionImgUrl = args.GetValueOrDefault("action_img_url", ""), Time = now, SelfId = self };
        }
        else if (notice.Type == 732 && notice.SubType == 12)
        {
            group = ProtoWire.Number(fields, 1); var op = ProtoWire.String(fields, 4);
            var state = ProtoWire.Child(ProtoWire.Child(fields, 5), 3); if (state.Count == 0) return null;
            var uid = ProtoWire.String(state, 1); var seconds = ProtoWire.Number(state, 2);
            var operatorId = op.Length == 0 ? null : await resolve(op);
            if (uid.Length == 0)
            {
                kind = QEventKinds.GroupWholeMute;
                raw = new QGroupWholeMute { GroupId = group.ToString(), OperatorId = operatorId, IsMute = seconds != 0, Time = now, SelfId = self };
            }
            else
            {
                kind = QEventKinds.GroupMute;
                raw = new QGroupMute { GroupId = group.ToString(), UserId = await resolve(uid), OperatorId = operatorId,
                    Duration = TimeSpan.FromSeconds(seconds), Time = now, SelfId = self };
            }
        }
        else if (notice.Type == 732 && notice.SubType is 16 or 21)
        {
            var content = notice.Content.AsSpan(); if (content.Length < 7) return null;
            group = BinaryPrimitives.ReadUInt32BigEndian(content);
            var length = BinaryPrimitives.ReadUInt16BigEndian(content[5..]);
            if (length > content.Length - 7) throw new InvalidDataException("Truncated group notice.");
            var body = ProtoWire.Parse(content.Slice(7, length));
            if (notice.SubType == 16 && ProtoWire.Number(body, 13) == 12)
            {
                var name = ProtoWire.String(ProtoWire.Child(body, 5), 2); if (name.Length == 0) return null;
                var op = ProtoWire.String(body, 21); group = ProtoWire.Number(body, 4) is > 0 and var actual ? actual : group;
                kind = QEventKinds.GroupNameChange;
                raw = new QGroupNameChange { GroupId = group.ToString(), NewGroupName = name, OperatorId = op.Length == 0 ? null : await resolve(op), Time = now, SelfId = self };
            }
            else if (notice.SubType == 21)
            {
                var data = ProtoWire.Child(body, 33); if (data.Count == 0) return null;
                group = ProtoWire.Number(data, 1); kind = QEventKinds.GroupEssenceMessageChange;
                raw = new QGroupEssenceMessageChange { GroupId = group.ToString(), MessageId = ProtoWire.Number(data, 2).ToString(),
                    OperatorId = ProtoWire.Number(data, 6).ToString(), IsSet = ProtoWire.Number(data, 4) == 1, Time = now, SelfId = self };
            }
            else return null;
        }
        else return null;
        return new PlatformEvent { Platform = LagrangeMapper.PlatformId, SelfId = self, Kind = kind, Raw = raw,
            Channel = group == 0 ? Channel.Direct(notice.Sender.ToString()) : new Channel(group.ToString(), ChannelType.Group) };
    }
}
