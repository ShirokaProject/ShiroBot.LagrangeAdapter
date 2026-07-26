using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.LagrangeAdapter;

/// <summary>
/// Lagrange.Core 模型 ↔ ShiroBot 核心模型 + 通用 QQ 模型的映射。
/// 与 Milky/OneBot 适配器保持同构:Raw 携带 Qq.Model 类型,PlatformEvent Kind 复用 QqEventKinds。
/// </summary>
public static class LagrangeMapper
{
    public const string PlatformId = "qq";

    internal static string? SelfId { get; set; }

    // ─── 实体 ───

    public static QqFriend ToQq(BotFriend friend) => new()
    {
        UserId = friend.Uin,
        Nickname = friend.Nickname,
        Sex = ToQqSex(friend.Gender),
        Qid = string.IsNullOrEmpty(friend.Qid) ? null : friend.Qid,
        Remark = string.IsNullOrEmpty(friend.Remarks) ? null : friend.Remarks,
        Category = new QqFriendCategory(friend.Category.Id, friend.Category.Name)
    };

    public static QqGroup ToQq(BotGroup group) => new()
    {
        GroupId = group.GroupUin,
        GroupName = group.GroupName,
        MemberCount = group.MemberCount,
        MaxMemberCount = group.MaxMember,
        CreatedTime = group.CreateTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(group.CreateTime) : null,
        Description = group.Description,
        Announcement = group.Announcement
    };

    public static QqGroupMember ToQq(BotGroupMember member) => new()
    {
        UserId = member.Uin,
        Nickname = member.Nickname,
        GroupId = member.Group.GroupUin,
        Card = string.IsNullOrEmpty(member.MemberCard) ? null : member.MemberCard,
        Title = string.IsNullOrEmpty(member.SpecialTitle) ? null : member.SpecialTitle,
        Level = member.GroupLevel,
        Role = member.Permission switch
        {
            GroupMemberPermission.Owner => QqGroupRole.Owner,
            GroupMemberPermission.Admin => QqGroupRole.Admin,
            _ => QqGroupRole.Member
        },
        JoinTime = ToOffset(member.JoinTime),
        LastSentTime = ToOffset(member.LastMsgTime),
        ShutUpEndTime = member.ShutUpTimestamp > DateTime.UtcNow ? ToOffset(member.ShutUpTimestamp) : null
    };

    private static QqSex ToQqSex(BotGender gender) => gender switch
    {
        BotGender.Male => QqSex.Male,
        BotGender.Female => QqSex.Female,
        _ => QqSex.Unknown
    };

    private static DateTimeOffset? ToOffset(DateTime time) =>
        time == default ? null : new DateTimeOffset(time.ToUniversalTime());

    // ─── 入站消息 ───

    public static MessageEvent ToMessageEvent(BotMessage message)
    {
        var (channel, sender, member, qqMessage) = message.Contact switch
        {
            BotGroupMember groupMember => (
                new Sdk.Channel(groupMember.Group.GroupUin.ToString(), ChannelType.Group)
                {
                    Name = groupMember.Group.GroupName,
                    GuildId = groupMember.Group.GroupUin.ToString()
                },
                new Sdk.User(groupMember.Uin.ToString()) { Name = groupMember.Nickname },
                new Member(new Sdk.User(groupMember.Uin.ToString()) { Name = groupMember.Nickname })
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
                (QqIncomingMessage)new QqGroupMessage
                {
                    PeerId = groupMember.Group.GroupUin,
                    MessageSeq = (long)message.Sequence,
                    SenderId = groupMember.Uin,
                    Time = new DateTimeOffset(message.Time.ToUniversalTime()),
                    Segments = ToQqSegments(message.Entities),
                    Group = ToQq(groupMember.Group),
                    GroupMember = ToQq(groupMember)
                }),
            BotFriend friend => (
                Sdk.Channel.Direct(friend.Uin.ToString()),
                new Sdk.User(friend.Uin.ToString()) { Name = friend.Nickname },
                (Member?)null,
                (QqIncomingMessage)new QqFriendMessage
                {
                    PeerId = friend.Uin,
                    MessageSeq = (long)message.Sequence,
                    SenderId = friend.Uin,
                    Time = new DateTimeOffset(message.Time.ToUniversalTime()),
                    Segments = ToQqSegments(message.Entities),
                    Friend = ToQq(friend)
                }),
            _ => (
                new Sdk.Channel(message.Contact.Uin.ToString(), ChannelType.Other),
                new Sdk.User(message.Contact.Uin.ToString()) { Name = message.Contact.Nickname },
                (Member?)null,
                (QqIncomingMessage)new QqTempMessage
                {
                    PeerId = message.Contact.Uin,
                    MessageSeq = (long)message.Sequence,
                    SenderId = message.Contact.Uin,
                    Time = new DateTimeOffset(message.Time.ToUniversalTime()),
                    Segments = ToQqSegments(message.Entities)
                })
        };

        return new MessageEvent
        {
            Platform = PlatformId,
            SelfId = SelfId,
            Raw = qqMessage,
            MessageId = message.Sequence.ToString(),
            Channel = channel,
            Sender = sender,
            Member = member,
            Segments = ToSegments(message.Entities),
            Timestamp = new DateTimeOffset(message.Time.ToUniversalTime())
        };
    }

