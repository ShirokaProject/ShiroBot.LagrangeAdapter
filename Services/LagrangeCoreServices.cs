using Lagrange.Core.Common.Interface;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;

namespace ShiroBot.LagrangeAdapter.Services;

/// <summary>平台无关消息服务的 Lagrange 实现。</summary>
internal sealed class LagrangeMessageService(LagrangeSession session) : IMessageService
{
    public async Task<SentMessage> SendMessageAsync(Sdk.Channel channel, IReadOnlyList<MessageSegment> segments)
    {
        var peerId = LagrangeMapper.ParseId(channel.Id, "Channel.Id");
        var chain = await LagrangeMapper.ToChainAsync(segments, session.ResolveMessageAsync).ConfigureAwait(false);

        var sent = channel.Type == ChannelType.Group
            ? await session.Context.SendGroupMessage(peerId, chain).ConfigureAwait(false)
            : await session.Context.SendFriendMessage(peerId, chain).ConfigureAwait(false);

        session.CacheMessage(sent);
        return new SentMessage(sent.Sequence.ToString())
        {
            Timestamp = new DateTimeOffset(sent.Time.ToUniversalTime())
        };
    }

    public async Task DeleteMessageAsync(Sdk.Channel channel, string messageId)
    {
        var message = await session.ResolveMessageAsync(messageId).ConfigureAwait(false)
                      ?? throw LagrangeMapper.Unsupported($"撤回未缓存的消息(seq={messageId},仅支持近期消息)");
        await session.Context.RecallMessage(message).ConfigureAwait(false);
    }

    public async Task<MessageEvent?> GetMessageAsync(Sdk.Channel channel, string messageId)
    {
        var sequence = (ulong)LagrangeMapper.ParseId(messageId, "messageId");

        if (session.FindMessage(sequence) is { } cached)
        {
            return LagrangeMapper.ToMessageEvent(cached);
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
        return LagrangeMapper.ToMessageEvent(message);
    }

    public async Task<IReadOnlyList<MessageEvent>> GetHistoryMessagesAsync(
        Sdk.Channel channel,
        string? beforeMessageId = null,
        int limit = 20)
    {
        var peerId = LagrangeMapper.ParseId(channel.Id, "Channel.Id");

        ulong endSequence;
        if (beforeMessageId is not null)
        {
            endSequence = (ulong)LagrangeMapper.ParseId(beforeMessageId, "beforeMessageId");
        }
        else if (channel.Type == ChannelType.Group)
        {
            var extra = await session.Context.FetchGroupExtra(peerId).ConfigureAwait(false);
            endSequence = (ulong)extra.LatestMessageSequence;
        }
        else
        {
            throw LagrangeMapper.Unsupported("私聊历史消息需要指定 beforeMessageId");
        }

        var startSequence = endSequence > (ulong)limit ? endSequence - (ulong)limit : 1;
        var messages = channel.Type == ChannelType.Group
            ? await session.Context.GetGroupMessage(peerId, startSequence, endSequence).ConfigureAwait(false)
            : await session.Context.GetC2CMessage(peerId, startSequence, endSequence).ConfigureAwait(false);

        foreach (var message in messages)
        {
            session.CacheMessage(message);
        }

        return messages.Select(LagrangeMapper.ToMessageEvent).ToArray();
    }

    public Task<string> GetResourceUrlAsync(string resourceId) =>
        session.Context.GetNTV2RichMediaUrl(resourceId);
}

/// <summary>平台无关群/成员服务的 Lagrange 实现。</summary>
internal sealed class LagrangeChannelService(LagrangeSession session) : IChannelService
{
    public async Task<IReadOnlyList<Sdk.Channel>> GetChannelsAsync()
    {
        var groups = await session.Context.FetchGroups().ConfigureAwait(false);
        return groups
            .Select(group => new Sdk.Channel(group.GroupUin.ToString(), ChannelType.Group)
            {
                Name = group.GroupName,
                GuildId = group.GroupUin.ToString()
            })
            .ToArray();
    }

    public async Task<Sdk.Channel?> GetChannelAsync(string channelId)
    {
        var groupUin = LagrangeMapper.ParseId(channelId, "channelId");
        var groups = await session.Context.FetchGroups().ConfigureAwait(false);
        var group = groups.FirstOrDefault(item => item.GroupUin == groupUin);
        return group is null
            ? null
            : new Sdk.Channel(group.GroupUin.ToString(), ChannelType.Group)
            {
                Name = group.GroupName,
                GuildId = group.GroupUin.ToString()
            };
    }

    public async Task<IReadOnlyList<Member>> GetMembersAsync(string channelId)
    {
        var members = await session.Context
            .FetchMembers(LagrangeMapper.ParseId(channelId, "channelId"))
            .ConfigureAwait(false);
        return members
            .Select(member => new Member(new Sdk.User(member.Uin.ToString()) { Name = member.Nickname })
            {
                Nick = string.IsNullOrEmpty(member.MemberCard) ? null : member.MemberCard,
                Role = member.Permission switch
                {
                    Lagrange.Core.Common.Entity.GroupMemberPermission.Owner => MemberRole.Owner,
                    Lagrange.Core.Common.Entity.GroupMemberPermission.Admin => MemberRole.Admin,
                    _ => MemberRole.Member
                }
            })
            .ToArray();
    }

    public async Task<Member?> GetMemberAsync(string channelId, string userId)
    {
        var members = await GetMembersAsync(channelId).ConfigureAwait(false);
        return members.FirstOrDefault(member => member.User.Id == userId);
    }

    public Task SetChannelNameAsync(string channelId, string name) =>
        session.Context.GroupRename(LagrangeMapper.ParseId(channelId, "channelId"), name);

    public Task KickMemberAsync(string channelId, string userId) =>
        throw LagrangeMapper.Unsupported("踢出群成员(Lagrange.Core 未提供 API)");

    public Task MuteMemberAsync(string channelId, string userId, TimeSpan duration) =>
        throw LagrangeMapper.Unsupported("群成员禁言(Lagrange.Core 未提供 API)");

    public Task LeaveChannelAsync(string channelId) =>
        session.Context.GroupQuit(LagrangeMapper.ParseId(channelId, "channelId"));
}

/// <summary>平台无关用户服务的 Lagrange 实现。</summary>
internal sealed class LagrangeUserService(LagrangeSession session) : IUserService
{
    public Task<Sdk.User> GetSelfAsync()
    {
        var context = session.Context;
        return Task.FromResult(new Sdk.User(context.BotUin.ToString())
        {
            Name = context.BotInfo?.Name,
            IsBot = true
        });
    }

    public async Task<Sdk.User?> GetUserAsync(string userId)
    {
        var stranger = await session.Context
            .FetchStranger(LagrangeMapper.ParseId(userId, "userId"))
            .ConfigureAwait(false);
        return new Sdk.User(stranger.Uin.ToString()) { Name = stranger.Nickname };
    }

    public async Task<IReadOnlyList<Sdk.User>> GetFriendsAsync()
    {
        var friends = await session.Context.FetchFriends().ConfigureAwait(false);
        return friends
            .Select(friend => new Sdk.User(friend.Uin.ToString()) { Name = friend.Nickname })
            .ToArray();
    }

    public Task AcceptFriendRequestAsync(string token) =>
        throw LagrangeMapper.Unsupported("处理好友申请(Lagrange.Core 未提供 API)");

    public Task RejectFriendRequestAsync(string token, string? reason = null) =>
        throw LagrangeMapper.Unsupported("处理好友申请(Lagrange.Core 未提供 API)");
}
