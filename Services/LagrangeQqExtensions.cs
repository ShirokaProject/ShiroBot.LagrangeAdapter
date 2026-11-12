using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using ShiroBot.Model.QQ;

namespace ShiroBot.LagrangeAdapter.Services;

/// <summary>
/// IQq*Api 的 Lagrange 实现。Lagrange.Core 尚未提供的能力保留接口默认实现
/// (NotSupportedException),插件通过能力探测降级。
/// </summary>
internal sealed class LagrangeQqFriendApi(LagrangeSession session) : IQFriendApi
{
    public Task SendNudgeAsync(string userId, bool isSelf = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = LagrangeMapper.ParseId(userId, nameof(userId));
        return session.Context.SendFriendNudge(peer, isSelf ? session.Context.BotUin : peer).WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QFriendRequest>> GetFriendRequestsAsync(int limit = 20, bool isFiltered = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (isFiltered) throw LagrangeMapper.Unsupported("过滤好友请求列表，上游没有过滤参数");
        var requests = await session.Context.FetchFriendRequests().WaitAsync(cancellationToken);
        var result = new List<QFriendRequest>();
        foreach (var request in requests.OrderByDescending(x => x.Time).Take(limit))
        {
            var initiator = await session.Context.FetchStranger(request.SourceUin).WaitAsync(cancellationToken);
            result.Add(new QFriendRequest
            {
                InitiatorId = request.SourceUin.ToString(),
                InitiatorUid = initiator.Uid,
                TargetUserId = request.TargetUin.ToString(),
                Time = DateTimeOffset.FromUnixTimeSeconds(request.Time),
                Comment = request.Comment,
                Via = request.Source,
                State = request.EventState switch
                {
                    BotFriendRequest.State.Approved => QRequestState.Accepted,
                    BotFriendRequest.State.Disapproved => QRequestState.Rejected,
                    _ => QRequestState.Pending
                }
            });
        }
        return result;
    }

    public Task SendProfileLikeAsync(string userId, int count = 1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("名片点赞(Lagrange.Core 未提供 API)");
    }

    public Task DeleteFriendAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("删除好友(Lagrange.Core 未提供 API)");
    }

    // 好友请求列表/处理:Lagrange.Core 只有事件,无处理 API — 走接口默认 NotSupported
}

internal sealed class LagrangeQqGroupApi(LagrangeSession session) : IQGroupApi
{
    public Task<IReadOnlyList<QGroup>> GetGroupListAsync(bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (new LagrangeQqSystemApi(session).GetGroupListAsync(noCache, cancellationToken)).WaitAsync(cancellationToken);
    }

    public Task<QGroup> GetGroupInfoAsync(string groupId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (new LagrangeQqSystemApi(session).GetGroupInfoAsync(groupId, noCache, cancellationToken)).WaitAsync(cancellationToken);
    }

    public Task<IReadOnlyList<QGroupMember>> GetGroupMemberListAsync(string groupId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (new LagrangeQqSystemApi(session).GetGroupMemberListAsync(groupId, noCache, cancellationToken)).WaitAsync(cancellationToken);
    }

    public Task<QGroupMember> GetGroupMemberInfoAsync(string groupId, string userId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (new LagrangeQqSystemApi(session).GetGroupMemberInfoAsync(groupId, userId, noCache, cancellationToken)).WaitAsync(cancellationToken);
    }

    public async Task<QGroupJoinRequestPage> GetJoinRequestsAsync(string groupId, string? cursor = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var notifications = await session.Context.FetchGroupNotifications((ulong)limit, cursor is null ? 0 : ulong.Parse(cursor)).WaitAsync(cancellationToken);
        var requests = notifications.Where(x => x.GroupUin == LagrangeMapper.ParseId(groupId, nameof(groupId)))
            .Select(LagrangeMapper.ToJoinRequest).OfType<QGroupJoinRequest>().ToArray();
        return new QGroupJoinRequestPage(requests, notifications.Count == 0 ? null : notifications[^1].Sequence.ToString());
    }

