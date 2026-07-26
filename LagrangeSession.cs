using System.Collections.Concurrent;
using Lagrange.Core;
using Lagrange.Core.Message;

namespace ShiroBot.LagrangeAdapter;

/// <summary>
/// 适配器会话状态:BotContext 单例 + 近期消息缓存。
/// Lagrange 的回复/撤回都需要原始 BotMessage 对象,因此按 sequence 缓存近期消息。
/// </summary>
internal sealed class LagrangeSession
{
    private const int MaxCachedMessages = 4096;

    private readonly ConcurrentDictionary<ulong, BotMessage> _messages = new();
    private readonly ConcurrentQueue<ulong> _order = new();

    public required BotContext Context { get; init; }

    public void CacheMessage(BotMessage message)
    {
        if (_messages.TryAdd(message.Sequence, message))
        {
            _order.Enqueue(message.Sequence);
            while (_messages.Count > MaxCachedMessages && _order.TryDequeue(out var oldest))
            {
                _messages.TryRemove(oldest, out _);
            }
        }
        else
        {
            _messages[message.Sequence] = message;
        }
    }

    public BotMessage? FindMessage(ulong sequence) =>
        _messages.GetValueOrDefault(sequence);

    public Task<BotMessage?> ResolveMessageAsync(string messageId) =>
        Task.FromResult(ulong.TryParse(messageId, out var sequence) ? FindMessage(sequence) : null);
}
