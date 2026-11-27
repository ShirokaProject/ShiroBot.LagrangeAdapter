using Lagrange.Core;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;

namespace ShiroBot.LagrangeAdapter;

/// <summary>
/// 适配器会话状态:BotContext 单例 + 近期消息缓存。
/// Lagrange 的回复/撤回都需要原始 BotMessage 对象,因此按 sequence 缓存近期消息。
/// </summary>
internal sealed class LagrangeSession
{
    private const int MaxCachedMessages = 4096;

    private readonly Lock _cacheLock = new();
    private readonly Dictionary<(long Peer, bool Group, ulong Sequence), BotMessage> _messages = new();
    private readonly Queue<(long Peer, bool Group, ulong Sequence)> _order = new();

    internal event Action<BotMessage>? MessagePushed;

    internal void CachePushedMessage(BotMessage message)
    {
        CacheMessage(message);
        MessagePushed?.Invoke(message);
    }

    public BotContext? ActiveContext { get; set; }
    public BotContext Context => ActiveContext ?? throw new InvalidOperationException("Lagrange 适配器尚未启动。");

    public void Clear()
    {
        lock (_cacheLock)
        {
            _messages.Clear();
            _order.Clear();
        }
    }

    public void CacheMessage(BotMessage message)
    {
        message = message.CreateCacheSnapshot();
        var key = (message.Contact is BotGroupMember member ? member.Group.GroupUin : message.Contact.Uin == ActiveContext?.BotUin ? message.Receiver.Uin : message.Contact.Uin, message.Type == MessageType.Group, message.Sequence);
        lock (_cacheLock)
        {
            if (_messages.TryGetValue(key, out var previous))
            {
                if (message.Contact.Uin != ActiveContext?.BotUin ||
                    !previous.Entities.OfType<VideoEntity>().Any(video => video.IsBubble) ||
                    message.Entities.OfType<VideoEntity>().Any(video => video.IsBubble))
                    _messages[key] = message;
                return;
            }

            _messages.Add(key, message);
            _order.Enqueue(key);
            if (_messages.Count > MaxCachedMessages)
                _messages.Remove(_order.Dequeue());
        }
    }

    public BotMessage? FindMessage(ulong sequence, long peer, bool group)
    {
        lock (_cacheLock)
            return _messages.GetValueOrDefault((peer, group, sequence));
    }

    public async Task<BotMessage?> ResolveMessageAsync(string messageId, long peer, bool group, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ulong.TryParse(messageId, out var sequence)) throw new ArgumentException("消息 ID 必须是序号。", nameof(messageId));
        if (FindMessage(sequence, peer, group) is { } cached) return cached;
        var messages = await (group ? Context.GetGroupMessage(peer, sequence, sequence)
            : Context.GetC2CMessage(peer, sequence, sequence)).WaitAsync(cancellationToken);
        foreach (var message in messages) CacheMessage(message);
        return messages.FirstOrDefault(x => x.Sequence == sequence || (!group && x.ClientSequence == sequence));
    }
}
