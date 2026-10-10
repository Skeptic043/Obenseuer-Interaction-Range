using System;
using System.IO;
using System.Reflection;

internal static class Program
{
    private static string gameManaged;
    private static string bepinexCore;

    private static int Main(string[] args)
    {
        if (args.Length != 2) { Console.Error.WriteLine("Supply GamePath and BepInExCorePath."); return 2; }
        gameManaged = Path.Combine(args[0], "Obenseuer_Data", "Managed");
        bepinexCore = args[1];
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try { return Tests.Run(); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static Assembly Resolve(object sender, ResolveEventArgs args)
    {
        string name = new AssemblyName(args.Name).Name + ".dll";
        foreach (var directory in new[] { bepinexCore, gameManaged })
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }
}
