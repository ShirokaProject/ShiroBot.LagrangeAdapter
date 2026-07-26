using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using ShiroBot.Qq.Model;

namespace ShiroBot.LagrangeAdapter.Services;

/// <summary>
/// IQq*Api 的 Lagrange 实现。Lagrange.Core 尚未提供的能力保留接口默认实现
/// (NotSupportedException),插件通过能力探测降级。
/// </summary>
internal sealed class LagrangeQqFriendApi(LagrangeSession session) : IQqFriendApi
{
    public Task SendNudgeAsync(long userId, bool isSelf = false) =>
        session.Context.SendFriendNudge(userId, isSelf ? session.Context.BotUin : null);

    public Task SendProfileLikeAsync(long userId, int count = 1) =>
        throw LagrangeMapper.Unsupported("名片点赞(Lagrange.Core 未提供 API)");

    public Task DeleteFriendAsync(long userId) =>
        throw LagrangeMapper.Unsupported("删除好友(Lagrange.Core 未提供 API)");

    // 好友请求列表/处理:Lagrange.Core 只有事件,无处理 API — 走接口默认 NotSupported
}

internal sealed class LagrangeQqGroupApi(LagrangeSession session) : IQqGroupApi
{
    public Task SetGroupNameAsync(long groupId, string name) =>
        session.Context.GroupRename(groupId, name);

    public Task SetMemberCardAsync(long groupId, long userId, string card) =>
        session.Context.GroupMemberRename(groupId, userId, card);

    public Task SetMemberSpecialTitleAsync(long groupId, long userId, string title) =>
        session.Context.GroupSetSpecialTitle(groupId, userId, title);

    public Task QuitGroupAsync(long groupId) =>
        session.Context.GroupQuit(groupId);

    public Task SendNudgeAsync(long groupId, long userId) =>
        session.Context.SendGroupNudge(groupId, userId);

    public Task SendMessageReactionAsync(long groupId, long messageSeq, string faceId, bool isAdd = true) =>
        session.Context.SetGroupReaction(groupId, (ulong)messageSeq, faceId, isAdd);

    public Task SendMessageReactionAsync(long groupId, long messageSeq, string reactionId, QqReactionType reactionType, bool isAdd = true) =>
        SendMessageReactionAsync(groupId, messageSeq, reactionId, isAdd);

    public Task AcceptJoinRequestAsync(QqGroupJoinRequest request) =>
        session.Context.SetGroupNotification(
            request.GroupId,
            (ulong)request.NotificationSeq,
            BotGroupNotificationType.Join,
            request.IsFiltered,
            GroupNotificationOperate.Allow);

    public Task RejectJoinRequestAsync(QqGroupJoinRequest request, string? reason = null) =>
        session.Context.SetGroupNotification(
            request.GroupId,
            (ulong)request.NotificationSeq,
            BotGroupNotificationType.Join,
            request.IsFiltered,
            GroupNotificationOperate.Deny,
            reason ?? string.Empty);

    public Task AcceptJoinRequestAsync(long groupId, long notificationSeq, bool isInvited = false, bool isFiltered = false) =>
        session.Context.SetGroupNotification(
            groupId,
            (ulong)notificationSeq,
            isInvited ? BotGroupNotificationType.Invite : BotGroupNotificationType.Join,
            isFiltered,
            GroupNotificationOperate.Allow);

    public Task RejectJoinRequestAsync(long groupId, long notificationSeq, bool isInvited = false, bool isFiltered = false, string? reason = null) =>
        session.Context.SetGroupNotification(
            groupId,
            (ulong)notificationSeq,
            isInvited ? BotGroupNotificationType.Invite : BotGroupNotificationType.Join,
            isFiltered,
            GroupNotificationOperate.Deny,
            reason ?? string.Empty);