    public async Task<QBatchOperationResult> SetMemberMutesAsync(string groupId, IReadOnlyList<QMemberMute> members, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<QOperationResult>();
        for (var index = 0; index < members.Count; index++)
        {
            var member = members[index];
            var started = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                started = true;
                await MuteMemberAsync(groupId, member.UserId, member.Duration, cancellationToken);
                results.Add(new QOperationResult { UserId = member.UserId, Status = QOperationStatus.Succeeded });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new QOperationResult { UserId = member.UserId, Status = started ? QOperationStatus.Unknown : QOperationStatus.NotExecuted });
                results.AddRange(members.Skip(index + 1).Select(x => new QOperationResult { UserId = x.UserId, Status = QOperationStatus.NotExecuted }));
                throw new QBatchOperationCanceledException(new QBatchOperationResult { Items = results }, cancellationToken);
            }
            catch (Exception ex)
            {
                results.Add(new QOperationResult { UserId = member.UserId, Status = QOperationStatus.Failed, ErrorMessage = ex.Message });
            }
        }
        return new QBatchOperationResult { Items = results };
    }

    public Task SetGroupNameAsync(string groupId, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GroupRename(LagrangeMapper.ParseId(groupId, "groupId"), name)).WaitAsync(cancellationToken);
    }

    public Task SetMemberCardAsync(string groupId, string userId, string card, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GroupMemberRename(LagrangeMapper.ParseId(groupId, "groupId"), LagrangeMapper.ParseId(userId, "userId"), card)).WaitAsync(cancellationToken);
    }

    public Task SetMemberSpecialTitleAsync(string groupId, string userId, string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GroupSetSpecialTitle(LagrangeMapper.ParseId(groupId, "groupId"), LagrangeMapper.ParseId(userId, "userId"), title)).WaitAsync(cancellationToken);
    }

    public Task QuitGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GroupQuit(LagrangeMapper.ParseId(groupId, "groupId"))).WaitAsync(cancellationToken);
    }

    public Task SendNudgeAsync(string groupId, string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.SendGroupNudge(LagrangeMapper.ParseId(groupId, "groupId"), LagrangeMapper.ParseId(userId, "userId"))).WaitAsync(cancellationToken);
    }

    public Task SendMessageReactionAsync(string groupId, string messageId, string faceId, bool isAdd = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.SetGroupReaction(LagrangeMapper.ParseId(groupId, "groupId"), ulong.Parse(messageId), faceId, isAdd)).WaitAsync(cancellationToken);
    }

    public Task SendMessageReactionAsync(string groupId, string messageId, string reactionId, QReactionType reactionType, bool isAdd = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (SendMessageReactionAsync(groupId, messageId, reactionId, isAdd)).WaitAsync(cancellationToken);
    }

    public Task AcceptJoinRequestAsync(QGroupJoinRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.SetGroupNotification(
            LagrangeMapper.ParseId(request.GroupId, "GroupId"),
            ulong.Parse(request.RequestId),
            request.IsInvited ? BotGroupNotificationType.Invite : BotGroupNotificationType.Join,
            request.IsFiltered,
            GroupNotificationOperate.Allow)).WaitAsync(cancellationToken);
    }

    public Task RejectJoinRequestAsync(QGroupJoinRequest request, string? reason = null, bool addToBlacklist = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (addToBlacklist) throw LagrangeMapper.Unsupported("拒绝申请并拉黑，上游没有拉黑参数");
        return (session.Context.SetGroupNotification(
            LagrangeMapper.ParseId(request.GroupId, "GroupId"),
            ulong.Parse(request.RequestId),
            request.IsInvited ? BotGroupNotificationType.Invite : BotGroupNotificationType.Join,
            request.IsFiltered,
            GroupNotificationOperate.Deny,
            reason ?? string.Empty)).WaitAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<QGroupNotification> Notifications, string? NextCursor)> GetNotificationsAsync(
        string? cursor = null, bool isFiltered = false, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notifications = isFiltered
            ? await session.Context.FetchFilteredGroupNotifications((ulong)limit, (cursor is null ? 0 : ulong.Parse(cursor))).ConfigureAwait(false)
            : await session.Context.FetchGroupNotifications((ulong)limit, (cursor is null ? 0 : ulong.Parse(cursor))).ConfigureAwait(false);

        var converted = notifications
            .Select(ToQqNotification)
            .Where(notification => notification is not null)
            .Cast<QGroupNotification>()
            .ToArray();

        var next = notifications.Count > 0 ? notifications[^1].Sequence.ToString() : null;
        return (converted, next);
    }

    private static QGroupNotification? ToQqNotification(BotGroupNotificationBase notification) => notification switch
    {
        BotGroupJoinNotification join => new QJoinRequestNotification
        {
            GroupId = (join.GroupUin).ToString(),
            NotificationId = ((long)join.Sequence).ToString(),
            InitiatorId = (join.TargetUin).ToString(),
            State = ToQqState(join.State),
            Comment = join.Comment,
            IsFiltered = join.IsFiltered,
            OperatorId = (join.OperatorUin).ToString()
        },
        BotGroupInviteNotification invite => new QInvitedJoinRequestNotification
        {
            GroupId = (invite.GroupUin).ToString(),
            NotificationId = ((long)invite.Sequence).ToString(),
            InitiatorId = (invite.InviterUin).ToString(),
            TargetUserId = (invite.TargetUin).ToString(),
            State = ToQqState(invite.State),
            OperatorId = (invite.OperatorUin).ToString()
        },
        BotGroupSetAdminNotification admin => new QAdminChangeNotification
        {
            GroupId = (admin.GroupUin).ToString(),
            NotificationId = ((long)admin.Sequence).ToString(),
            TargetUserId = (admin.TargetUin).ToString(),
            IsSet = true,
            OperatorId = (admin.OperatorUin).ToString()
        },
        BotGroupUnsetAdminNotification unset => new QAdminChangeNotification
        {
            GroupId = (unset.GroupUin).ToString(),
            NotificationId = ((long)unset.Sequence).ToString(),
            TargetUserId = (unset.TargetUin).ToString(),
            IsSet = false,
            OperatorId = (unset.OperatorUin).ToString()
        },
        BotGroupKickNotification kick => new QKickNotification
        {
            GroupId = (kick.GroupUin).ToString(),
            NotificationId = ((long)kick.Sequence).ToString(),
            TargetUserId = (kick.TargetUin).ToString(),
            OperatorId = (kick.OperatorUin).ToString()
        },
        BotGroupExitNotification exit => new QQuitNotification
        {
            GroupId = (exit.GroupUin).ToString(),
            NotificationId = ((long)exit.Sequence).ToString(),
            TargetUserId = (exit.TargetUin).ToString()
        },
        _ => null
    };

    private static QRequestState ToQqState(BotGroupNotificationState state) => state switch
    {
        BotGroupNotificationState.Accept => QRequestState.Accepted,
        BotGroupNotificationState.Reject => QRequestState.Rejected,
        _ => QRequestState.Pending
    };

    // 不支持项(Lagrange.Core 未提供 API):
    public Task SetGroupAvatarAsync(string groupId, string imageUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("设置群头像(Lagrange.Core 未提供 API)");
    }

    public Task SetMemberAdminAsync(string groupId, string userId, bool isSet = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("设置群管理员(Lagrange.Core 未提供 API)");
    }

    public Task MuteMemberAsync(string groupId, string userId, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LagrangeOperations.RequireSuccessAsync(session.Context.MuteGroupMember(
            LagrangeMapper.ParseId(groupId, nameof(groupId)), LagrangeMapper.ParseId(userId, nameof(userId)),
            LagrangeOperations.MuteSeconds(duration)), "群成员禁言", cancellationToken);
    }

    public Task SetWholeMuteAsync(string groupId, bool isMute = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LagrangeOperations.RequireSuccessAsync(session.Context.MuteGroupGlobal(
            LagrangeMapper.ParseId(groupId, nameof(groupId)), isMute), "全员禁言", cancellationToken);
    }

    public Task KickMemberAsync(string groupId, string userId, bool rejectAddRequest = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LagrangeOperations.RequireSuccessAsync(session.Context.KickGroupMember(
            LagrangeMapper.ParseId(groupId, nameof(groupId)), LagrangeMapper.ParseId(userId, nameof(userId)),
            rejectAddRequest), "踢出群成员", cancellationToken);
    }

    public Task<IReadOnlyList<QGroupAnnouncement>> GetAnnouncementsAsync(string groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("群公告(Lagrange.Core 未提供 API)");
    }

    public Task SendAnnouncementAsync(string groupId, string content, string? imageUri = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("群公告(Lagrange.Core 未提供 API)");
    }

    public Task DeleteAnnouncementAsync(string groupId, string announcementId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("群公告(Lagrange.Core 未提供 API)");
    }

    public Task<IReadOnlyList<QEssenceMessage>> GetEssenceMessagesAsync(string groupId, int pageIndex, int pageSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("精华消息(Lagrange.Core 未提供 API)");
    }

    public async Task SetEssenceMessageAsync(string groupId, string messageId, bool isSet = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = await session.ResolveMessageAsync(messageId, LagrangeMapper.ParseId(groupId, nameof(groupId)), true, cancellationToken)
            ?? throw new InvalidOperationException("找不到精华消息。");
        await (isSet ? session.Context.SetEssenceMessage(message) : session.Context.RemoveEssenceMessage(message))
            .WaitAsync(cancellationToken);
    }

    public Task AcceptInvitationAsync(string groupId, string invitationSeq, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("处理他人邀请自身入群(Lagrange.Core 未提供 API)");
    }

    public Task RejectInvitationAsync(string groupId, string invitationSeq, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("处理他人邀请自身入群(Lagrange.Core 未提供 API)");
    }
}

