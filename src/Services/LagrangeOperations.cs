namespace ShiroBot.LagrangeAdapter.Services;

internal static class LagrangeOperations
{
    internal static uint MuteSeconds(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero || duration.TotalSeconds > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return checked((uint)Math.Ceiling(duration.TotalSeconds));
    }

    internal static async Task RequireSuccessAsync(Task<bool> operation, string name, CancellationToken token)
    {
        if (!await operation.WaitAsync(token)) throw new InvalidOperationException($"{name}失败：协议端返回 false。");
    }
}