    public async Task<(IReadOnlyList<QqGroupNotification> Notifications, long? NextNotificationSeq)> GetNotificationsAsync(
        long? startNotificationSeq = null, bool isFiltered = false, int limit = 20)
    {
        var notifications = isFiltered
            ? await session.Context.FetchFilteredGroupNotifications((ulong)limit, (ulong)(startNotificationSeq ?? 0)).ConfigureAwait(false)
            : await session.Context.FetchGroupNotifications((ulong)limit, (ulong)(startNotificationSeq ?? 0)).ConfigureAwait(false);

        var converted = notifications
            .Select(ToQqNotification)
            .Where(notification => notification is not null)
            .Cast<QqGroupNotification>()
            .ToArray();

        var next = notifications.Count > 0 ? (long?)(long)notifications[^1].Sequence : null;
        return (converted, next);
    }

    private static QqGroupNotification? ToQqNotification(BotGroupNotificationBase notification) => notification switch
    {
        BotGroupJoinNotification join => new QqJoinRequestNotification
        {
            GroupId = join.GroupUin,
            NotificationSeq = (long)join.Sequence,
            InitiatorId = join.TargetUin,
            State = ToQqState(join.State),
            Comment = join.Comment,
            IsFiltered = join.IsFiltered,
            OperatorId = join.OperatorUin
        },
        BotGroupInviteNotification invite => new QqInvitedJoinRequestNotification
        {
            GroupId = invite.GroupUin,
            NotificationSeq = (long)invite.Sequence,
            InitiatorId = invite.InviterUin,
            TargetUserId = invite.TargetUin,
            State = ToQqState(invite.State),
            OperatorId = invite.OperatorUin
        },
        BotGroupSetAdminNotification admin => new QqAdminChangeNotification
        {
            GroupId = admin.GroupUin,
            NotificationSeq = (long)admin.Sequence,
            TargetUserId = admin.TargetUin,
            IsSet = true,
            OperatorId = admin.OperatorUin
        },
        BotGroupUnsetAdminNotification unset => new QqAdminChangeNotification
        {
            GroupId = unset.GroupUin,
            NotificationSeq = (long)unset.Sequence,
            TargetUserId = unset.TargetUin,
            IsSet = false,
            OperatorId = unset.OperatorUin
        },
        BotGroupKickNotification kick => new QqKickNotification
        {
            GroupId = kick.GroupUin,
            NotificationSeq = (long)kick.Sequence,
            TargetUserId = kick.TargetUin,
            OperatorId = kick.OperatorUin
        },
        BotGroupExitNotification exit => new QqQuitNotification
        {
            GroupId = exit.GroupUin,
            NotificationSeq = (long)exit.Sequence,
            TargetUserId = exit.TargetUin
        },
        _ => null
    };

    private static QqRequestState ToQqState(BotGroupNotificationState state) => state switch
    {
        BotGroupNotificationState.Accept => QqRequestState.Accepted,
        BotGroupNotificationState.Reject => QqRequestState.Rejected,
        _ => QqRequestState.Pending
    };

    // 不支持项(Lagrange.Core 未提供 API):
    public Task SetGroupAvatarAsync(long groupId, string imageUri) =>
        throw LagrangeMapper.Unsupported("设置群头像(Lagrange.Core 未提供 API)");

    public Task SetMemberAdminAsync(long groupId, long userId, bool isSet = true) =>
        throw LagrangeMapper.Unsupported("设置群管理员(Lagrange.Core 未提供 API)");

    public Task MuteMemberAsync(long groupId, long userId, TimeSpan duration) =>
        throw LagrangeMapper.Unsupported("群成员禁言(Lagrange.Core 未提供 API)");

    public Task SetWholeMuteAsync(long groupId, bool isMute = true) =>
        throw LagrangeMapper.Unsupported("全员禁言(Lagrange.Core 未提供 API)");

    public Task KickMemberAsync(long groupId, long userId, bool rejectAddRequest = false) =>
        throw LagrangeMapper.Unsupported("踢出群成员(Lagrange.Core 未提供 API)");

    public Task<IReadOnlyList<QqGroupAnnouncement>> GetAnnouncementsAsync(long groupId) =>
        throw LagrangeMapper.Unsupported("群公告(Lagrange.Core 未提供 API)");

    public Task SendAnnouncementAsync(long groupId, string content, string? imageUri = null) =>
        throw LagrangeMapper.Unsupported("群公告(Lagrange.Core 未提供 API)");

