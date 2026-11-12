using System.Text.Json.Serialization;
using Lagrange.Core.Common;

namespace ShiroBot.LagrangeAdapter;

// Generated accessors avoid the framework reflection cache retaining collectible types.
[JsonSerializable(typeof(BotAppInfo))]
[JsonSerializable(typeof(BotKeystore))]
internal partial class LagrangeJsonContext : JsonSerializerContext;
