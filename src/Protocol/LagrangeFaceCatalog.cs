using System.Runtime.CompilerServices;
using Lagrange.Core;
using Lagrange.Core.Message.Entities;

namespace ShiroBot.LagrangeAdapter;

internal static class LagrangeFaceCatalog
{
    private static readonly ConditionalWeakTable<BotContext, Lazy<Task<Dictionary<int, List<ProtoWire.Field>>>>> Cache = new();
    internal static async Task<FaceEntity> Create(BotContext? context, int id, bool large)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        if (id < 260) return new FaceEntity(id);
        if (context is null) throw new InvalidOperationException("新版表情需要在线设备的表情目录。");
        var entry = Cache.GetValue(context, bot => new Lazy<Task<Dictionary<int, List<ProtoWire.Field>>>>(() => Load(bot)));
        Dictionary<int, List<ProtoWire.Field>> catalog;
        try { catalog = await entry.Value; }
        catch { Cache.Remove(context); throw; }
        if (!catalog.TryGetValue(id, out var face)) throw new ArgumentException("QQ 表情 ID 不存在。", nameof(id));
        return new FaceEntity(id, large, ProtoWire.String(face, 2), checked((uint)ProtoWire.Number(face, 6)),
            checked((uint)ProtoWire.Number(face, 7)), checked((uint)ProtoWire.Number(face, 5)));
    }
    private static async Task<Dictionary<int, List<ProtoWire.Field>>> Load(BotContext bot)
    {
        var data = await new LagrangeProtocol(bot).Oidb(0x9154, 1, new ProtoWire().UInt(1, 0).UInt(2, 7).Text(3, "0"), CancellationToken.None);
        var result = new Dictionary<int, List<ProtoWire.Field>>();
        void Add(IEnumerable<ProtoWire.Field> faces)
        {
            foreach (var raw in faces.Where(f => f.Number == 2 && f.Wire == 2))
            {
                var face = ProtoWire.Parse(raw.Bytes);
                if (int.TryParse(ProtoWire.String(face, 1), out var id)) result[id] = face;
            }
        }
        foreach (var section in data.Where(f => f.Number is 2 or 3 && f.Wire == 2))
        foreach (var pack in ProtoWire.Parse(section.Bytes).Where(f => f.Number == 1 && f.Wire == 2)) Add(ProtoWire.Parse(pack.Bytes));
        Add(ProtoWire.Child(ProtoWire.Child(data, 4), 1));
        return result;
    }
}