internal sealed class LagrangeQqFileApi(LagrangeSession session) : IQFileApi
{
    public async Task<string> UploadPrivateFileAsync(string userId, string fileUri, string fileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await LagrangeMapper.OpenResourceAsync(fileUri).ConfigureAwait(false);
        await session.Context.SendFriendFile(LagrangeMapper.ParseId(userId, "userId"), stream, fileName).ConfigureAwait(false);
        return string.Empty; // 好友文件无 fileId
    }

    public async Task<string> UploadGroupFileAsync(string groupId, string fileUri, string fileName, string parentFolderId = "/", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await LagrangeMapper.OpenResourceAsync(fileUri).ConfigureAwait(false);
        return await session.Context.SendGroupFile(LagrangeMapper.ParseId(groupId, "groupId"), stream, fileName, parentFolderId).ConfigureAwait(false);
    }

    public Task<string> GetGroupFileDownloadUrlAsync(string groupId, string fileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GroupFSDownload(LagrangeMapper.ParseId(groupId, "groupId"), fileId)).WaitAsync(cancellationToken);
    }

    public Task MoveGroupFileAsync(string groupId, string fileId, string targetFolderId, string parentFolderId = "/", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GroupFSMove(LagrangeMapper.ParseId(groupId, "groupId"), fileId, targetFolderId, parentFolderId)).WaitAsync(cancellationToken);
    }

    public Task DeleteGroupFileAsync(string groupId, string fileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GroupFSDelete(LagrangeMapper.ParseId(groupId, "groupId"), fileId)).WaitAsync(cancellationToken);
    }

    // 不支持项:
    public Task<string> GetPrivateFileDownloadUrlAsync(string userId, string fileId, string fileHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("私聊文件下载链接(Lagrange.Core 未提供 API)");
    }

    public async Task<(IReadOnlyList<QGroupFile> Files, IReadOnlyList<QGroupFolder> Folders)> GetGroupFilesAsync(string groupId, string parentFolderId = "/", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await session.Context.FetchGroupFSList(LagrangeMapper.ParseId(groupId, nameof(groupId)), parentFolderId)
            .WaitAsync(cancellationToken);
        return (entries.OfType<BotFileEntry>().Select(x => LagrangeMapper.ToQq(x, groupId)).ToArray(),
            entries.OfType<BotFolderEntry>().Select(x => LagrangeMapper.ToQq(x, groupId)).ToArray());
    }

    public Task RenameGroupFileAsync(string groupId, string fileId, string newFileName, string parentFolderId = "/", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("重命名群文件(Lagrange.Core 未提供 API)");
    }

    public async Task<string> CreateGroupFolderAsync(string groupId, string folderName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = LagrangeMapper.ParseId(groupId, nameof(groupId));
        var before = (await session.Context.FetchGroupFSList(peer).WaitAsync(cancellationToken))
            .OfType<BotFolderEntry>().Select(x => x.FolderId).ToHashSet();
        await session.Context.GroupFSCreateFolder(peer, folderName).WaitAsync(cancellationToken);
        var created = (await session.Context.FetchGroupFSList(peer).WaitAsync(cancellationToken))
            .OfType<BotFolderEntry>().Where(x => x.FolderName == folderName && !before.Contains(x.FolderId)).ToArray();
        return created.Length == 1 ? created[0].FolderId
            : throw new InvalidOperationException("创建操作已完成，但无法唯一确定新文件夹 ID，请刷新群文件列表。");
    }

    public Task RenameGroupFolderAsync(string groupId, string folderId, string newFolderName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupFSRenameFolder(LagrangeMapper.ParseId(groupId, nameof(groupId)), folderId, newFolderName)
            .WaitAsync(cancellationToken);
    }

    public Task DeleteGroupFolderAsync(string groupId, string folderId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupFSDeleteFolder(LagrangeMapper.ParseId(groupId, nameof(groupId)), folderId)
            .WaitAsync(cancellationToken);
    }

    public Task PersistGroupFileAsync(string groupId, string fileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("群文件转永久(Lagrange.Core 未提供 API)");
    }
}

