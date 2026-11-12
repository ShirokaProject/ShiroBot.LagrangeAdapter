using Lagrange.Core;
using Lagrange.Core.Events;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Message.Entities;
using Lagrange.Core.Events.EventArgs;
using ShiroBot.Model.QQ;
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

    public void Detach() => _session = null;

    public void Attach(LagrangeSession session)
    {
        _session = session;
        var invoker = session.Context.EventInvoker;

        // 注意:Lagrange 断言 handler 不能是静态方法,这里全部用实例 lambda 委托
        invoker.RegisterEvent<BotMessageEvent>(async (_, e) => await OnMessageAsync(e));
        invoker.RegisterEvent<BotGroupRecallEvent>(async (_, e) => await PublishAsync(new MessageDeletedEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = _session?.ActiveContext?.BotUin.ToString(),
            Raw = e,
            MessageId = e.Sequence.ToString(),
            Channel = GroupChannel(e.GroupUin),
            SenderId = e.AuthorUin.ToString(),
            OperatorId = e.OperatorUin.ToString()
        }));
        invoker.RegisterEvent<BotFriendRecallEvent>(async (_, e) => await PublishAsync(new MessageDeletedEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = _session?.ActiveContext?.BotUin.ToString(),
            Raw = e,
            MessageId = e.Sequence.ToString(),
            Channel = Sdk.Channel.Direct(e.PeerUin.ToString()),
            SenderId = e.AuthorUin.ToString()
        }));
        invoker.RegisterEvent<BotGroupMemberIncreaseEvent>(async (_, e) => await PublishAsync(new MemberJoinedEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = _session?.ActiveContext?.BotUin.ToString(),
            Raw = e,
            Channel = GroupChannel(e.GroupUin),
            UserId = e.MemberUin.ToString(),
            OperatorId = e.OperatorUin != 0 ? e.OperatorUin.ToString()
                : e.InvitorUin != 0 ? e.InvitorUin.ToString() : null
        }));
        invoker.RegisterEvent<BotGroupMemberDecreaseEvent>(async (_, e) => await PublishAsync(new MemberLeftEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = _session?.ActiveContext?.BotUin.ToString(),
            Raw = e,
            Channel = GroupChannel(e.GroupUin),
            UserId = e.UserUin.ToString(),
            OperatorId = e.OperatorUin?.ToString()
        }));
        invoker.RegisterEvent<LgrEvents.BotFriendRequestEvent>(async (_, e) =>
            await PublishAsync(new Sdk.FriendRequestEvent
            {
                Platform = LagrangeMapper.PlatformId,
                SelfId = _session?.ActiveContext?.BotUin.ToString(),
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
                SelfId = _session?.ActiveContext?.BotUin.ToString(),
                Kind = QEventKinds.GroupJoinRequest,
                Channel = GroupChannel(notification.GroupUin),
                Raw = new QGroupJoinRequest
                {
                    Time = DateTimeOffset.Now,
                    SelfId = (SelfUin()).ToString(),
                    GroupId = (notification.GroupUin).ToString(),
                    RequestId = ((long)notification.Sequence).ToString(),
                    UserId = (notification.TargetUin).ToString(),
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
                SelfId = _session?.ActiveContext?.BotUin.ToString(),
                Kind = QEventKinds.GroupJoinRequest,
                Channel = GroupChannel(notification.GroupUin),
                Raw = new QGroupJoinRequest
                {
                    Time = DateTimeOffset.Now,
                    SelfId = (SelfUin()).ToString(),
                    GroupId = (notification.GroupUin).ToString(),
                    RequestId = ((long)notification.Sequence).ToString(),
                    InviterId = notification.InviterUin.ToString(),
                    IsInvited = true,
                    UserId = (notification.TargetUin).ToString()
                }
            });
        });
        invoker.RegisterEvent<BotGroupNudgeEvent>(async (_, e) => await PublishAsync(new PlatformEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = _session?.ActiveContext?.BotUin.ToString(),
            Kind = QEventKinds.GroupNudge,
            Channel = GroupChannel(e.GroupUin),
            Raw = new QGroupNudge
            {
                Time = DateTimeOffset.Now,
                SelfId = (SelfUin()).ToString(),
                GroupId = (e.GroupUin).ToString(),
                SenderId = (e.OperatorUin).ToString(),
                ReceiverId = (e.TargetUin).ToString(),
                DisplayAction = e.Action,
                DisplaySuffix = e.Suffix
            }
        }));
        invoker.RegisterEvent<BotGroupReactionEvent>(async (_, e) => await PublishAsync(new PlatformEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = _session?.ActiveContext?.BotUin.ToString(),
            Kind = QEventKinds.GroupMessageReaction,
            Channel = GroupChannel(e.TargetGroupUin),
            Raw = new QGroupMessageReaction
            {
                Time = DateTimeOffset.Now,
                SelfId = (SelfUin()).ToString(),
                GroupId = (e.TargetGroupUin).ToString(),
                UserId = (e.OperatorUin).ToString(),
                MessageId = ((long)e.TargetSequence).ToString(),
                FaceId = e.Code,
                IsAdd = e.IsAdd
            }
        }));
        invoker.RegisterEvent<LgrEvents.BotOfflineEvent>(async (_, e) => await PublishAsync(new Sdk.BotOfflineEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = _session?.ActiveContext?.BotUin.ToString(),
            Raw = e,
            Reason = $"{e.Reason}: {e.Tips?.Message}"
        }));
    }

    private async Task OnMessageAsync(BotMessageEvent e)
    {
        _session?.CacheMessage(e.Message);
        await PublishAsync(LagrangeMapper.ToMessageEvent(e.Message, _session?.ActiveContext?.BotUin.ToString())).ConfigureAwait(false);
        if (e.Message.Contact is BotGroupMember member)
        {
            foreach (var file in e.Message.Entities.OfType<GroupFileEntity>())
                await PublishAsync(new PlatformEvent
                {
                    Platform = LagrangeMapper.PlatformId,
                    SelfId = _session?.ActiveContext?.BotUin.ToString(),
                    Kind = QEventKinds.GroupFileUpload,
                    Channel = GroupChannel(member.Group.GroupUin),
                    Raw = new QGroupFileUpload
                    {
                        SelfId = _session?.ActiveContext?.BotUin.ToString(),
                        Time = DateTimeOffset.FromUnixTimeSeconds(e.Message.Time),
                        GroupId = member.Group.GroupUin.ToString(),
                        UserId = member.Uin.ToString(),
                        FileId = file.FileId,
                        FileName = file.FileName,
                        FileSize = file.FileSize
                    }
                }).ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(Sdk.BotEvent botEvent)
    {
        if (EventReceived is { } handler)
        {
            foreach (Func<Sdk.BotEvent, Task> subscriber in handler.GetInvocationList())
                await subscriber(botEvent).ConfigureAwait(false);
        }
    }

    private long SelfUin() => _session?.Context.BotUin ?? 0;

    private static Sdk.Channel GroupChannel(long groupUin) =>
        new(groupUin.ToString(), ChannelType.Group) { GuildId = groupUin.ToString() };
}
