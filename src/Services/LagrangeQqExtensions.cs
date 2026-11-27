using Lagrange.Core.Common;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using ShiroBot.Model.QQ;

namespace ShiroBot.LagrangeAdapter.Services;

/// <summary>
/// QQ SDK 实现，复用 Lagrange.Core 并补充 Yogurt 对应的协议与 Web 接口。
/// SDK 额外策略能力未实现时保留明确的 NotSupportedException。
/// </summary>
internal sealed class LagrangeQqFriendApi(LagrangeSession session) : IQFriendApi
{
    public Task SendNudgeAsync(string userId, bool isSelf = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = LagrangeMapper.ParseId(userId, nameof(userId));
        return session.Context.SendFriendNudge(peer, isSelf ? session.Context.BotUin : peer)
            .WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QFriendRequest>> GetFriendRequestsAsync(int limit = 20, bool isFiltered = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (isFiltered)
        {
            var data = await new LagrangeProtocol(session.Context).Oidb(0xd69, 0,
                new ProtoWire().UInt(1, 1).Message(2, new ProtoWire().UInt(1, (ulong)limit)), cancellationToken);
            var filtered = new List<QFriendRequest>();
            foreach (var raw in ProtoWire.Child(data, 2).Where(f => f.Number == 1 && f.Wire == 2).Take(limit))
            {
                var item = ProtoWire.Parse(raw.Bytes); var uid = ProtoWire.String(item, 1);
                var user = await session.Context.FetchStrangerByUid(uid).WaitAsync(cancellationToken);
                filtered.Add(new QFriendRequest { InitiatorId = user.Uin.ToString(), InitiatorUid = uid,
                    TargetUserId = session.Context.BotUin.ToString(), Time = DateTimeOffset.FromUnixTimeSeconds((long)ProtoWire.Number(item, 8)),
                    Comment = ProtoWire.String(item, 5), Via = ProtoWire.String(item, 6), State = QRequestState.Pending, IsFiltered = true });
            }
            return filtered;
        }
        var dataNormal = await new LagrangeProtocol(session.Context).Oidb(0x5cf, 11,
            new ProtoWire().UInt(1, 1).UInt(3, 6).Text(4, session.Context.Keystore.Uid).UInt(5, 0)
                .UInt(6, (ulong)limit).UInt(8, 2).UInt(9, 0).UInt(12, 1).UInt(22, 1), cancellationToken);
        var result = new List<QFriendRequest>();
        foreach (var raw in ProtoWire.Child(dataNormal, 3).Where(f => f.Number == 7 && f.Wire == 2).Take(limit))
        {
            var item = ProtoWire.Parse(raw.Bytes); var sourceUid = ProtoWire.String(item, 2); var targetUid = ProtoWire.String(item, 1);
            var source = await session.Context.FetchStrangerByUid(sourceUid).WaitAsync(cancellationToken);
            var target = targetUid == session.Context.Keystore.Uid ? session.Context.BotUin :
                (await session.Context.FetchStrangerByUid(targetUid).WaitAsync(cancellationToken)).Uin;
            result.Add(new QFriendRequest { InitiatorId = source.Uin.ToString(), InitiatorUid = sourceUid,
                TargetUserId = target.ToString(), Time = DateTimeOffset.FromUnixTimeSeconds((long)ProtoWire.Number(item, 4)),
                Comment = ProtoWire.String(item, 5), Via = ProtoWire.String(item, 6),
                State = ProtoWire.Number(item, 3) switch { 3 => QRequestState.Accepted, 7 => QRequestState.Rejected, _ => QRequestState.Pending } });
        }

        return result;
    }

    public async Task SendProfileLikeAsync(string userId, int count = 1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(count));
        var context = session.Context;
        var uid = await LagrangeProtocol.ResolveContactUidAsync(context, LagrangeMapper.ParseId(userId, nameof(userId)), cancellationToken);
        await new LagrangeProtocol(context).Like(uid, count, cancellationToken);
    }