    // ─── 段:Lagrange → 核心 ───

    public static IReadOnlyList<MessageSegment> ToSegments(MessageChain chain) =>
        chain.Select(ToSegment).ToArray();

    private static MessageSegment ToSegment(IMessageEntity entity) => entity switch
    {
        TextEntity text => new TextSegment(text.Text),
        MentionEntity { Uin: 0 } => new MentionAllSegment(),
        MentionEntity mention => new MentionSegment(mention.Uin.ToString()) { DisplayName = mention.Display },
        ReplyEntity reply => new QuoteSegment(reply.SrcSequence.ToString()),
        ImageEntity image => new ImageSegment(image.FileUrl ?? string.Empty)
        {
            ResourceId = image.FileUuid,
            Width = (int)image.ImageSize.X,
            Height = (int)image.ImageSize.Y,
            Summary = image.Summary
        },
        RecordEntity record => new AudioSegment(record.FileUrl ?? string.Empty)
        {
            ResourceId = record.FileUuid,
            Duration = TimeSpan.FromSeconds(record.RecordLength)
        },
        VideoEntity video => new VideoSegment(video.FileUrl ?? string.Empty)
        {
            ResourceId = video.FileUuid
        },
        GroupFileEntity file => new Sdk.FileSegment(file.FileUrl ?? string.Empty)
        {
            ResourceId = file.FileId,
            FileName = file.FileName,
            FileSize = file.FileSize
        },
        MultiMsgEntity multi => new RawSegment(PlatformId, "forward",
            new QqForwardIncoming(multi.ResId ?? string.Empty)),
        LightAppEntity lightApp => new RawSegment(PlatformId, "light_app",
            new QqLightAppIncoming(lightApp.AppName, lightApp.Payload)),
        _ => new RawSegment(PlatformId, entity.GetType().Name, entity)
    };

    // ─── 段:Lagrange → Qq.Model(用于 Raw) ───

    public static IReadOnlyList<QqIncomingSegment> ToQqSegments(MessageChain chain) =>
        chain.Select(ToQqSegment).ToArray();