    public Task DeleteAnnouncementAsync(long groupId, string announcementId) =>
        throw LagrangeMapper.Unsupported("群公告(Lagrange.Core 未提供 API)");

    public Task<IReadOnlyList<QqEssenceMessage>> GetEssenceMessagesAsync(long groupId, int pageIndex, int pageSize) =>
        throw LagrangeMapper.Unsupported("精华消息(Lagrange.Core 未提供 API)");

    public Task SetEssenceMessageAsync(long groupId, long messageSeq, bool isSet = true) =>
        throw LagrangeMapper.Unsupported("精华消息(Lagrange.Core 未提供 API)");

    public Task AcceptInvitationAsync(long groupId, long invitationSeq) =>
        throw LagrangeMapper.Unsupported("处理他人邀请自身入群(Lagrange.Core 未提供 API)");

    public Task RejectInvitationAsync(long groupId, long invitationSeq) =>
        throw LagrangeMapper.Unsupported("处理他人邀请自身入群(Lagrange.Core 未提供 API)");
}

internal sealed class LagrangeQqFileApi(LagrangeSession session) : IQqFileApi
{
    public async Task<string> UploadPrivateFileAsync(long userId, string fileUri, string fileName)
    {
        await using var stream = await LagrangeMapper.OpenResourceAsync(fileUri).ConfigureAwait(false);
        await session.Context.SendFriendFile(userId, stream, fileName).ConfigureAwait(false);
        return string.Empty; // 好友文件无 fileId
    }

    public async Task<string> UploadGroupFileAsync(long groupId, string fileUri, string fileName, string parentFolderId = "/")
    {
        await using var stream = await LagrangeMapper.OpenResourceAsync(fileUri).ConfigureAwait(false);
        return await session.Context.SendGroupFile(groupId, stream, fileName, parentFolderId).ConfigureAwait(false);
    }

    public Task<string> GetGroupFileDownloadUrlAsync(long groupId, string fileId) =>
        session.Context.GroupFSDownload(groupId, fileId);

    public Task MoveGroupFileAsync(long groupId, string fileId, string targetFolderId, string parentFolderId = "/") =>
        session.Context.GroupFSMove(groupId, fileId, targetFolderId, parentFolderId);

    public Task DeleteGroupFileAsync(long groupId, string fileId) =>
        session.Context.GroupFSDelete(groupId, fileId);

    // 不支持项:
    public Task<string> GetPrivateFileDownloadUrlAsync(long userId, string fileId, string fileHash) =>
        throw LagrangeMapper.Unsupported("私聊文件下载链接(Lagrange.Core 未提供 API)");

    public Task<(IReadOnlyList<QqGroupFile> Files, IReadOnlyList<QqGroupFolder> Folders)> GetGroupFilesAsync(
        long groupId, string parentFolderId = "/") =>
        throw LagrangeMapper.Unsupported("群文件列表(Lagrange.Core 未提供 API)");

    public Task RenameGroupFileAsync(long groupId, string fileId, string newFileName, string parentFolderId = "/") =>
        throw LagrangeMapper.Unsupported("重命名群文件(Lagrange.Core 未提供 API)");

    public Task<string> CreateGroupFolderAsync(long groupId, string folderName) =>
        throw LagrangeMapper.Unsupported("创建群文件夹(Lagrange.Core 未提供 API)");

    public Task RenameGroupFolderAsync(long groupId, string folderId, string newFolderName) =>
        throw LagrangeMapper.Unsupported("重命名群文件夹(Lagrange.Core 未提供 API)");

    public Task DeleteGroupFolderAsync(long groupId, string folderId) =>
        throw LagrangeMapper.Unsupported("删除群文件夹(Lagrange.Core 未提供 API)");

    public Task PersistGroupFileAsync(long groupId, string fileId) =>
        throw LagrangeMapper.Unsupported("群文件转永久(Lagrange.Core 未提供 API)");
}

