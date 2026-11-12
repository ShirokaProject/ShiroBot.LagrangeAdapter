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
        Console.WriteLine("Cross-instance AppInfo/session JSON cache unload check passed.");
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
        return context;
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
