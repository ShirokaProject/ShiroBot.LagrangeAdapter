using Lagrange.Core.Common.Interface;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;

namespace ShiroBot.LagrangeAdapter.Services;

/// <summary>平台无关消息服务的 Lagrange 实现。</summary>
internal sealed class LagrangeMessageService(LagrangeSession session) : IMessageService
{
    public async Task<SentMessage> SendMessageAsync(Channel channel, IReadOnlyList<MessageSegment> segments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var peerId = LagrangeMapper.ParseId(channel.Id, "Channel.Id");
        var chain = await LagrangeMapper.ToChainAsync(segments,
                id => session.ResolveMessageAsync(id, peerId, channel.Type == ChannelType.Group, cancellationToken))
            .ConfigureAwait(false);

        var sent = channel.Type == ChannelType.Group
            ? await session.Context.SendGroupMessage(peerId, chain).ConfigureAwait(false)
            : await session.Context.SendFriendMessage(peerId, chain).ConfigureAwait(false);

        session.CacheMessage(sent);
        return new SentMessage(sent.Sequence.ToString())
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(sent.Time)
        };
    }

    public async Task DeleteMessageAsync(Channel channel, string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = await session.ResolveMessageAsync(messageId, LagrangeMapper.ParseId(channel.Id, "Channel.Id"),
                          channel.Type == ChannelType.Group, cancellationToken).ConfigureAwait(false)
                      ?? throw LagrangeMapper.Unsupported($"撤回未缓存的消息(seq={messageId},仅支持近期消息)");
        await session.Context.RecallMessage(message).ConfigureAwait(false);
    }

    public async Task<MessageEvent?> GetMessageAsync(Channel channel, string messageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = (ulong)LagrangeMapper.ParseId(messageId, "messageId");

        if (session.FindMessage(sequence, LagrangeMapper.ParseId(channel.Id, "Channel.Id"),
                channel.Type == ChannelType.Group) is { } cached)
        {
            return LagrangeMapper.ToMessageEvent(cached, session.Context.BotUin.ToString());
        }

        var peerId = LagrangeMapper.ParseId(channel.Id, "Channel.Id");
        var messages = channel.Type == ChannelType.Group
            ? await session.Context.GetGroupMessage(peerId, sequence, sequence).ConfigureAwait(false)
            : await session.Context.GetC2CMessage(peerId, sequence, sequence).ConfigureAwait(false);

        var message = messages.FirstOrDefault();
        if (message is null)
        {
            return null;
        }

        session.CacheMessage(message);
        return LagrangeMapper.ToMessageEvent(message, session.Context.BotUin.ToString());
    }

    public async Task<IReadOnlyList<MessageEvent>> GetHistoryMessagesAsync(
        Channel channel,
        string? beforeMessageId = null,
        int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var peerId = LagrangeMapper.ParseId(channel.Id, "Channel.Id");

        ulong endSequence;
        if (beforeMessageId is not null)
        {
            endSequence = (ulong)LagrangeMapper.ParseId(beforeMessageId, "beforeMessageId");
            if (endSequence <= 1) return [];
            endSequence--;
        }
        else if (channel.Type == ChannelType.Group)
        {
            var extra = await session.Context.FetchGroupExtra(peerId).ConfigureAwait(false);
            endSequence = (ulong)extra.LatestMessageSequence;
        }
        else
        {
            var recent = await session.Context.GetRoamMessage(peerId,
                    checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), (uint)limit)
                .WaitAsync(cancellationToken);
            foreach (var item in recent) session.CacheMessage(item);
            return [.. recent.Select(x => LagrangeMapper.ToMessageEvent(x, session.Context.BotUin.ToString()))];
        }

        var startSequence = endSequence > (ulong)limit ? endSequence - (ulong)limit + 1 : 1;
        var messages = channel.Type == ChannelType.Group
            ? await session.Context.GetGroupMessage(peerId, startSequence, endSequence).ConfigureAwait(false)
            : await session.Context.GetC2CMessage(peerId, startSequence, endSequence).ConfigureAwait(false);

        foreach (var message in messages)
        {
            session.CacheMessage(message);
        }

        return
        [
            .. messages.Select(message => LagrangeMapper.ToMessageEvent(message, session.Context.BotUin.ToString()))
        ];
    }

    public Task<string> GetResourceUrlAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GetNTV2RichMediaUrl(resourceId).WaitAsync(cancellationToken);
    }
}