    public async Task DeleteFriendAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uid = await LagrangeProtocol.Uid(session.Context, LagrangeMapper.ParseId(userId, nameof(userId)), cancellationToken);
        await new LagrangeProtocol(session.Context).DeleteFriend(uid, cancellationToken);
    }

    public Task AcceptFriendRequestAsync(string initiatorUid, bool isFiltered = false, CancellationToken cancellationToken = default) =>
        HandleRequest(initiatorUid, true, isFiltered, null, cancellationToken);
    public Task RejectFriendRequestAsync(string initiatorUid, bool isFiltered = false, string? reason = null, CancellationToken cancellationToken = default) =>
        HandleRequest(initiatorUid, false, isFiltered, reason, cancellationToken);
    private async Task HandleRequest(string uid, bool accept, bool filtered, string? reason, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);
        if (!string.IsNullOrEmpty(reason)) throw new ArgumentException("腾讯好友审批接口不支持拒绝原因。", nameof(reason));
        // Match Yogurt: filtered requests are accepted explicitly; rejecting one is a no-op.
        if (filtered && !accept) return;
        await new LagrangeProtocol(session.Context).FriendRequest(session.Context.Keystore.Uid, uid, accept, filtered, token);
    }

}

internal sealed class LagrangeQqGroupApi(LagrangeSession session) : IQGroupApi
{
    public QGroupCapabilities Capabilities => QGroupCapabilities.Rename | QGroupCapabilities.Avatar |
        QGroupCapabilities.MemberCard | QGroupCapabilities.MemberTitle | QGroupCapabilities.MemberAdmin |
        QGroupCapabilities.MemberMute | QGroupCapabilities.WholeMute | QGroupCapabilities.Kick |
        QGroupCapabilities.Quit | QGroupCapabilities.Nudge | QGroupCapabilities.Reaction |
        QGroupCapabilities.Announcement | QGroupCapabilities.Essence | QGroupCapabilities.JoinRequests |
        QGroupCapabilities.Notifications | QGroupCapabilities.Invitations | QGroupCapabilities.BatchMute |
        QGroupCapabilities.GroupList | QGroupCapabilities.GroupInfo | QGroupCapabilities.Members;
    public Task<IReadOnlyList<QGroup>> GetGroupListAsync(bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeQqSystemApi(session).GetGroupListAsync(noCache, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    public Task<QGroup> GetGroupInfoAsync(string groupId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeQqSystemApi(session).GetGroupInfoAsync(groupId, noCache, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    public Task<IReadOnlyList<QGroupMember>> GetGroupMemberListAsync(string groupId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeQqSystemApi(session).GetGroupMemberListAsync(groupId, noCache, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    public Task<QGroupMember> GetGroupMemberInfoAsync(string groupId, string userId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeQqSystemApi(session).GetGroupMemberInfoAsync(groupId, userId, noCache, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    public async Task<QGroupJoinRequestPage> GetJoinRequestsAsync(string groupId, string? cursor = null, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var notifications = await session.Context
            .FetchGroupNotifications((ulong)limit, cursor is null ? 0 : ulong.Parse(cursor))
            .WaitAsync(cancellationToken);
        var requests = notifications.Where(x => x.GroupUin == LagrangeMapper.ParseId(groupId, nameof(groupId)))
            .Select(LagrangeMapper.ToJoinRequest).OfType<QGroupJoinRequest>().ToArray();
        return new QGroupJoinRequestPage(requests,
            notifications.Count == 0 ? null : notifications[^1].Sequence.ToString());
    }

    public async Task<QBatchOperationResult> SetMemberMutesAsync(string groupId, IReadOnlyList<QMemberMute> members,
        CancellationToken cancellationToken = default)
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
                results.Add(new QOperationResult
                {
                    UserId = member.UserId, Status = started ? QOperationStatus.Unknown : QOperationStatus.NotExecuted
                });
                results.AddRange(members.Skip(index + 1).Select(x => new QOperationResult
                    { UserId = x.UserId, Status = QOperationStatus.NotExecuted }));
                throw new QBatchOperationCanceledException(new QBatchOperationResult { Items = results },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                results.Add(new QOperationResult
                    { UserId = member.UserId, Status = QOperationStatus.Failed, ErrorMessage = ex.Message });
            }
        }

        return new QBatchOperationResult { Items = results };
    }

    public Task SetGroupNameAsync(string groupId, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupRename(LagrangeMapper.ParseId(groupId, "groupId"), name)
            .WaitAsync(cancellationToken);
    }

    public Task SetMemberCardAsync(string groupId, string userId, string card,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context
            .GroupMemberRename(LagrangeMapper.ParseId(groupId, "groupId"), LagrangeMapper.ParseId(userId, "userId"),
                card).WaitAsync(cancellationToken);
    }

    public Task SetMemberSpecialTitleAsync(string groupId, string userId, string title,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context
            .GroupSetSpecialTitle(LagrangeMapper.ParseId(groupId, "groupId"), LagrangeMapper.ParseId(userId, "userId"),
                title).WaitAsync(cancellationToken);
    }

    public Task QuitGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupQuit(LagrangeMapper.ParseId(groupId, "groupId")).WaitAsync(cancellationToken);
    }

    public Task SendNudgeAsync(string groupId, string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context
            .SendGroupNudge(LagrangeMapper.ParseId(groupId, "groupId"), LagrangeMapper.ParseId(userId, "userId"))
            .WaitAsync(cancellationToken);
    }

    public Task SendMessageReactionAsync(string groupId, string messageId, string faceId, bool isAdd = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context
            .SetGroupReaction(LagrangeMapper.ParseId(groupId, "groupId"), ulong.Parse(messageId), faceId, isAdd)
            .WaitAsync(cancellationToken);
    }

    public Task SendMessageReactionAsync(string groupId, string messageId, string reactionId,
        QReactionType reactionType, bool isAdd = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SendMessageReactionAsync(groupId, messageId, reactionId, isAdd, cancellationToken)
            .WaitAsync(cancellationToken);
    }

    public Task AcceptJoinRequestAsync(QGroupJoinRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.SetGroupNotification(
            LagrangeMapper.ParseId(request.GroupId, "GroupId"),
            ulong.Parse(request.RequestId),
            request.IsInvited ? BotGroupNotificationType.Invite : BotGroupNotificationType.Join,
            request.IsFiltered,
            GroupNotificationOperate.Allow).WaitAsync(cancellationToken);
    }

    public Task RejectJoinRequestAsync(QGroupJoinRequest request, string? reason = null, bool addToBlacklist = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (addToBlacklist) throw LagrangeMapper.Unsupported("拒绝申请并拉黑，上游没有拉黑参数");
        return session.Context.SetGroupNotification(
            LagrangeMapper.ParseId(request.GroupId, "GroupId"),
            ulong.Parse(request.RequestId),
            request.IsInvited ? BotGroupNotificationType.Invite : BotGroupNotificationType.Join,
            request.IsFiltered,
            GroupNotificationOperate.Deny,
            reason ?? string.Empty).WaitAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<QGroupNotification> Notifications, string? NextCursor)> GetNotificationsAsync(
        string? cursor = null, bool isFiltered = false, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var notifications = isFiltered
            ? await session.Context
                .FetchFilteredGroupNotifications((ulong)limit, cursor is null ? 0 : ulong.Parse(cursor))
                .ConfigureAwait(false)
            : await session.Context.FetchGroupNotifications((ulong)limit, cursor is null ? 0 : ulong.Parse(cursor))
                .ConfigureAwait(false);

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
            GroupId = join.GroupUin.ToString(),
            NotificationId = ((long)join.Sequence).ToString(),
            InitiatorId = join.TargetUin.ToString(),
            State = ToQqState(join.State),
            Comment = join.Comment,
            IsFiltered = join.IsFiltered,
            OperatorId = join.OperatorUin.ToString()
        },
        BotGroupInviteNotification invite => new QInvitedJoinRequestNotification
        {
            GroupId = invite.GroupUin.ToString(),
            NotificationId = ((long)invite.Sequence).ToString(),
            InitiatorId = invite.InviterUin.ToString(),
            TargetUserId = invite.TargetUin.ToString(),
            State = ToQqState(invite.State),
            OperatorId = invite.OperatorUin.ToString()
        },
        BotGroupSetAdminNotification admin => new QAdminChangeNotification
        {
            GroupId = admin.GroupUin.ToString(),
            NotificationId = ((long)admin.Sequence).ToString(),
            TargetUserId = admin.TargetUin.ToString(),
            IsSet = true,
            OperatorId = admin.OperatorUin.ToString()
        },
        BotGroupUnsetAdminNotification unset => new QAdminChangeNotification
        {
            GroupId = unset.GroupUin.ToString(),
            NotificationId = ((long)unset.Sequence).ToString(),
            TargetUserId = unset.TargetUin.ToString(),
            IsSet = false,
            OperatorId = unset.OperatorUin.ToString()
        },
        BotGroupKickNotification kick => new QKickNotification
        {
            GroupId = kick.GroupUin.ToString(),
            NotificationId = ((long)kick.Sequence).ToString(),
            TargetUserId = kick.TargetUin.ToString(),
            OperatorId = kick.OperatorUin.ToString()
        },
        BotGroupExitNotification exit => new QQuitNotification
        {
            GroupId = exit.GroupUin.ToString(),
            NotificationId = ((long)exit.Sequence).ToString(),
            TargetUserId = exit.TargetUin.ToString()
        },
        _ => null
    };

    private static QRequestState ToQqState(BotGroupNotificationState state) => state switch
    {
        BotGroupNotificationState.Accept => QRequestState.Accepted,
        BotGroupNotificationState.Reject => QRequestState.Rejected,
        _ => QRequestState.Pending
    };

    public async Task SetGroupAvatarAsync(string groupId, string imageUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var source = await LagrangeMapper.OpenResourceAsync(imageUri, bufferHttpResponse: false, cancellationToken);
        await LagrangeOperations.RequireSuccessAsync(session.Context.SetGroupAvatar(LagrangeMapper.ParseId(groupId, nameof(groupId)), source), "设置群头像", cancellationToken);
    }

    public async Task SetMemberAdminAsync(string groupId, string userId, bool isSet = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uid = await LagrangeProtocol.Uid(session.Context, LagrangeMapper.ParseId(userId, nameof(userId)), cancellationToken);
        await new LagrangeProtocol(session.Context).SetAdmin((ulong)LagrangeMapper.ParseId(groupId, nameof(groupId)), uid, isSet, cancellationToken);
    }

    public Task MuteMemberAsync(string groupId, string userId, TimeSpan duration,
        CancellationToken cancellationToken = default)
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

    public Task KickMemberAsync(string groupId, string userId, bool rejectAddRequest = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LagrangeOperations.RequireSuccessAsync(session.Context.KickGroupMember(
            LagrangeMapper.ParseId(groupId, nameof(groupId)), LagrangeMapper.ParseId(userId, nameof(userId)),
            rejectAddRequest), "踢出群成员", cancellationToken);
    }

    public Task<IReadOnlyList<QGroupAnnouncement>> GetAnnouncementsAsync(string groupId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = LagrangeMapper.ParseId(groupId, nameof(groupId));
        return new LagrangeGroupWeb(session.Context).Announcements(groupId, cancellationToken);
    }

    public Task SendAnnouncementAsync(string groupId, string content, string? imageUri = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = LagrangeMapper.ParseId(groupId, nameof(groupId)); ArgumentException.ThrowIfNullOrWhiteSpace(content);
        return new LagrangeGroupWeb(session.Context).Announce(groupId, content, imageUri, cancellationToken);
    }

    public Task DeleteAnnouncementAsync(string groupId, string announcementId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = LagrangeMapper.ParseId(groupId, nameof(groupId)); ArgumentException.ThrowIfNullOrWhiteSpace(announcementId);
        return new LagrangeGroupWeb(session.Context).Delete(groupId, announcementId, cancellationToken);
    }

    public async Task<IReadOnlyList<QEssenceMessage>> GetEssenceMessagesAsync(string groupId, int pageIndex, int pageSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await GetEssenceMessagesPageAsync(groupId, pageIndex, pageSize, cancellationToken)).Messages;
    }

    public Task<(IReadOnlyList<QEssenceMessage> Messages, bool IsEnd)> GetEssenceMessagesPageAsync(string groupId, int pageIndex, int pageSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = LagrangeMapper.ParseId(groupId, nameof(groupId));
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return new LagrangeGroupWeb(session.Context).Essence(groupId, pageIndex, pageSize, cancellationToken);
    }

    public async Task SetEssenceMessageAsync(string groupId, string messageId, bool isSet = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = await session.ResolveMessageAsync(messageId, LagrangeMapper.ParseId(groupId, nameof(groupId)),
                          true, cancellationToken)
                      ?? throw new InvalidOperationException("找不到精华消息。");
        await (isSet ? session.Context.SetEssenceMessage(message) : session.Context.RemoveEssenceMessage(message))
            .WaitAsync(cancellationToken);
    }

    public Task AcceptInvitationAsync(string groupId, string invitationSeq,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeProtocol(session.Context).Invitation((ulong)LagrangeMapper.ParseId(groupId, nameof(groupId)),
            ulong.Parse(invitationSeq), true, cancellationToken);
    }

    public Task RejectInvitationAsync(string groupId, string invitationSeq,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeProtocol(session.Context).Invitation((ulong)LagrangeMapper.ParseId(groupId, nameof(groupId)),
            ulong.Parse(invitationSeq), false, cancellationToken);
    }
}

internal sealed partial class LagrangeQqFileApi(LagrangeSession session, LagrangeFlashTransfers flash) : IQFileApi
{
    public async Task<string> UploadPrivateFileAsync(string userId, string fileUri, string fileName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await LagrangeMapper.OpenResourceAsync(fileUri, bufferHttpResponse: false, cancellationToken).ConfigureAwait(false);
        var context = session.Context;
        var result = await context.RunMessageOperationAsync(
            () => context.SendFriendFileWithId(LagrangeMapper.ParseId(userId, "userId"), stream, fileName),
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(result.FileId)) throw new InvalidDataException("私聊文件上传未返回文件 ID。");
        return result.FileId;
    }

    public async Task<string> UploadGroupFileAsync(string groupId, string fileUri, string fileName,
        string parentFolderId = "/", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await LagrangeMapper.OpenResourceAsync(fileUri, bufferHttpResponse: false, cancellationToken).ConfigureAwait(false);
        var context = session.Context;
        return await context.RunMessageOperationAsync(
            () => context.SendGroupFile(LagrangeMapper.ParseId(groupId, "groupId"), stream, fileName, parentFolderId),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<string> GetGroupFileDownloadUrlAsync(string groupId, string fileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupFSDownload(LagrangeMapper.ParseId(groupId, "groupId"), fileId)
            .WaitAsync(cancellationToken);
    }

    public Task MoveGroupFileAsync(string groupId, string fileId, string targetFolderId, string parentFolderId = "/",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context
            .GroupFSMove(LagrangeMapper.ParseId(groupId, "groupId"), fileId, targetFolderId, parentFolderId)
            .WaitAsync(cancellationToken);
    }

    public Task DeleteGroupFileAsync(string groupId, string fileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupFSDelete(LagrangeMapper.ParseId(groupId, "groupId"), fileId)
            .WaitAsync(cancellationToken);
    }

    public Task<string> GetPrivateFileDownloadUrlAsync(string userId, string fileId, string fileHash,
        CancellationToken cancellationToken = default)
        => GetPrivateFileDownloadUrlAsync(userId, fileId, fileHash, false, cancellationToken);

    public async Task<string> GetPrivateFileDownloadUrlAsync(string userId, string fileId, string fileHash,
        bool isSelfSend, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId); ArgumentException.ThrowIfNullOrWhiteSpace(fileHash);
        var peer = isSelfSend ? LagrangeMapper.ParseId(userId, nameof(userId)) : session.Context.BotUin;
        var uid = await LagrangeProtocol.Uid(session.Context, peer, cancellationToken);
        return await new LagrangeProtocol(session.Context).PrivateFile(uid, fileId, fileHash, cancellationToken);
    }

    public async Task<(IReadOnlyList<QGroupFile> Files, IReadOnlyList<QGroupFolder> Folders)> GetGroupFilesAsync(
        string groupId, string parentFolderId = "/", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await session.Context
            .FetchGroupFSList(LagrangeMapper.ParseId(groupId, nameof(groupId)), parentFolderId)
            .WaitAsync(cancellationToken);
        return ([.. entries.OfType<BotFileEntry>().Select(x => LagrangeMapper.ToQq(x, groupId))],
            [.. entries.OfType<BotFolderEntry>().Select(x => LagrangeMapper.ToQq(x, groupId))]);
    }

    public Task RenameGroupFileAsync(string groupId, string fileId, string newFileName, string parentFolderId = "/",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId); ArgumentException.ThrowIfNullOrWhiteSpace(newFileName);
        return new LagrangeProtocol(session.Context).RenameFile((ulong)LagrangeMapper.ParseId(groupId, nameof(groupId)), fileId, parentFolderId, newFileName, cancellationToken);
    }

    public async Task<string> CreateGroupFolderAsync(string groupId, string folderName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = LagrangeMapper.ParseId(groupId, nameof(groupId));
        var before = (await session.Context.FetchGroupFSList(peer).WaitAsync(cancellationToken))
            .OfType<BotFolderEntry>().Select(x => x.FolderId).ToHashSet();
        await session.Context.GroupFSCreateFolder(peer, folderName).WaitAsync(cancellationToken);
        var created = (await session.Context.FetchGroupFSList(peer).WaitAsync(cancellationToken))
            .OfType<BotFolderEntry>().Where(x => x.FolderName == folderName && !before.Contains(x.FolderId)).ToArray();
        return created.Length == 1
            ? created[0].FolderId
            : throw new InvalidOperationException("创建操作已完成，但无法唯一确定新文件夹 ID，请刷新群文件列表。");
    }

    public Task RenameGroupFolderAsync(string groupId, string folderId, string newFolderName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context
            .GroupFSRenameFolder(LagrangeMapper.ParseId(groupId, nameof(groupId)), folderId, newFolderName)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        return new LagrangeProtocol(session.Context).PersistFile((ulong)LagrangeMapper.ParseId(groupId, nameof(groupId)), fileId, cancellationToken);
    }
}

internal sealed class LagrangeQqSystemApi(LagrangeSession session) : IQSystemApi
{
    public async Task<IReadOnlyList<QOnlineClient>> GetOnlineClientsAsync(bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        var devices = await session.Context.GetOnlineDevicesAsync(noCache, cancellationToken);
        return [.. devices.Select(device => new QOnlineClient
        {
            AppId = device.AppId,
            DeviceName = string.IsNullOrEmpty(device.DeviceName) ? null : device.DeviceName,
            DeviceKind = string.IsNullOrEmpty(device.DeviceKind) ? null : device.DeviceKind
        })];
    }

    public async Task<QUserProfile> GetUserProfileAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stranger = await session.Context.FetchStranger(LagrangeMapper.ParseId(userId, "userId"))
            .ConfigureAwait(false);
        return new QUserProfile
        {
            UserId = stranger.Uin.ToString(),
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

    public async Task<IReadOnlyList<QFriend>> GetFriendListAsync(bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var friends = await session.Context.FetchFriends(noCache).ConfigureAwait(false);
        return [.. friends.Select(LagrangeMapper.ToQq)];
    }

    public async Task<QFriend> GetFriendInfoAsync(string userId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var friends = await GetFriendListAsync(noCache, cancellationToken).ConfigureAwait(false);
        return friends.FirstOrDefault(friend => friend.UserId == userId)
               ?? throw new InvalidOperationException($"好友 {userId} 不存在。");
    }

    internal async Task<IReadOnlyList<QGroup>> GetGroupListAsync(bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = await session.Context.FetchGroups(noCache).ConfigureAwait(false);
        return [.. groups.Select(LagrangeMapper.ToQq)];
    }

    internal async Task<QGroup> GetGroupInfoAsync(string groupId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = await GetGroupListAsync(noCache, cancellationToken).ConfigureAwait(false);
        return groups.FirstOrDefault(group => group.GroupId == groupId)
               ?? throw new InvalidOperationException($"群 {groupId} 不存在。");
    }

    internal async Task<IReadOnlyList<QGroupMember>> GetGroupMemberListAsync(string groupId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = await session.Context.FetchMembers(LagrangeMapper.ParseId(groupId, "groupId"), noCache)
            .ConfigureAwait(false);
        return [.. members.Select(LagrangeMapper.ToQq)];
    }

    internal async Task<QGroupMember> GetGroupMemberInfoAsync(string groupId, string userId, bool noCache = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = await GetGroupMemberListAsync(groupId, noCache, cancellationToken).ConfigureAwait(false);
        return members.FirstOrDefault(member => member.UserId == userId)
               ?? throw new InvalidOperationException($"群成员 {groupId}/{userId} 不存在。");
    }

    public async Task SetAvatarAsync(string imageUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = await LagrangeMapper.OpenResourceAsync(imageUri, bufferHttpResponse: false, cancellationToken)
            .ConfigureAwait(false);
        await LagrangeOperations.RequireSuccessAsync(session.Context.SetBotAvatar(stream), "设置头像", cancellationToken);
    }

    public Task<string> GetCookiesAsync(string domain, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LagrangeWebTickets.For(session.Context).GetCookiesAsync(domain, cancellationToken);
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
            QqProtocolType = context.Config.Protocol switch
            {
                Protocols.AndroidPhone => "android_phone", Protocols.AndroidPad => "android_pad",
                Protocols.MacOs => "macos", _ => context.Config.Protocol.ToString().ToLowerInvariant()
            },
            QqProtocolVersion = context.Config.Protocol is Protocols.AndroidPhone or Protocols.AndroidPad ? context.AppInfo.PtVersion : context.AppInfo.CurrentVersion
        });
    }

    public Task SetPeerPinAsync(QMessageScene scene, string peerId, bool isPinned = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peer = LagrangeMapper.ParseId(peerId, nameof(peerId));
        if (scene == QMessageScene.Temp) throw LagrangeMapper.Unsupported("临时会话置顶");
        return (scene == QMessageScene.Group
            ? session.Context.SetPinGroup(peer, isPinned)
            : session.Context.SetPinFriend(peer, isPinned)).WaitAsync(cancellationToken);
    }

    public async Task SetNicknameAsync(string nickname, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        await new LagrangeProtocol(session.Context).Profile((ulong)session.Context.BotUin, 20002, nickname, cancellationToken);
    }

    public async Task SetBioAsync(string bio, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await new LagrangeProtocol(session.Context).Profile((ulong)session.Context.BotUin, 102, bio, cancellationToken);
    }

    public async Task<string> GetCsrfTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = await LagrangeWebTickets.For(session.Context).GetSKeyAsync(cancellationToken);
        return LagrangeGroupWeb.Csrf(key);
    }

    public async Task<IReadOnlyList<string>> GetCustomFaceUrlListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await new LagrangeProtocol(session.Context).Faces((ulong)session.Context.BotUin, cancellationToken);
    }

