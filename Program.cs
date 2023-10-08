using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace TmlNetcoredbgPatcher;

public class Program {
    public static void Main(string[] args) {
        string tmlPath = "";
        string outputPath = "";

        for (int i = 0; i < args.Length; i++) {
            string arg = args[i];

            switch (arg) {
                case "-p":
                case "--path":
                    i++;
                    tmlPath = args[i];
                    break;
                case "-o":
                case "--output":
                    i++;
                    outputPath = args[i];
                    break;
                default:
                    throw new Exception("Unknown argument " + arg);
            }
        }

        if (string.IsNullOrEmpty(tmlPath))
            tmlPath = TmlPath();

        if (!File.Exists(tmlPath))
            throw new FileNotFoundException("Unable to locate tModLoader, please pass your tModLoader.dll location using --path", tmlPath);


        File.Delete(tmlPath + ".bak");

        using var resolver = new DefaultAssemblyResolver();
        Directory.GetFiles(Path.GetDirectoryName(tmlPath)!, "*.dll", SearchOption.AllDirectories).ToList().ForEach(p => resolver.AddSearchDirectory(Path.GetDirectoryName(p)));

        ModuleDefinition md = ModuleDefinition.ReadModule(tmlPath, new ReaderParameters { AssemblyResolver = resolver });
        TypeDefinition assemblyManager = md.GetType("Terraria.ModLoader.Core.AssemblyManager");
        TypeDefinition modLoadContext = md.GetType("Terraria.ModLoader.Core.AssemblyManager/ModLoadContext");
        MethodDefinition loadAssemblies = modLoadContext.FindMethod("LoadAssemblies")!;
        ILContext il = new(loadAssemblies);
        ILCursor c = new(il);

        c.GotoNext(MoveType.Before,
            i => i.MatchLdarg0(),
            i => i.MatchLdarg0(),
            i => i.MatchLdfld(modLoadContext.FindField("modFile")!),
            i => i.MatchCall(assemblyManager.FindMethod("GetModAssembly")!),
            i => i.MatchLdarg0(),
            i => i.MatchLdfld(modLoadContext.FindField("properties")!),
            i => i.MatchLdfld(md.GetType("Terraria.ModLoader.Core.BuildProperties").FindField("eacPath")!),
            i => i.MatchCall(typeof(System.IO.File).GetMethod("ReadAllBytes", BindingFlags.Public | BindingFlags.Static)!)
        );
        c.RemoveRange(9);

        c.Emit(OpCodes.Ldarg_0);

        c.Emit(OpCodes.Ldarg_0);
        c.Emit(OpCodes.Ldfld, modLoadContext.FindField("properties")!);
        c.Emit(OpCodes.Ldfld, md.GetType("Terraria.ModLoader.Core.BuildProperties").FindField("eacPath")!);

        c.Emit(OpCodes.Ldstr, ".dll");

        c.Emit(OpCodes.Call, typeof(System.IO.Path).GetMethod("ChangeExtension", BindingFlags.Public | BindingFlags.Static)!);

        c.Emit(OpCodes.Call, typeof(AssemblyLoadContext).GetMethod("LoadFromAssemblyPath", BindingFlags.Public | BindingFlags.Instance)!);

        using MemoryStream ms = new();
        md.Write(ms);
        md.Dispose();

        File.Move(tmlPath, tmlPath + ".bak");

        using FileStream fs = new(string.IsNullOrEmpty(outputPath) ? tmlPath : outputPath, FileMode.Create);
        ms.Position = 0;
        ms.CopyTo(fs);
    }

    public static string TmlPath() {
        if (OperatingSystem.IsWindows())
            return "C:\\Program Files\\Steam\\steamapps\\common\\tModLoader\\tModLoader.dll"; // This shouldn't break right??
        else if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library/Application Support/Steam/steamapps/common/tModLoader/tModLoader.dll");
        else if (OperatingSystem.IsLinux()) {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, "Steam/steamapps/common/tModLoader/tModLoader.dll");
            else
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share", "Steam/steamapps/common/tModLoader/tModLoader.dll");
        }

        throw new PlatformNotSupportedException("Unknown or unsupported platform!");
    }
}
