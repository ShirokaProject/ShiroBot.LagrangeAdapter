using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace ShiroBot.LagrangeAdapter;

/// <summary>
/// Lagrange.Core 模型 ↔ ShiroBot 核心模型 + 通用 QQ 模型的映射。
/// 与 Milky/OneBot 适配器保持同构:Raw 携带 Qq.Model 类型,PlatformEvent Kind 复用 QEventKinds。
/// </summary>
public static class LagrangeMapper
{
    private static readonly HttpClient ResourceClient = new();
    public const string PlatformId = "qq";


    // ─── 实体 ───

    public static QFriend ToQq(BotFriend friend) => new()
    {
        UserId = friend.Uin.ToString(),
        Nickname = friend.Nickname,
        Sex = ToQqSex(friend.Gender),
        Qid = string.IsNullOrEmpty(friend.Qid) ? null : friend.Qid,
        Remark = string.IsNullOrEmpty(friend.Remarks) ? null : friend.Remarks,
        Category = new QFriendCategory(friend.Category.Id, friend.Category.Name)
    };

    public static QGroup ToQq(BotGroup group) => new()
    {
        GroupId = group.GroupUin.ToString(),
        GroupName = group.GroupName,
        MemberCount = group.MemberCount,
        MaxMemberCount = group.MaxMember,
        CreatedTime = group.CreateTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(group.CreateTime) : null,
        Description = group.Description,
        Announcement = group.Announcement
    };

    public static QGroupMember ToQq(BotGroupMember member) => new()
    {
        UserId = member.Uin.ToString(),
        Nickname = member.Nickname,
        GroupId = member.Group.GroupUin.ToString(),
        Card = string.IsNullOrEmpty(member.MemberCard) ? null : member.MemberCard,
        Title = string.IsNullOrEmpty(member.SpecialTitle) ? null : member.SpecialTitle,
        Level = member.GroupLevel,
        Role = member.Permission switch
        {
            GroupMemberPermission.Owner => QGroupRole.Owner,
            GroupMemberPermission.Admin => QGroupRole.Admin,
            _ => QGroupRole.Member
        },
        JoinTime = ToOffset(member.JoinTime),
        LastSentTime = ToOffset(member.LastMsgTime),
        ShutUpEndTime = member.ShutUpTimestamp > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            ? ToOffset(member.ShutUpTimestamp)
            : null
    };

    private static QSex ToQqSex(BotGender gender) => gender switch
    {
        BotGender.Male => QSex.Male,
        BotGender.Female => QSex.Female,
        _ => QSex.Unknown
    };

    public static QGroupFile ToQq(BotFileEntry file, string groupId) => new()
    {
        GroupId = groupId,
        FileId = file.FileId,
        FileName = file.FileName,
        ParentFolderId = file.ParentDirectory,
        FileSize = checked((long)file.FileSize),
        UploadedTime = ToOffset(file.UploadedTime),
        ExpireTime = ToOffset(file.ExpireTime),
        UploaderId = file.UploaderUin.ToString(),
        DownloadedTimes = checked((int)file.DownloadedTimes)
    };

    public static QGroupFolder ToQq(BotFolderEntry folder, string groupId) => new()
    {
        GroupId = groupId,
        FolderId = folder.FolderId,
        FolderName = folder.FolderName,
        ParentFolderId = folder.ParentFolderId,
        CreatedTime = ToOffset(folder.CreateTime),
        LastModifiedTime = ToOffset(folder.ModifiedTime),
        CreatorId = folder.CreatorUin.ToString(),
        FileCount = checked((int)folder.TotalFileCount)
    };

    internal static QGroupJoinRequest? ToJoinRequest(BotGroupNotificationBase notification) => notification switch
    {
        BotGroupJoinNotification join => new QGroupJoinRequest
        {
            GroupId = join.GroupUin.ToString(),
            UserId = join.TargetUin.ToString(),
            RequestId = join.Sequence.ToString(),
            Comment = join.Comment,
            IsFiltered = join.IsFiltered,
            State = ToRequestState(join.State)
        },
        BotGroupInviteNotification invite => new QGroupJoinRequest
        {
            GroupId = invite.GroupUin.ToString(),
            UserId = invite.TargetUin.ToString(),
            RequestId = invite.Sequence.ToString(),
            InviterId = invite.InviterUin.ToString(),
            IsInvited = true,
            IsFiltered = invite.IsFiltered,
            State = ToRequestState(invite.State)
        },
        _ => null
    };

