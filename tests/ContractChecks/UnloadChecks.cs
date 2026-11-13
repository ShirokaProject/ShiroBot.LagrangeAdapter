using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace ContractChecks;

internal static class UnloadChecks
{
    internal static void Run(string assemblyPath)
    {
        var retired = LoadAndUnload(assemblyPath);
        var active = Load(assemblyPath);
        try
        {
            for (var attempt = 0; attempt < 50 && retired.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(100);
            }
            if (retired.IsAlive)
                throw new Exception("A running adapter's JSON cache retained an unloaded adapter assembly.");
            GC.KeepAlive(active);
        }
        finally
        {
            active.Unload();
        }
        Console.WriteLine("Cross-instance AppInfo/session cache and pending-request timer unload checks passed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndUnload(string path)
    {
        var context = Load(path);
        var reference = new WeakReference(context);
        context.Unload();
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CheckLoadContext Load(string path)
    {
        var context = new CheckLoadContext(Path.GetDirectoryName(path)!);
        var assembly = context.LoadFromAssemblyPath(path);
        var options = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeConnectionOptions")!;
        var resolve = options.GetMethod("ResolveAppInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
        resolve.Invoke(null, [Enum.Parse(resolve.GetParameters()[0].ParameterType, "Linux"), null]);
        var storage = assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeStorage")!;
        var save = storage.GetMethod("SaveSession", BindingFlags.NonPublic | BindingFlags.Static)!;
        var keystoreType = save.GetParameters()[1].ParameterType;
        var keystore = keystoreType.GetMethod("CreateEmpty")!.Invoke(null, null)!;
        var config = new StoredConfigContext(Path.Combine(Path.GetTempPath(), "lagrange-unload-check", "config.toml"));
        save.Invoke(null, [config, keystore]);
        var adapterConfig = Activator.CreateInstance(assembly.GetType("ShiroBot.LagrangeAdapter.LagrangeAdapterConfig")!);
        storage.GetMethod("ReadSession", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [config, adapterConfig]);
        CheckPendingCleanup(assembly);
        return context;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckPendingCleanup(Assembly assembly)
    {
        // Exercise the exact state that previously kept an online adapter loaded for 30 seconds.
        // No packet is sent and no network connection is opened.
        var core = assembly.GetType("Lagrange.Core.Common.BotConfig")?.Assembly ??
            AssemblyLoadContext.GetLoadContext(assembly)!.LoadFromAssemblyName(new AssemblyName("Lagrange.Core"));
        var config = Activator.CreateInstance(core.GetType("Lagrange.Core.Common.BotConfig")!)!;
        var create = core.GetType("Lagrange.Core.Common.Interface.BotFactory")!.GetMethods()
            .Single(method => method.Name == "Create" && method.GetParameters().Length == 2);
        var bot = create.Invoke(null, [config, null])!;
        var owner = bot.GetType().GetProperty("PacketContext", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bot)!;
        var pendingType = owner.GetType().GetNestedType("PendingRequest", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(pendingType, BindingFlags.Instance | BindingFlags.NonPublic, null, [owner, 123], null)!;
        var dictionary = owner.GetType().GetField("_pendingTasks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        if (!(bool)dictionary.GetType().GetMethod("TryAdd")!.Invoke(dictionary, [123, pending])!) throw new Exception("Pending request setup failed.");
        pendingType.GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pending, null);
        ((IDisposable)bot).Dispose();
        if ((int)dictionary.GetType().GetProperty("Count")!.GetValue(dictionary)! != 0)
            throw new Exception("Disposing a bot left pending requests alive.");
        // Disposing before Start is also valid when a disconnect races with a new request.
        pendingType.GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(pending, null);
    }

    private sealed class CheckLoadContext(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "ShiroBot.SDK" or "ShiroBot.Model.QQ" or "ShiroBot.Model.Discord" or "ShiroBot.Model.Telegram")
                return Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == name.Name);
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