internal sealed class LagrangeQqSystemApi(LagrangeSession session) : IQqSystemApi
{
    public async Task<QqUserProfile> GetUserProfileAsync(long userId)
    {
        var stranger = await session.Context.FetchStranger(userId).ConfigureAwait(false);
        return new QqUserProfile
        {
            UserId = stranger.Uin,
            Nickname = stranger.Nickname,
            Qid = string.IsNullOrEmpty(stranger.QID) ? null : stranger.QID,
            Age = (int)stranger.Age,
            Sex = stranger.Gender switch
            {
                BotGender.Male => QqSex.Male,
                BotGender.Female => QqSex.Female,
                _ => QqSex.Unknown
            },
            Remark = string.IsNullOrEmpty(stranger.Remark) ? null : stranger.Remark,
            Bio = string.IsNullOrEmpty(stranger.PersonalSign) ? null : stranger.PersonalSign,
            Level = (int)stranger.Level,
            Country = string.IsNullOrEmpty(stranger.Country) ? null : stranger.Country,
            City = string.IsNullOrEmpty(stranger.City) ? null : stranger.City,
            School = stranger.School
        };
    }

    public async Task<IReadOnlyList<QqFriend>> GetFriendListAsync(bool noCache = false)
    {
        var friends = await session.Context.FetchFriends(noCache).ConfigureAwait(false);
        return friends.Select(LagrangeMapper.ToQq).ToArray();
    }

    public async Task<QqFriend> GetFriendInfoAsync(long userId, bool noCache = false)
    {
        var friends = await GetFriendListAsync(noCache).ConfigureAwait(false);
        return friends.FirstOrDefault(friend => friend.UserId == userId)
               ?? throw new InvalidOperationException($"好友 {userId} 不存在。");
    }

    public async Task<IReadOnlyList<QqGroup>> GetGroupListAsync(bool noCache = false)
    {
        var groups = await session.Context.FetchGroups(noCache).ConfigureAwait(false);
        return groups.Select(LagrangeMapper.ToQq).ToArray();
    }

    public async Task<QqGroup> GetGroupInfoAsync(long groupId, bool noCache = false)
    {
        var groups = await GetGroupListAsync(noCache).ConfigureAwait(false);
        return groups.FirstOrDefault(group => group.GroupId == groupId)
               ?? throw new InvalidOperationException($"群 {groupId} 不存在。");
    }

    public async Task<IReadOnlyList<QqGroupMember>> GetGroupMemberListAsync(long groupId, bool noCache = false)
    {
        var members = await session.Context.FetchMembers(groupId, noCache).ConfigureAwait(false);
        return members.Select(LagrangeMapper.ToQq).ToArray();
    }

    public async Task<QqGroupMember> GetGroupMemberInfoAsync(long groupId, long userId, bool noCache = false)
    {
        var members = await GetGroupMemberListAsync(groupId, noCache).ConfigureAwait(false);
        return members.FirstOrDefault(member => member.UserId == userId)
               ?? throw new InvalidOperationException($"群成员 {groupId}/{userId} 不存在。");
    }

    public async Task SetAvatarAsync(string imageUri)
    {
        await using var stream = await LagrangeMapper.OpenResourceAsync(imageUri).ConfigureAwait(false);
        await session.Context.SetBotAvatar(stream).ConfigureAwait(false);
    }

    public async Task<string> GetCookiesAsync(string domain)
    {
        var cookies = await session.Context.FetchCookies([domain]).ConfigureAwait(false);
        return cookies.GetValueOrDefault(domain, string.Empty);
    }

    async Task<QqLoginInfo> IQqSystemApi.GetLoginInfoAsync()
    {
        var context = session.Context;
        return await Task.FromResult(new QqLoginInfo(context.BotUin, context.BotInfo?.Name ?? string.Empty));
    }

    async Task<QqImplInfo> IQqSystemApi.GetImplInfoAsync()
    {
        var context = session.Context;
        return await Task.FromResult(new QqImplInfo
        {
            ImplName = "Lagrange.Core",
            ImplVersion = typeof(Lagrange.Core.BotContext).Assembly.GetName().Version?.ToString() ?? "unknown",
            QqProtocolType = context.Config.Protocol.ToString().ToLowerInvariant()
        });
    }

    // 不支持项:
    public Task SetNicknameAsync(string nickname) =>
        throw LagrangeMapper.Unsupported("设置昵称(Lagrange.Core 未提供 API)");