internal sealed class LagrangeQqSystemApi(LagrangeSession session) : IQSystemApi
{
    public async Task<QUserProfile> GetUserProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stranger = await session.Context.FetchStranger(LagrangeMapper.ParseId(userId, "userId")).ConfigureAwait(false);
        return new QUserProfile
        {
            UserId = (stranger.Uin).ToString(),
            Nickname = stranger.Nickname,
            Qid = string.IsNullOrEmpty(stranger.QID) ? null : stranger.QID,
            Age = (int)stranger.Age,
            Sex = stranger.Gender switch
            {
                BotGender.Male => QSex.Male,
                BotGender.Female => QSex.Female,
                _ => QSex.Unknown
            },
            Remark = string.IsNullOrEmpty(stranger.Remark) ? null : stranger.Remark,
            Bio = string.IsNullOrEmpty(stranger.PersonalSign) ? null : stranger.PersonalSign,
            Level = (int)stranger.Level,
            Country = string.IsNullOrEmpty(stranger.Country) ? null : stranger.Country,
            City = string.IsNullOrEmpty(stranger.City) ? null : stranger.City,
            School = stranger.School
        };
    }

    public async Task<IReadOnlyList<QFriend>> GetFriendListAsync(bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var friends = await session.Context.FetchFriends(noCache).ConfigureAwait(false);
        return friends.Select(LagrangeMapper.ToQq).ToArray();
    }

    public async Task<QFriend> GetFriendInfoAsync(string userId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var friends = await GetFriendListAsync(noCache).ConfigureAwait(false);
        return friends.FirstOrDefault(friend => friend.UserId == userId)
               ?? throw new InvalidOperationException($"好友 {userId} 不存在。");
    }

    internal async Task<IReadOnlyList<QGroup>> GetGroupListAsync(bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = await session.Context.FetchGroups(noCache).ConfigureAwait(false);
        return groups.Select(LagrangeMapper.ToQq).ToArray();
    }

    internal async Task<QGroup> GetGroupInfoAsync(string groupId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = await GetGroupListAsync(noCache).ConfigureAwait(false);
        return groups.FirstOrDefault(group => group.GroupId == groupId)
               ?? throw new InvalidOperationException($"群 {groupId} 不存在。");
    }

    internal async Task<IReadOnlyList<QGroupMember>> GetGroupMemberListAsync(string groupId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = await session.Context.FetchMembers(LagrangeMapper.ParseId(groupId, "groupId"), noCache).ConfigureAwait(false);
        return members.Select(LagrangeMapper.ToQq).ToArray();
    }

    internal async Task<QGroupMember> GetGroupMemberInfoAsync(string groupId, string userId, bool noCache = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = await GetGroupMemberListAsync(groupId, noCache).ConfigureAwait(false);
        return members.FirstOrDefault(member => member.UserId == userId)
               ?? throw new InvalidOperationException($"群成员 {groupId}/{userId} 不存在。");
    }

    public async Task SetAvatarAsync(string imageUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await LagrangeMapper.OpenResourceAsync(imageUri).ConfigureAwait(false);
        await LagrangeOperations.RequireSuccessAsync(session.Context.SetBotAvatar(stream), "设置头像", cancellationToken);
    }

    public async Task<string> GetCookiesAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cookies = await session.Context.FetchCookies([domain]).ConfigureAwait(false);
        return cookies.GetValueOrDefault(domain, string.Empty);
    }

    async Task<QLoginInfo> IQSystemApi.GetLoginInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = session.Context;
        return await Task.FromResult(new QLoginInfo(context.BotUin.ToString(), context.BotInfo?.Name ?? string.Empty));
    }

    async Task<QImplInfo> IQSystemApi.GetImplInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = session.Context;
        return await Task.FromResult(new QImplInfo
        {
            ImplName = "Lagrange.Core",
            ImplVersion = typeof(Lagrange.Core.BotContext).Assembly.GetName().Version?.ToString() ?? "unknown",
            QqProtocolType = context.Config.Protocol.ToString().ToLowerInvariant()
        });
    }

    // 不支持项:
    public Task SetPeerPinAsync(QMessageScene scene, string peerId, bool isPinned = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = LagrangeMapper.ParseId(peerId, nameof(peerId));
        if (scene == QMessageScene.Temp) throw LagrangeMapper.Unsupported("临时会话置顶");
        return (scene == QMessageScene.Group ? session.Context.SetPinGroup(peer, isPinned)
            : session.Context.SetPinFriend(peer, isPinned)).WaitAsync(cancellationToken);
    }

    public Task SetNicknameAsync(string nickname, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("设置昵称(Lagrange.Core 未提供 API)");
    }

    public Task SetBioAsync(string bio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("设置个性签名(Lagrange.Core 未提供 API)");
    }

    public Task<string> GetCsrfTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("CSRF Token(可用 GetCookiesAsync 自行计算 bkn)");
    }
}