    public async Task<(IReadOnlyList<QFriend> Friends, IReadOnlyList<QGroup> Groups)> GetPeerPinsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pins = await new LagrangeProtocol(session.Context).Pins(cancellationToken);
        var uids = pins.Where(x => x.Number == 1 && x.Wire == 2).Select(x => ProtoWire.String(ProtoWire.Parse(x.Bytes), 1)).ToHashSet();
        var groups = pins.Where(x => x.Number == 3 && x.Wire == 2).Select(x => ProtoWire.Number(ProtoWire.Parse(x.Bytes), 1)).ToHashSet();
        var friends = await session.Context.FetchFriends().WaitAsync(cancellationToken);
        var allGroups = await session.Context.FetchGroups().WaitAsync(cancellationToken);
        return ([.. friends.Where(x => uids.Contains(x.Uid)).Select(LagrangeMapper.ToQq)],
            [.. allGroups.Where(x => groups.Contains((ulong)x.Uin)).Select(LagrangeMapper.ToQq)]);
    }
}

internal sealed partial class LagrangeQqMessageApi(LagrangeSession session, LagrangeQqFileApi files) : IQMessageApi
{
    public async Task<string> SendMessageAsync(QMessageScene scene, string peerId,
        IReadOnlyList<QOutgoingSegment> segments, CancellationToken cancellationToken = default) =>
        (await SendMessageDetailedAsync(scene, peerId, segments, cancellationToken)).MessageId;