    private static QqIncomingSegment ToQqSegment(IMessageEntity entity) => entity switch
    {
        TextEntity text => new QqTextIncoming(text.Text),
        MentionEntity { Uin: 0 } => new QqMentionAllIncoming(),
        MentionEntity mention => new QqMentionIncoming(mention.Uin, mention.Display ?? string.Empty),
        ReplyEntity reply => new QqReplyIncoming((long)reply.SrcSequence),
        ImageEntity image => new QqImageIncoming(image.FileUuid ?? string.Empty, image.FileUrl ?? string.Empty)
        {
            Width = (int)image.ImageSize.X,
            Height = (int)image.ImageSize.Y,
            Summary = image.Summary
        },
        RecordEntity record => new QqRecordIncoming(record.FileUuid ?? string.Empty, record.FileUrl ?? string.Empty)
        {
            Duration = TimeSpan.FromSeconds(record.RecordLength)
        },
        VideoEntity video => new QqVideoIncoming(video.FileUuid ?? string.Empty, video.FileUrl ?? string.Empty),
        GroupFileEntity file => new QqFileIncoming(file.FileId, file.FileName, file.FileSize),
        MultiMsgEntity multi => new QqForwardIncoming(multi.ResId ?? string.Empty),
        LightAppEntity lightApp => new QqLightAppIncoming(lightApp.AppName, lightApp.Payload),
        _ => new QqTextIncoming(string.Empty)
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
                    // Lagrange.Core 暂无 FaceEntity
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
                        video.ThumbnailUri is null ? null : await OpenResourceAsync(video.ThumbnailUri).ConfigureAwait(false),
                        disposeOnCompletion: true));
                    break;
                case RawSegment { Payload: QqOutgoingSegment qqOutgoing }:
                    await AppendQqSegmentAsync(chain, qqOutgoing, resolveQuoted).ConfigureAwait(false);
                    break;
                case RawSegment { Payload: IMessageEntity lagrangeEntity }:
                    chain.Add(lagrangeEntity);
                    break;
                case Sdk.FileSegment:
                    throw Unsupported("消息内文件段(请使用 IQqFileApi.UploadGroupFileAsync/UploadPrivateFileAsync)");
                default:
                    throw Unsupported(segment.GetType().Name);
            }
        }

        return chain;
    }

    public static async Task AppendQqSegmentAsync(
        MessageChain chain,
        QqOutgoingSegment segment,
        Func<string, Task<BotMessage?>> resolveQuoted)
    {
        switch (segment)
        {
            case QqTextOutgoing text:
                chain.Add(new TextEntity(text.Text));
                break;
            case QqMentionAllOutgoing:
                chain.Add(new MentionEntity(0, "@全体成员"));
                break;
            case QqMentionOutgoing mention:
                chain.Add(new MentionEntity(mention.UserId, null));
                break;
            case QqReplyOutgoing reply:
                var quoted = await resolveQuoted(reply.MessageSeq.ToString()).ConfigureAwait(false);
                if (quoted is not null)
                {
                    chain.Add(new ReplyEntity(quoted));
                }
                break;
            case QqImageOutgoing image:
                chain.Add(new ImageEntity(await OpenResourceAsync(image.Uri).ConfigureAwait(false),
                    image.Summary ?? "[图片]", disposeOnCompletion: true));
                break;
            case QqRecordOutgoing record:
                chain.Add(new RecordEntity(await OpenResourceAsync(record.Uri).ConfigureAwait(false),
                    disposeOnCompletion: true));
                break;
            case QqVideoOutgoing video:
                chain.Add(new VideoEntity(
                    await OpenResourceAsync(video.Uri).ConfigureAwait(false),
                    video.ThumbUri is null ? null : await OpenResourceAsync(video.ThumbUri).ConfigureAwait(false),
                    disposeOnCompletion: true));
                break;
            case QqLightAppOutgoing lightApp:
                chain.Add(new LightAppEntity(lightApp.JsonPayload));
                break;
            case QqFaceOutgoing:
                throw Unsupported("QQ 表情段(FaceEntity)");
            case QqForwardOutgoing forward:
                chain.Add(await ToMultiMsgAsync(forward, resolveQuoted).ConfigureAwait(false));
                break;
            default:
                throw Unsupported(segment.GetType().Name);
        }
    }

    /// <summary>合并转发 → MultiMsgEntity(伪造消息)。</summary>
    public static async Task<MultiMsgEntity> ToMultiMsgAsync(
        QqForwardOutgoing forward,
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
                message.UserId,
                message.SenderName,
                (message.Time ?? DateTimeOffset.Now).LocalDateTime,
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

        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(uri).ConfigureAwait(false);
            return new MemoryStream(bytes);
        }

        // 裸路径按本地文件处理
        return File.OpenRead(uri);
    }

    // ─── 工具 ───

    public static long ParseId(string id, string what)
    {
        if (!long.TryParse(id, out var value))
        {
            throw new ArgumentException($"QQ(Lagrange) 平台的 {what} 必须是数字,实际为 '{id}'。");
        }

        return value;
    }

    public static NotSupportedException Unsupported(string what) =>
        new($"Lagrange 适配器暂不兼容: {what}。");
}