internal sealed class LagrangeQqMessageApi(LagrangeSession session) : IQMessageApi
{
    public async Task<string> SendMessageAsync(QMessageScene scene, string peerId, IReadOnlyList<QOutgoingSegment> segments, CancellationToken cancellationToken = default) =>
        (await SendMessageDetailedAsync(scene, peerId, segments, cancellationToken)).MessageId;

    public async Task<QSentMessage> SendMessageDetailedAsync(QMessageScene scene, string peerId, IReadOnlyList<QOutgoingSegment> segments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chain = new Lagrange.Core.Message.MessageChain();
        foreach (var segment in segments)
        {
            await LagrangeMapper.AppendQqSegmentAsync(chain, segment, id => session.ResolveMessageAsync(id, LagrangeMapper.ParseId(peerId, "peerId"), scene == QMessageScene.Group)).ConfigureAwait(false);
        }

        var sent = scene == QMessageScene.Group
            ? await session.Context.SendGroupMessage(LagrangeMapper.ParseId(peerId, "peerId"), chain).ConfigureAwait(false)
            : await session.Context.SendFriendMessage(LagrangeMapper.ParseId(peerId, "peerId"), chain).ConfigureAwait(false);

        session.CacheMessage(sent);
        return new QSentMessage(sent.Sequence.ToString(), DateTimeOffset.FromUnixTimeSeconds(sent.Time));
    }