    public Task SetBioAsync(string bio) =>
        throw LagrangeMapper.Unsupported("设置个性签名(Lagrange.Core 未提供 API)");

    public Task<string> GetCsrfTokenAsync() =>
        throw LagrangeMapper.Unsupported("CSRF Token(可用 GetCookiesAsync 自行计算 bkn)");
}

internal sealed class LagrangeQqMessageApi(LagrangeSession session) : IQqMessageApi
{
    public async Task<long> SendMessageAsync(QqMessageScene scene, long peerId, IReadOnlyList<QqOutgoingSegment> segments)
    {
        var chain = new Lagrange.Core.Message.MessageChain();
        foreach (var segment in segments)
        {
            await LagrangeMapper.AppendQqSegmentAsync(chain, segment, session.ResolveMessageAsync).ConfigureAwait(false);
        }

        var sent = scene == QqMessageScene.Group
            ? await session.Context.SendGroupMessage(peerId, chain).ConfigureAwait(false)
            : await session.Context.SendFriendMessage(peerId, chain).ConfigureAwait(false);

        session.CacheMessage(sent);
        return (long)sent.Sequence;
    }

    public async Task<QqIncomingMessage?> GetMessageAsync(QqMessageScene scene, long peerId, long messageSeq)
    {
        var messages = scene == QqMessageScene.Group
            ? await session.Context.GetGroupMessage(peerId, (ulong)messageSeq, (ulong)messageSeq).ConfigureAwait(false)
            : await session.Context.GetC2CMessage(peerId, (ulong)messageSeq, (ulong)messageSeq).ConfigureAwait(false);

        var message = messages.FirstOrDefault();
        return message is null ? null : LagrangeMapper.ToMessageEvent(message).Raw as QqIncomingMessage;
    }

    public async Task<(IReadOnlyList<QqIncomingMessage> Messages, long? NextMessageSeq)> GetHistoryMessagesAsync(
        QqMessageScene scene, long peerId, long? startMessageSeq = null, int limit = 20)
    {
        ulong endSequence;
        if (startMessageSeq is { } start)
        {
            endSequence = (ulong)start;
        }
        else if (scene == QqMessageScene.Group)
        {
            var extra = await session.Context.FetchGroupExtra(peerId).ConfigureAwait(false);
            endSequence = (ulong)extra.LatestMessageSequence;
        }
        else
        {
            throw LagrangeMapper.Unsupported("私聊历史消息需要指定 startMessageSeq");
        }

        var startSequence = endSequence > (ulong)limit ? endSequence - (ulong)limit : 1;
        var messages = scene == QqMessageScene.Group
            ? await session.Context.GetGroupMessage(peerId, startSequence, endSequence).ConfigureAwait(false)
            : await session.Context.GetC2CMessage(peerId, startSequence, endSequence).ConfigureAwait(false);

        var converted = messages
            .Select(message => LagrangeMapper.ToMessageEvent(message).Raw)
            .OfType<QqIncomingMessage>()
            .ToArray();

        var next = startSequence > 1 ? (long?)(long)(startSequence - 1) : null;
        return (converted, next);
    }

    public async Task RecallMessageAsync(QqMessageScene scene, long peerId, long messageSeq)
    {
        var message = session.FindMessage((ulong)messageSeq)
                      ?? throw LagrangeMapper.Unsupported($"撤回未缓存的消息(seq={messageSeq},仅支持近期消息)");
        await session.Context.RecallMessage(message).ConfigureAwait(false);
    }

    public Task<string> GetResourceTempUrlAsync(string resourceId) =>
        session.Context.GetNTV2RichMediaUrl(resourceId);

    // 不支持项:
    public Task<IReadOnlyList<QqForwardedIncomingMessage>> GetForwardedMessagesAsync(string forwardId) =>
        throw LagrangeMapper.Unsupported("按 resId 拉取合并转发内容(Lagrange.Core 未提供 API)");

    public Task MarkAsReadAsync(QqMessageScene scene, long peerId, long messageSeq) =>
        throw LagrangeMapper.Unsupported("标记已读(Lagrange.Core 未提供 API)");
}
