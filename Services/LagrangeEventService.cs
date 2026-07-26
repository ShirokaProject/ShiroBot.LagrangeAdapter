using Lagrange.Core;
using Lagrange.Core.Events;
using Lagrange.Core.Events.EventArgs;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Adapter;
using ShiroBot.SDK.Models;
using Sdk = ShiroBot.SDK.Models;
using LgrEvents = Lagrange.Core.Events.EventArgs;

namespace ShiroBot.LagrangeAdapter.Services;

/// <summary>Lagrange 事件流 → 核心 BotEvent。</summary>
internal sealed class LagrangeEventService : IEventService
{
    private LagrangeSession? _session;

    public event Func<Sdk.BotEvent, Task>? EventReceived;

    public void Attach(LagrangeSession session)
    {
        _session = session;
        var invoker = session.Context.EventInvoker;

        // 注意:Lagrange 断言 handler 不能是静态方法,这里全部用实例 lambda 委托
        invoker.RegisterEvent<BotMessageEvent>(async (_, e) => await OnMessageAsync(e));
        invoker.RegisterEvent<BotGroupRecallEvent>(async (_, e) => await PublishAsync(new MessageDeletedEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = LagrangeMapper.SelfId,
            Raw = e,
            MessageId = e.Sequence.ToString(),
            Channel = GroupChannel(e.GroupUin),
            SenderId = e.AuthorUin.ToString(),
            OperatorId = e.OperatorUin.ToString()
        }));
        invoker.RegisterEvent<BotFriendRecallEvent>(async (_, e) => await PublishAsync(new MessageDeletedEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = LagrangeMapper.SelfId,
            Raw = e,
            MessageId = e.Sequence.ToString(),
            Channel = Sdk.Channel.Direct(e.PeerUin.ToString()),
            SenderId = e.AuthorUin.ToString()
        }));
        invoker.RegisterEvent<BotGroupMemberIncreaseEvent>(async (_, e) => await PublishAsync(new MemberJoinedEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = LagrangeMapper.SelfId,
            Raw = e,
            Channel = GroupChannel(e.GroupUin),
            UserId = e.MemberUin.ToString(),
            OperatorId = e.OperatorUin != 0 ? e.OperatorUin.ToString()
                : e.InvitorUin != 0 ? e.InvitorUin.ToString() : null
        }));
        invoker.RegisterEvent<BotGroupMemberDecreaseEvent>(async (_, e) => await PublishAsync(new MemberLeftEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = LagrangeMapper.SelfId,
            Raw = e,
            Channel = GroupChannel(e.GroupUin),
            UserId = e.UserUin.ToString(),
            OperatorId = e.OperatorUin?.ToString()
        }));
        invoker.RegisterEvent<LgrEvents.BotFriendRequestEvent>(async (_, e) =>
            await PublishAsync(new Sdk.FriendRequestEvent
            {
                Platform = LagrangeMapper.PlatformId,
                SelfId = LagrangeMapper.SelfId,
                Raw = e,
                UserId = e.InitiatorUin.ToString(),
                Comment = e.Message,
                Token = e.InitiatorUid
            }));
        invoker.RegisterEvent<BotGroupJoinNotificationEvent>(async (_, e) =>
        {
            var notification = e.Notification;
            await PublishAsync(new PlatformEvent
            {
                Platform = LagrangeMapper.PlatformId,
                SelfId = LagrangeMapper.SelfId,
                Kind = QqEventKinds.GroupJoinRequest,
                Channel = GroupChannel(notification.GroupUin),
                Raw = new QqGroupJoinRequest
                {
                    Time = DateTimeOffset.Now,
                    SelfId = SelfUin(),
                    GroupId = notification.GroupUin,
                    NotificationSeq = (long)notification.Sequence,
                    InitiatorId = notification.TargetUin,
                    Comment = notification.Comment,
                    IsFiltered = notification.IsFiltered
                }
            });
        });
        invoker.RegisterEvent<BotGroupInviteNotificationEvent>(async (_, e) =>
        {
            var notification = e.Notification;
            await PublishAsync(new PlatformEvent
            {
                Platform = LagrangeMapper.PlatformId,
                SelfId = LagrangeMapper.SelfId,
                Kind = QqEventKinds.GroupInvitedJoinRequest,
                Channel = GroupChannel(notification.GroupUin),
                Raw = new QqGroupInvitedJoinRequest
                {
                    Time = DateTimeOffset.Now,
                    SelfId = SelfUin(),
                    GroupId = notification.GroupUin,
                    NotificationSeq = (long)notification.Sequence,
                    InitiatorId = notification.InviterUin,
                    TargetUserId = notification.TargetUin
                }
            });
        });
        invoker.RegisterEvent<BotGroupNudgeEvent>(async (_, e) => await PublishAsync(new PlatformEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = LagrangeMapper.SelfId,
            Kind = QqEventKinds.GroupNudge,
            Channel = GroupChannel(e.GroupUin),
            Raw = new QqGroupNudge
            {
                Time = DateTimeOffset.Now,
                SelfId = SelfUin(),
                GroupId = e.GroupUin,
                SenderId = e.OperatorUin,
                ReceiverId = e.TargetUin,
                DisplayAction = e.Action,
                DisplaySuffix = e.Suffix
            }
        }));
        invoker.RegisterEvent<BotGroupReactionEvent>(async (_, e) => await PublishAsync(new PlatformEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = LagrangeMapper.SelfId,
            Kind = QqEventKinds.GroupMessageReaction,
            Channel = GroupChannel(e.TargetGroupUin),
            Raw = new QqGroupMessageReaction
            {
                Time = DateTimeOffset.Now,
                SelfId = SelfUin(),
                GroupId = e.TargetGroupUin,
                UserId = e.OperatorUin,
                MessageSeq = (long)e.TargetSequence,
                FaceId = e.Code,
                IsAdd = e.IsAdd
            }
        }));
        invoker.RegisterEvent<LgrEvents.BotOfflineEvent>(async (_, e) => await PublishAsync(new Sdk.BotOfflineEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = LagrangeMapper.SelfId,
            Raw = e,
            Reason = $"{e.Reason}: {e.Tips?.Message}"
        }));
    }

    private async Task OnMessageAsync(BotMessageEvent e)
    {
        _session?.CacheMessage(e.Message);
        await PublishAsync(LagrangeMapper.ToMessageEvent(e.Message)).ConfigureAwait(false);
    }

    private async Task PublishAsync(Sdk.BotEvent botEvent)
    {
        if (EventReceived is { } handler)
        {
            await handler(botEvent).ConfigureAwait(false);
        }
    }

    private long SelfUin() => _session?.Context.BotUin ?? 0;

    private static Sdk.Channel GroupChannel(long groupUin) =>
        new(groupUin.ToString(), ChannelType.Group) { GuildId = groupUin.ToString() };
}