    public async Task<QIncomingMessage?> GetMessageAsync(QMessageScene scene, string peerId, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var messages = scene == QMessageScene.Group
            ? await session.Context.GetGroupMessage(LagrangeMapper.ParseId(peerId, "peerId"), ulong.Parse(messageId), ulong.Parse(messageId)).ConfigureAwait(false)
            : await session.Context.GetC2CMessage(LagrangeMapper.ParseId(peerId, "peerId"), ulong.Parse(messageId), ulong.Parse(messageId)).ConfigureAwait(false);

        var message = messages.FirstOrDefault();
        return message is null ? null : LagrangeMapper.ToMessageEvent(message, session.Context.BotUin.ToString()).Raw as QIncomingMessage;
    }

    public async Task<(IReadOnlyList<QIncomingMessage> Messages, string? NextCursor)> GetHistoryMessagesAsync(
        QMessageScene scene, string peerId, string? cursor = null, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (scene == QMessageScene.Friend && cursor is null)
        {
            var recent = await session.Context.GetRoamMessage(LagrangeMapper.ParseId(peerId, nameof(peerId)),
                checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), (uint)limit).WaitAsync(cancellationToken);
            foreach (var item in recent) session.CacheMessage(item);
            var first = recent.Count == 0 ? 0 : recent.Min(x => x.Sequence);
            return (recent.Select(x => (QIncomingMessage)LagrangeMapper.ToMessageEvent(x, session.Context.BotUin.ToString()).Raw!).ToArray(),
                first > 1 ? (first - 1).ToString() : null);
        }
        ulong endSequence;
        if (cursor is { } start)
        {
            endSequence = ulong.Parse(start);
        }
        else if (scene == QMessageScene.Group)
        {
            var extra = await session.Context.FetchGroupExtra(LagrangeMapper.ParseId(peerId, "peerId")).ConfigureAwait(false);
            endSequence = (ulong)extra.LatestMessageSequence;
        }
        else
        {
            throw LagrangeMapper.Unsupported("私聊历史消息需要指定 startMessageId");
        }