/// <summary>平台无关群/成员服务的 Lagrange 实现。</summary>
internal sealed class LagrangeChannelService(LagrangeSession session) : IChannelService
{
    public async Task<IReadOnlyList<Channel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = await session.Context.FetchGroups().ConfigureAwait(false);
        return
        [
            .. groups
                .Select(group => new Channel(group.GroupUin.ToString(), ChannelType.Group)
                {
                    Name = group.GroupName,
                    GuildId = group.GroupUin.ToString()
                })
        ];
    }

    public async Task<Channel?> GetChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groupUin = LagrangeMapper.ParseId(channelId, "channelId");
        var groups = await session.Context.FetchGroups().ConfigureAwait(false);
        var group = groups.FirstOrDefault(item => item.GroupUin == groupUin);
        return group is null
            ? null
            : new Channel(group.GroupUin.ToString(), ChannelType.Group)
            {
                Name = group.GroupName,
                GuildId = group.GroupUin.ToString()
            };
    }

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string channelId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = await session.Context
            .FetchMembers(LagrangeMapper.ParseId(channelId, "channelId"))
            .ConfigureAwait(false);
        return
        [
            .. members
                .Select(member => new Member(new User(member.Uin.ToString()) { Name = member.Nickname })
                {
                    Nick = string.IsNullOrEmpty(member.MemberCard) ? null : member.MemberCard,
                    Role = member.Permission switch
                    {
                        Lagrange.Core.Common.Entity.GroupMemberPermission.Owner => MemberRole.Owner,
                        Lagrange.Core.Common.Entity.GroupMemberPermission.Admin => MemberRole.Admin,
                        _ => MemberRole.Member
                    }
                })
        ];
    }

    public async Task<Member?> GetMemberAsync(string channelId, string userId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var members = await GetMembersAsync(channelId, cancellationToken).ConfigureAwait(false);
        return members.FirstOrDefault(member => member.User.Id == userId);
    }

    public Task SetChannelNameAsync(string channelId, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupRename(LagrangeMapper.ParseId(channelId, "channelId"), name)
            .WaitAsync(cancellationToken);
    }

    public Task KickMemberAsync(string channelId, string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeQqGroupApi(session).KickMemberAsync(channelId, userId, cancellationToken: cancellationToken);
    }

    public Task MuteMemberAsync(string channelId, string userId, TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new LagrangeQqGroupApi(session).MuteMemberAsync(channelId, userId, duration, cancellationToken);
    }

    public Task LeaveChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return session.Context.GroupQuit(LagrangeMapper.ParseId(channelId, "channelId")).WaitAsync(cancellationToken);
    }
}

/// <summary>平台无关用户服务的 Lagrange 实现。</summary>
internal sealed class LagrangeUserService(LagrangeSession session) : IUserService
{
    public Task<User> GetSelfAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = session.Context;
        return Task.FromResult(new User(context.BotUin.ToString())
        {
            Name = context.BotInfo?.Name,
            IsBot = true
        });
    }

    public async Task<User?> GetUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stranger = await session.Context
            .FetchStranger(LagrangeMapper.ParseId(userId, "userId"))
            .ConfigureAwait(false);
        return new User(stranger.Uin.ToString()) { Name = stranger.Nickname };
    }

    public async Task<IReadOnlyList<User>> GetFriendsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var friends = await session.Context.FetchFriends().ConfigureAwait(false);
        return
        [
            .. friends
                .Select(friend => new User(friend.Uin.ToString()) { Name = friend.Nickname })
        ];
    }

    public Task AcceptFriendRequestAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("处理好友申请(Lagrange.Core 未提供 API)");
    }

    public Task RejectFriendRequestAsync(string token, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw LagrangeMapper.Unsupported("处理好友申请(Lagrange.Core 未提供 API)");
    }
}