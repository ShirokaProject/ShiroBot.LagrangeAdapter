using System.Buffers;
using System.Diagnostics;
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
public static partial class LagrangeMapper
{
    private static readonly HttpClient ResourceClient = new();
    private static readonly SemaphoreSlim ForwardImageDownloadSlots = new(8, 8);
    private static readonly TimeSpan ForwardImageInactivityTimeout = TimeSpan.FromSeconds(12);
    public const string PlatformId = "qq";


    // ─── 实体 ───

    public static QFriend ToQq(BotFriend friend) => new()
    {
        UserId = friend.Uin.ToString(),
        Nickname = friend.Nickname,
        Sex = ToQqSex(friend.Gender),
        Qid = string.IsNullOrEmpty(friend.Qid) ? null : friend.Qid,
        Remark = string.IsNullOrEmpty(friend.Remarks) ? null : friend.Remarks,
        Category = friend.Category is { } category ? new QFriendCategory(category.Id, category.Name) : null
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
        PrivateFileEntity file => new FileSegment("") { ResourceId = file.FileId, FileName = file.FileName, FileSize = file.FileSize },
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
        VideoEntity { IsBubble: true } bubble => new RawSegment(PlatformId, "bubble", ToBubble(bubble)),
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
        MarkdownEntity markdown => TryReadFlashCard(markdown.Content) is { } flash
            ? new RawSegment(PlatformId, "flash_file", flash)
            : new TextSegment(markdown.Content),
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
        VideoEntity { IsBubble: true } bubble => ToBubble(bubble),
        VideoEntity video => new QIncomingVideo(video.FileUuid, video.FileUrl),
        GroupFileEntity file => new QIncomingFile(file.FileId, file.FileName, file.FileSize),
        PrivateFileEntity file => new QIncomingFile(file.FileId, file.FileName, file.FileSize) { FileHash = file.FileHash },
        MultiMsgEntity multi => new QIncomingForward(multi.ResId ?? string.Empty),
        LightAppEntity lightApp => new QIncomingLightApp(lightApp.AppName, lightApp.Payload),
        MarkdownEntity markdown => (QIncomingSegment?)TryReadFlashCard(markdown.Content) ?? new QIncomingText(markdown.Content),
        _ => new QIncomingText(string.Empty)
    };

    private static QIncomingBubbleVideo ToBubble(VideoEntity video) => new(video.FileUuid, video.FileUrl)
    {
        Width = (int)video.VideoSize.X,
        Height = (int)video.VideoSize.Y,
        Duration = TimeSpan.FromSeconds(video.VideoLength)
    };

    // ─── 段:核心/Qq → Lagrange(出站) ───