        var startSequence = endSequence > (ulong)limit ? endSequence - (ulong)limit + 1 : 1;
        var messages = scene == QMessageScene.Group
            ? await session.Context.GetGroupMessage(LagrangeMapper.ParseId(peerId, "peerId"), startSequence, endSequence).ConfigureAwait(false)
            : await session.Context.GetC2CMessage(LagrangeMapper.ParseId(peerId, "peerId"), startSequence, endSequence).ConfigureAwait(false);

        foreach (var item in messages) session.CacheMessage(item);
        var converted = messages
            .Select(message => LagrangeMapper.ToMessageEvent(message, session.Context.BotUin.ToString()).Raw)
            .OfType<QIncomingMessage>()
            .ToArray();

        var next = startSequence > 1 ? (startSequence - 1).ToString() : null;
        return (converted, next);
    }

    public async Task RecallMessageAsync(QMessageScene scene, string peerId, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = await session.ResolveMessageAsync(messageId, LagrangeMapper.ParseId(peerId, "peerId"), scene == QMessageScene.Group, cancellationToken)
                      ?? throw new InvalidOperationException($"找不到要撤回的消息: {messageId}");
        await session.Context.RecallMessage(message).ConfigureAwait(false);
    }

    public Task<string> GetResourceTempUrlAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (session.Context.GetNTV2RichMediaUrl(resourceId)).WaitAsync(cancellationToken);
    }

    // 不支持项:
    public async Task<IReadOnlyList<QForwardedIncomingMessage>> GetForwardedMessagesAsync(string forwardId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = new Lagrange.Core.Message.Entities.MultiMsgEntity(forwardId);
        var envelope = Lagrange.Core.Message.BotMessage.CreateCustomGroup(0, 0, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), []);
        await entity.Postprocess(session.Context, envelope).WaitAsync(cancellationToken);
        return entity.Messages.Select(x => new QForwardedIncomingMessage
        {
            MessageId = x.Sequence.ToString(),
            SenderName = x.Contact.Nickname,
            Time = DateTimeOffset.FromUnixTimeSeconds(x.Time),
            Segments = LagrangeMapper.ToQqSegments(x.Entities)
        }).ToArray();
    }

    public Task MarkAsReadAsync(QMessageScene scene, string peerId, string messageId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("标记已读(Lagrange.Core 未提供 API)");
    }
}