    private static QRequestState ToRequestState(BotGroupNotificationState state) => state switch
    {
        BotGroupNotificationState.Accept => QRequestState.Accepted,
        BotGroupNotificationState.Reject => QRequestState.Rejected,
        _ => QRequestState.Pending
    };

    private static DateTimeOffset? ToOffset(long time) =>
        time == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(time);

    // ─── 入站消息 ───

    public static MessageEvent ToMessageEvent(BotMessage message, string? selfId = null)
    {
        var directPeer = message.Contact.Uin.ToString() == selfId ? message.Receiver : message.Contact;
        (Channel Channel, User Sender, Member? Member, QIncomingMessage Message) mapped = message.Contact switch
        {
            BotGroupMember groupMember => (
                new Channel(groupMember.Group.GroupUin.ToString(), ChannelType.Group)
                {
                    Name = groupMember.Group.GroupName,
                    GuildId = groupMember.Group.GroupUin.ToString()
                },
                new User(groupMember.Uin.ToString()) { Name = groupMember.Nickname },
                new Member(new User(groupMember.Uin.ToString()) { Name = groupMember.Nickname })
                {
                    Nick = string.IsNullOrEmpty(groupMember.MemberCard) ? null : groupMember.MemberCard,
                    Role = groupMember.Permission switch
                    {
                        GroupMemberPermission.Owner => MemberRole.Owner,
                        GroupMemberPermission.Admin => MemberRole.Admin,
                        _ => MemberRole.Member
                    },
                    JoinedAt = ToOffset(groupMember.JoinTime)
                },
                new QGroupMessage
                {
                    PeerId = groupMember.Group.GroupUin.ToString(),
                    MessageId = ((long)message.Sequence).ToString(),
                    SenderId = groupMember.Uin.ToString(),
                    Time = DateTimeOffset.FromUnixTimeSeconds(message.Time),
                    Segments = ToQqSegments(message.Entities),
                    Group = ToQq(groupMember.Group),
                    GroupMember = ToQq(groupMember)
                }),
            BotFriend friend => (
                Channel.Direct(directPeer.Uin.ToString()),
                new User(friend.Uin.ToString()) { Name = friend.Nickname },
                null,
                new QFriendMessage
                {
                    PeerId = directPeer.Uin.ToString(),
                    MessageId = ((long)message.Sequence).ToString(),
                    SenderId = friend.Uin.ToString(),
                    Time = DateTimeOffset.FromUnixTimeSeconds(message.Time),
                    Segments = ToQqSegments(message.Entities),
                    Friend = ToQq(directPeer as BotFriend ?? friend)
                }),
            _ => (
                new Channel(message.Contact.Uin.ToString(), ChannelType.Other),
                new User(message.Contact.Uin.ToString()) { Name = message.Contact.Nickname },
                null,
                new QTempMessage
                {
                    PeerId = message.Contact.Uin.ToString(),
                    MessageId = ((long)message.Sequence).ToString(),
                    SenderId = message.Contact.Uin.ToString(),
                    Time = DateTimeOffset.FromUnixTimeSeconds(message.Time),
                    Segments = ToQqSegments(message.Entities)
                })
        };

        var (channel, sender, member, qqMessage) = mapped;
        return new MessageEvent
        {
            Platform = PlatformId,
            SelfId = selfId,
            Raw = qqMessage,
            MessageId = message.Sequence.ToString(),
            Channel = channel,
            Sender = sender,
            Member = member,
            Segments = ToSegments(message.Entities),
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(message.Time)
        };
    }

    // ─── 段:Lagrange → 核心 ───

    public static IReadOnlyList<MessageSegment> ToSegments(MessageChain chain) =>
        [.. chain.Select(ToSegment)];