    public async Task<QSentMessage> SendMessageDetailedAsync(QMessageScene scene, string peerId,
        IReadOnlyList<QOutgoingSegment> segments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = session.Context;
        var parsedPeer = LagrangeMapper.ParseId(peerId, "peerId");
        List<Stream> resources = [];
        Lagrange.Core.Message.BotMessage sent;
        try
        {
            sent = await context.RunMessageOperationAsync(async () =>
            {
                var chain = new Lagrange.Core.Message.MessageChain();
                foreach (var segment in segments)
                {
                    await LagrangeMapper.AppendQqSegmentAsync(chain, segment,
                        id => session.ResolveMessageAsync(id, parsedPeer, scene == QMessageScene.Group, cancellationToken),
                        context, cancellationToken, resources).ConfigureAwait(false);
                }

                return scene == QMessageScene.Group
                    ? await context.SendGroupMessage(parsedPeer, chain).ConfigureAwait(false)
                    : await context.SendFriendMessage(parsedPeer, chain).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var resource in resources)
                await resource.DisposeAsync().ConfigureAwait(false);
        }

        session.CacheMessage(sent);
        return new QSentMessage(sent.Sequence.ToString(), DateTimeOffset.FromUnixTimeSeconds(sent.Time));
    }

    public async Task<QIncomingMessage?> GetMessageAsync(QMessageScene scene, string peerId, string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = session.Context;
        var peer = LagrangeMapper.ParseId(peerId, nameof(peerId));
        var message = await context.RunMessageOperationAsync(async () =>
        {
            var sequence = ulong.Parse(messageId, System.Globalization.CultureInfo.InvariantCulture);
            var group = scene == QMessageScene.Group;
            var cached = session.FindMessage(sequence, peer, group);
            if (cached?.Contact.Uin == context.BotUin && cached.Entities
                    .OfType<Lagrange.Core.Message.Entities.VideoEntity>()
                    .FirstOrDefault(video => video.IsBubble) is { } bubble)
            {
                // QQ history can present sent bubbles as plain compatibility text on desktop sessions.
                // The original media index belongs to this exact outgoing message and can resolve its download URL.
                if (string.IsNullOrWhiteSpace(bubble.FileUrl))
                    await bubble.Postprocess(context, cached);
                return cached;
            }
            if (!group)
                return await session.ResolveMessageAsync(messageId, peer, false, cancellationToken);

            return (await context.GetGroupMessage(peer, sequence, sequence)).FirstOrDefault();
        }, cancellationToken).ConfigureAwait(false);
        return message is null
            ? null
            : LagrangeMapper.ToMessageEvent(message, session.Context.BotUin.ToString()).Raw as QIncomingMessage;
    }

    public async Task<(IReadOnlyList<QIncomingMessage> Messages, string? NextCursor)> GetHistoryMessagesAsync(
        QMessageScene scene, string peerId, string? cursor = null, int limit = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (scene == QMessageScene.Friend && cursor is null)
        {
            var recent = await session.Context.GetRoamMessage(LagrangeMapper.ParseId(peerId, nameof(peerId)),
                checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), (uint)limit).WaitAsync(cancellationToken);
            foreach (var item in recent) session.CacheMessage(item);
            var first = recent.Count == 0 ? 0 : recent.Min(x => x.Sequence);
            return (
                [
                    .. recent.Select(x =>
                        (QIncomingMessage)LagrangeMapper.ToMessageEvent(x, session.Context.BotUin.ToString()).Raw!)
                ],
                first > 1 ? (first - 1).ToString() : null);
        }

        ulong endSequence;
        if (cursor is not null)
        {
            endSequence = ulong.Parse(cursor);
        }
        else if (scene == QMessageScene.Group)
        {
            var extra = await session.Context.FetchGroupExtra(LagrangeMapper.ParseId(peerId, "peerId"))
                .ConfigureAwait(false);
            endSequence = (ulong)extra.LatestMessageSequence;
        }
        else
        {
            throw LagrangeMapper.Unsupported("私聊历史消息需要指定 startMessageId");
        }

        var startSequence = endSequence > (ulong)limit ? endSequence - (ulong)limit + 1 : 1;
        var messages = scene == QMessageScene.Group
            ? await session.Context
                .GetGroupMessage(LagrangeMapper.ParseId(peerId, "peerId"), startSequence, endSequence)
                .ConfigureAwait(false)
            : await session.Context.GetC2CMessage(LagrangeMapper.ParseId(peerId, "peerId"), startSequence, endSequence)
                .ConfigureAwait(false);

        foreach (var item in messages) session.CacheMessage(item);
        var converted = messages
            .Select(message => LagrangeMapper.ToMessageEvent(message, session.Context.BotUin.ToString()).Raw)
            .OfType<QIncomingMessage>()
            .ToArray();

        var next = startSequence > 1 ? (startSequence - 1).ToString() : null;
        return (converted, next);
    }