    public static async Task<MessageChain> ToChainAsync(
        IReadOnlyList<MessageSegment> segments,
        Func<string, Task<BotMessage?>> resolveQuoted, Lagrange.Core.BotContext? context = null,
        CancellationToken cancellationToken = default, List<Stream>? resources = null)
    {
        var chain = new MessageChain();
        var trackedResources = resources ?? [];
        try
        {
            foreach (var segment in segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    case EmojiSegment emoji:
                        chain.Add(await LagrangeFaceCatalog.Create(context, int.Parse(emoji.Id), false));
                        break;
                    case ImageSegment image:
                        chain.Add(new ImageEntity(await OpenMessageResourceAsync(image.Uri, cancellationToken, trackedResources).ConfigureAwait(false),
                            image.Summary ?? "[图片]", disposeOnCompletion: true));
                        break;
                    case AudioSegment audio:
                        chain.Add(new RecordEntity(await OpenMessageResourceAsync(audio.Uri, cancellationToken, trackedResources).ConfigureAwait(false),
                            disposeOnCompletion: true));
                        break;
                    case VideoSegment video:
                        chain.Add(await CreateVideoAsync(video.Uri, video.ThumbnailUri, cancellationToken, trackedResources).ConfigureAwait(false));
                        break;
                    case RawSegment { Payload: QOutgoingSegment qqOutgoing }:
                        await AppendQqSegmentAsync(chain, qqOutgoing, resolveQuoted, context, cancellationToken, trackedResources).ConfigureAwait(false);
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
        catch when (resources is null)
        {
            foreach (var stream in trackedResources) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task AppendQqSegmentAsync(
        MessageChain chain,
        QOutgoingSegment segment,
        Func<string, Task<BotMessage?>> resolveQuoted, Lagrange.Core.BotContext? context = null,
        CancellationToken cancellationToken = default, List<Stream>? resources = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
                chain.Add(new ImageEntity(await OpenMessageResourceAsync(image.Uri, cancellationToken, resources).ConfigureAwait(false),
                    image.Summary ?? "[图片]", disposeOnCompletion: true));
                break;
            case QOutgoingRecord record:
                chain.Add(new RecordEntity(await OpenMessageResourceAsync(record.Uri, cancellationToken, resources).ConfigureAwait(false),
                    disposeOnCompletion: true));
                break;
            case QOutgoingVideo video:
                chain.Add(await CreateVideoAsync(video.Uri, video.ThumbUri, cancellationToken, resources).ConfigureAwait(false));
                break;
            case QOutgoingBubbleVideo bubble:
                chain.Add(await CreateVideoAsync(bubble.Uri, bubble.ThumbUri, cancellationToken, resources, isBubble: true).ConfigureAwait(false));
                break;
            case QOutgoingLightApp lightApp:
                chain.Add(new LightAppEntity(lightApp.JsonPayload));
                break;
            case QOutgoingFace face:
                chain.Add(await LagrangeFaceCatalog.Create(context, int.Parse(face.FaceId), face.IsLarge));
                break;
            case QOutgoingForward forward:
                chain.Add(await ToMultiMsgAsync(forward, resolveQuoted, context, cancellationToken, resources).ConfigureAwait(false));
                break;
            default:
                throw Unsupported(segment.GetType().Name);
        }
    }

    /// <summary>合并转发 → MultiMsgEntity(伪造消息)。</summary>
    public static async Task<MultiMsgEntity> ToMultiMsgAsync(
        QOutgoingForward forward,
        Func<string, Task<BotMessage?>> resolveQuoted, Lagrange.Core.BotContext? context = null,
        CancellationToken cancellationToken = default, List<Stream>? resources = null)
    {
        var remoteImages = forward.Messages.SelectMany(message => message.Segments).OfType<QOutgoingImage>()
            .Where(image => IsHttpResource(image.Uri)).ToArray();
        var downloaded = new Stream?[remoteImages.Length];
        var nodeResources = resources ?? [];
        var completed = false;
        try
        {
            if (remoteImages.Length > 0)
            {
                var started = Stopwatch.GetTimestamp();
                var trace = Activity.Current?.TraceId.ToString() ?? "-";
                context?.LogDebug(nameof(LagrangeMapper), "trace={0} forward image download start images={1}", trace, remoteImages.Length);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(40));
                try
                {
                    await Parallel.ForEachAsync(Enumerable.Range(0, remoteImages.Length),
                        new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token },
                        async (index, token) =>
                        {
                            downloaded[index] = await DownloadForwardImageAsync(remoteImages[index].Uri, token).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Downloading images for the forwarded message exceeded 40 seconds.");
                }
                finally
                {
                    context?.LogDebug(nameof(LagrangeMapper),
                        "trace={0} forward image download finished ready={1}/{2} elapsed_ms={3:F0}",
                        trace, downloaded.Count(stream => stream is not null), remoteImages.Length,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }

            var messages = new List<BotMessage>();
            var imageIndex = 0;
            foreach (var message in forward.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chain = new MessageChain();
                foreach (var segment in message.Segments)
                {
                    if (segment is QOutgoingImage image && IsHttpResource(image.Uri))
                    {
                        chain.Add(new ImageEntity(downloaded[imageIndex++]!, image.Summary ?? "[图片]",
                            disposeOnCompletion: true));
                        continue;
                    }
                    await AppendQqSegmentAsync(chain, segment, resolveQuoted, context, cancellationToken, nodeResources).ConfigureAwait(false);
                }

                messages.Add(BotMessage.CreateCustomGroup(
                    0, ParseId(message.UserId, "UserId"), message.SenderName,
                    (message.Time ?? DateTimeOffset.Now).ToUnixTimeSeconds(), chain));
            }

            if (resources is not null)
                foreach (var stream in downloaded) resources.Add(stream!);
            completed = true;
            return new MultiMsgEntity(messages)
            {
                Title = forward.Title,
                Preview = forward.Preview,
                Summary = forward.Summary,
                Prompt = forward.Prompt
            };
        }
        finally
        {
            if (!completed)
            {
                foreach (var stream in downloaded)
                    if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
                if (resources is null)
                    foreach (var stream in nodeResources) await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<Stream> DownloadForwardImageAsync(string uri, CancellationToken token)
    {
        await ForwardImageDownloadSlots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await DownloadHttpResourceAsync(uri, bufferResponse: false, forwardedImage: true, token)
                .ConfigureAwait(false);
        }
        finally
        {
            ForwardImageDownloadSlots.Release();
        }
    }

    internal static async Task<VideoEntity> CreateVideoAsync(string uri, string? thumbnailUri,
        CancellationToken cancellationToken, List<Stream>? resources, bool isBubble = false)
    {
        var video = await OpenMessageResourceAsync(uri, cancellationToken, null).ConfigureAwait(false);
        try
        {
            var (size, duration, thumbnail) = await LagrangeVideoMedia.PrepareAsync(
                video, generateThumbnail: thumbnailUri is null, cancellationToken).ConfigureAwait(false);
            if (thumbnailUri is not null)
                thumbnail = await OpenMessageResourceAsync(thumbnailUri, cancellationToken, null).ConfigureAwait(false);
            resources?.Add(video);
            // A missing cover is created internally by VideoEntity and must also be disposed after a send.
            if (thumbnail is not null) resources?.Add(thumbnail);
            return new VideoEntity(video, thumbnail, disposeOnCompletion: true)
            {
                IsBubble = isBubble,
                VideoSize = size,
                VideoLength = duration
            };
        }
        catch
        {
            await video.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Stream> OpenMessageResourceAsync(string uri, CancellationToken cancellationToken,
        List<Stream>? resources)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = await OpenResourceAsync(uri, bufferHttpResponse: false, cancellationToken).ConfigureAwait(false);
        resources?.Add(stream);
        return stream;
    }

    /// <summary>解析 http(s)://、file://、base64:// 三种资源形式为 Stream。</summary>
    public static async Task<Stream> OpenResourceAsync(string uri, bool bufferHttpResponse = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (uri.StartsWith("base64://", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenBase64ResourceAsync(uri, cancellationToken).ConfigureAwait(false);
        }

        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return File.OpenRead(new Uri(uri).LocalPath);
        }

        if (!IsHttpResource(uri)) return File.OpenRead(uri);
        return await DownloadHttpResourceAsync(uri, bufferHttpResponse, forwardedImage: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Stream> DownloadHttpResourceAsync(string uri, bool bufferResponse, bool forwardedImage,
        CancellationToken cancellationToken)
    {
        string host = new Uri(uri).IdnHost;
        string resource = forwardedImage ? "a forwarded image" : "media";
        for (int attempt = 1; ; attempt++)
        {
            using var deadline = forwardedImage ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
            deadline?.CancelAfter(ForwardImageInactivityTimeout);
            var token = deadline?.Token ?? cancellationToken;
            try
            {
                return bufferResponse
                    ? new MemoryStream(await ResourceClient.GetByteArrayAsync(uri, token).ConfigureAwait(false))
                    : await ReadHttpResourceAsync(uri, token, deadline).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested &&
                (deadline?.IsCancellationRequested == true || exception.InnerException is TimeoutException))
            {
                if (attempt == 2)
                    throw new TimeoutException($"Downloading {resource} from {host} timed out after two attempts.");
            }
            catch (HttpRequestException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt == 2 || !IsTransientDownloadFailure(exception))
                {
                    var reason = exception.StatusCode is { } status ? $"HTTP {(int)status}" : exception.HttpRequestError.ToString();
                    throw new HttpRequestException(exception.HttpRequestError,
                        $"Downloading {resource} from {host} failed ({reason}).", inner: null, exception.StatusCode);
                }
            }
            catch (HttpIOException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt == 2)
                    throw new HttpRequestException(exception.HttpRequestError,
                        $"Downloading {resource} from {host} failed while reading the response.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransientDownloadFailure(HttpRequestException exception) =>
        exception.StatusCode is { } status
            ? status is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests || (int)status >= 500
            : exception.HttpRequestError is HttpRequestError.Unknown or HttpRequestError.NameResolutionError or
                HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded;

    private static async Task<Stream> ReadHttpResourceAsync(string uri, CancellationToken cancellationToken,
        CancellationTokenSource? inactivityDeadline = null)
    {
        using var response = await ResourceClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var file = CreateTemporaryResource();
        try
        {
            if (inactivityDeadline is not null)
                await CopyForwardImageAsync(response.Content, file, inactivityDeadline).ConfigureAwait(false);
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(20));
                await response.Content.CopyToAsync(file, timeout.Token).ConfigureAwait(false);
            }
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static FileStream CreateTemporaryResource()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<Stream> OpenBase64ResourceAsync(string uri, CancellationToken cancellationToken)
    {
        const int blockSize = 16 * 1024;
        var chars = ArrayPool<char>.Shared.Rent(blockSize);
        var bytes = ArrayPool<byte>.Shared.Rent(blockSize / 4 * 3);
        try
        {
            var file = CreateTemporaryResource();
            try
            {
                var count = 0;
                var padded = false;
                for (var index = "base64://".Length; index < uri.Length; index++)
                {
                    if (index % blockSize == 0) cancellationToken.ThrowIfCancellationRequested();
                    var character = uri[index];
                    if (character is ' ' or '\t' or '\r' or '\n') continue;
                    if (padded) throw new FormatException("Invalid Base64 media.");
                    chars[count++] = character;
                    if (count < blockSize) continue;

                    if (!Convert.TryFromBase64Chars(chars.AsSpan(0, count), bytes, out var written))
                        throw new FormatException("Invalid Base64 media.");
                    await file.WriteAsync(bytes.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                    padded = character == '=';
                    count = 0;
                }

                if (count > 0)
                {
                    if (!Convert.TryFromBase64Chars(chars.AsSpan(0, count), bytes, out var written))
                        throw new FormatException("Invalid Base64 media.");
                    await file.WriteAsync(bytes.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                }

                file.Position = 0;
                return file;
            }
            catch
            {
                await file.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            ArrayPool<char>.Shared.Return(chars, clearArray: true);
        }
    }

    private static async Task CopyForwardImageAsync(HttpContent content, Stream file, CancellationTokenSource deadline)
    {
        deadline.CancelAfter(ForwardImageInactivityTimeout);
        await using var body = await content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int count;
            while ((count = await body.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                deadline.CancelAfter(ForwardImageInactivityTimeout);
                await file.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsHttpResource(string uri) =>
        uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

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