    private static MessageSegment ToSegment(IMessageEntity entity) => entity switch
    {
        FaceEntity face => new RawSegment(PlatformId, "face", new QIncomingFace(face.FaceId.ToString(), face.IsLarge)),
        TextEntity text => new TextSegment(text.Text),
        MentionEntity { Uin: 0 } => new MentionAllSegment(),
        MentionEntity mention => new MentionSegment(mention.Uin.ToString()) { DisplayName = mention.Display },
        ReplyEntity reply => new QuoteSegment(reply.SrcSequence.ToString()),
        ImageEntity image => new ImageSegment(image.FileUrl)
        {
            ResourceId = image.FileUuid,
            Width = (int)image.ImageSize.X,
            Height = (int)image.ImageSize.Y,
            Summary = image.Summary
        },
        RecordEntity record => new AudioSegment(record.FileUrl)
        {
            ResourceId = record.FileUuid,
            Duration = TimeSpan.FromSeconds(record.RecordLength)
        },
        VideoEntity video => new VideoSegment(video.FileUrl)
        {
            ResourceId = video.FileUuid
        },
        GroupFileEntity file => new FileSegment(file.FileUrl)
        {
            ResourceId = file.FileId,
            FileName = file.FileName,
            FileSize = file.FileSize
        },
        MultiMsgEntity multi => new RawSegment(PlatformId, "forward",
            new QIncomingForward(multi.ResId ?? string.Empty)),
        LightAppEntity lightApp => new RawSegment(PlatformId, "light_app",
            new QIncomingLightApp(lightApp.AppName, lightApp.Payload)),
        _ => new RawSegment(PlatformId, entity.GetType().Name, entity)
    };

    // ─── 段:Lagrange → Qq.Model(用于 Raw) ───

    public static IReadOnlyList<QIncomingSegment> ToQqSegments(MessageChain chain) =>
        [.. chain.Select(ToQqSegment)];

    private static QIncomingSegment ToQqSegment(IMessageEntity entity) => entity switch
    {
        FaceEntity face => new QIncomingFace(face.FaceId.ToString(), face.IsLarge),
        MarketFaceEntity face => new QIncomingMarketFace(face.EmojiId, face.Url)
            { EmojiPackageId = face.EmojiPackageId, Key = face.Key, Summary = face.Summary },
        TextEntity text => new QIncomingText(text.Text),
        MentionEntity { Uin: 0 } => new QIncomingMentionAll(),
        MentionEntity mention => new QIncomingMention(mention.Uin.ToString(), mention.Display ?? string.Empty),
        ReplyEntity reply => new QIncomingReply(reply.SrcSequence.ToString()),
        ImageEntity image => new QIncomingImage(image.FileUuid, image.FileUrl)
        {
            Width = (int)image.ImageSize.X,
            Height = (int)image.ImageSize.Y,
            Summary = image.Summary
        },
        RecordEntity record => new QIncomingRecord(record.FileUuid, record.FileUrl)
        {
            Duration = TimeSpan.FromSeconds(record.RecordLength)
        },
        VideoEntity video => new QIncomingVideo(video.FileUuid, video.FileUrl),
        GroupFileEntity file => new QIncomingFile(file.FileId, file.FileName, file.FileSize),
        MultiMsgEntity multi => new QIncomingForward(multi.ResId ?? string.Empty),
        LightAppEntity lightApp => new QIncomingLightApp(lightApp.AppName, lightApp.Payload),
        _ => new QIncomingText(string.Empty)
    };

    // ─── 段:核心/Qq → Lagrange(出站) ───

    public static async Task<MessageChain> ToChainAsync(
        IReadOnlyList<MessageSegment> segments,
        Func<string, Task<BotMessage?>> resolveQuoted)
    {
        var chain = new MessageChain();

        foreach (var segment in segments)
        {
            switch (segment)
            {
                case TextSegment text:
                    chain.Add(new TextEntity(text.Text));
                    break;
                case MentionAllSegment:
                    chain.Add(new MentionEntity(0, "@全体成员"));
                    break;
                case MentionSegment mention:
                    chain.Add(new MentionEntity(
                        long.TryParse(mention.UserId, out var uin) ? uin : 0,
                        mention.DisplayName));
                    break;
                case QuoteSegment quote:
                    var quoted = await resolveQuoted(quote.MessageId).ConfigureAwait(false);
                    if (quoted is not null)
                    {
                        chain.Add(new ReplyEntity(quoted));
                    }

                    break;
                case EmojiSegment:
                    // 上游 FaceEntity.Build 尚未实现，当前仅支持入站表情。
                    throw Unsupported("QQ 表情段(FaceEntity)");
                case ImageSegment image:
                    chain.Add(new ImageEntity(await OpenResourceAsync(image.Uri).ConfigureAwait(false),
                        image.Summary ?? "[图片]", disposeOnCompletion: true));
                    break;
                case AudioSegment audio:
                    chain.Add(new RecordEntity(await OpenResourceAsync(audio.Uri).ConfigureAwait(false),
                        disposeOnCompletion: true));
                    break;
                case VideoSegment video:
                    chain.Add(new VideoEntity(
                        await OpenResourceAsync(video.Uri).ConfigureAwait(false),
                        video.ThumbnailUri is null
                            ? null
                            : await OpenResourceAsync(video.ThumbnailUri).ConfigureAwait(false),
                        disposeOnCompletion: true));
                    break;
                case RawSegment { Payload: QOutgoingSegment qqOutgoing }:
                    await AppendQqSegmentAsync(chain, qqOutgoing, resolveQuoted).ConfigureAwait(false);
                    break;
                case RawSegment { Payload: IMessageEntity lagrangeEntity }:
                    chain.Add(lagrangeEntity);
                    break;
                case FileSegment:
                    throw Unsupported("消息内文件段(请使用 IQFileApi.UploadGroupFileAsync/UploadPrivateFileAsync)");
                default:
                    throw Unsupported(segment.GetType().Name);
            }
        }

        return chain;
    }