    public async Task RecallMessageAsync(QMessageScene scene, string peerId, string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = await session.ResolveMessageAsync(messageId, LagrangeMapper.ParseId(peerId, "peerId"),
                          scene == QMessageScene.Group, cancellationToken)
                      ?? throw new InvalidOperationException($"找不到要撤回的消息: {messageId}");
        await session.Context.RecallMessage(message).ConfigureAwait(false);
    }

    public Task<string> GetResourceTempUrlAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GetNTV2RichMediaUrl(resourceId).WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QForwardedIncomingMessage>> GetForwardedMessagesAsync(string forwardId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entity = new Lagrange.Core.Message.Entities.MultiMsgEntity(forwardId);
        var envelope =
            Lagrange.Core.Message.BotMessage.CreateCustomGroup(0, 0, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), []);
        var context = session.Context;
        await context.RunMessageOperationAsync(async () =>
        {
            await entity.Postprocess(context, envelope).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return
        [
            .. entity.Messages.Select(x => new QForwardedIncomingMessage
            {
                MessageId = x.Sequence.ToString(),
                SenderName = x.Contact.Nickname,
                Time = DateTimeOffset.FromUnixTimeSeconds(x.Time),
                Segments = LagrangeMapper.ToQqSegments(x.Entities)
            })
        ];
    }

    public async Task MarkAsReadAsync(QMessageScene scene, string peerId, string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scene == QMessageScene.Temp) throw new ArgumentException("临时会话不支持已读上报。", nameof(scene));
        var peer = LagrangeMapper.ParseId(peerId, nameof(peerId));
        var sequence = ulong.Parse(messageId, System.Globalization.CultureInfo.InvariantCulture);
        var protocol = new LagrangeProtocol(session.Context);
        if (scene == QMessageScene.Group) await protocol.Read((ulong)peer, null, sequence, 0, cancellationToken);
        else
        {
            var message = await session.ResolveMessageAsync(messageId, peer, false, cancellationToken)
                ?? throw new InvalidOperationException("找不到需要标记已读的消息时间。");
            await protocol.Read((ulong)peer, await LagrangeProtocol.Uid(session.Context, peer, cancellationToken), sequence, (ulong)message.Time, cancellationToken);
        }
    }
}