    public static async Task AppendQqSegmentAsync(
        MessageChain chain,
        QOutgoingSegment segment,
        Func<string, Task<BotMessage?>> resolveQuoted)
    {
        switch (segment)
        {
            case QOutgoingText text:
                chain.Add(new TextEntity(text.Text));
                break;
            case QOutgoingMentionAll:
                chain.Add(new MentionEntity(0, "@全体成员"));
                break;
            case QOutgoingMention mention:
                chain.Add(new MentionEntity(ParseId(mention.UserId, "UserId"), null));
                break;
            case QOutgoingReply reply:
                var quoted = await resolveQuoted(reply.MessageId).ConfigureAwait(false);
                if (quoted is not null)
                {
                    chain.Add(new ReplyEntity(quoted));
                }

                break;
            case QOutgoingImage image:
                chain.Add(new ImageEntity(await OpenResourceAsync(image.Uri).ConfigureAwait(false),
                    image.Summary ?? "[图片]", disposeOnCompletion: true));
                break;
            case QOutgoingRecord record:
                chain.Add(new RecordEntity(await OpenResourceAsync(record.Uri).ConfigureAwait(false),
                    disposeOnCompletion: true));
                break;
            case QOutgoingVideo video:
                chain.Add(new VideoEntity(
                    await OpenResourceAsync(video.Uri).ConfigureAwait(false),
                    video.ThumbUri is null ? null : await OpenResourceAsync(video.ThumbUri).ConfigureAwait(false),
                    disposeOnCompletion: true));
                break;
            case QOutgoingLightApp lightApp:
                chain.Add(new LightAppEntity(lightApp.JsonPayload));
                break;
            case QOutgoingFace:
                throw Unsupported("QQ 表情段(FaceEntity)");
            case QOutgoingForward forward:
                chain.Add(await ToMultiMsgAsync(forward, resolveQuoted).ConfigureAwait(false));
                break;
            default:
                throw Unsupported(segment.GetType().Name);
        }
    }

    /// <summary>合并转发 → MultiMsgEntity(伪造消息)。</summary>
    public static async Task<MultiMsgEntity> ToMultiMsgAsync(
        QOutgoingForward forward,
        Func<string, Task<BotMessage?>> resolveQuoted)
    {
        var messages = new List<BotMessage>();
        foreach (var message in forward.Messages)
        {
            var chain = new MessageChain();
            foreach (var segment in message.Segments)
            {
                await AppendQqSegmentAsync(chain, segment, resolveQuoted).ConfigureAwait(false);
            }

            messages.Add(BotMessage.CreateCustomGroup(
                0,
                ParseId(message.UserId, "UserId"),
                message.SenderName,
                (message.Time ?? DateTimeOffset.Now).ToUnixTimeSeconds(),
                chain));
        }

        return new MultiMsgEntity(messages);
    }

    /// <summary>解析 http(s)://、file://、base64:// 三种资源形式为 Stream。</summary>
    public static async Task<Stream> OpenResourceAsync(string uri)
    {
        if (uri.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            return new MemoryStream(Convert.FromBase64String(uri["base64://".Length..]));
        }

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return File.OpenRead(new Uri(uri).LocalPath);
        }

        if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return File.OpenRead(uri);
        {
            var bytes = await ResourceClient.GetByteArrayAsync(uri).ConfigureAwait(false);
            return new MemoryStream(bytes);
        }
    }

    // ─── 工具 ───

    public static long ParseId(string id, string what)
    {
        return long.TryParse(id, out var value)
            ? value
            : throw new ArgumentException($"QQ(Lagrange) 平台的 {what} 必须是数字,实际为 '{id}'。");
    }

    public static NotSupportedException Unsupported(string what) =>
        new($"Lagrange 适配器暂不兼容: {what}。");
}