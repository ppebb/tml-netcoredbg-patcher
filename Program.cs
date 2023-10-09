using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace TmlNetcoredbgPatcher;

public class Program {
    public static void Main(string[] args) {
        string tmlDirectory = "";
        string outputPath = "";

        for (int i = 0; i < args.Length; i++) {
            string arg = args[i];

            switch (arg) {
                case "-p":
                case "--path":
                    i++;
                    tmlDirectory = args[i];
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

        if (string.IsNullOrEmpty(tmlDirectory))
            tmlDirectory = TmlDirectory();

        string tmlPath = Path.Combine(tmlDirectory, "tModLoader.dll");

        Console.WriteLine($"Using tModLoader at {tmlPath}");

        if (!File.Exists(tmlPath))
            throw new FileNotFoundException("Unable to locate tModLoader, please pass your tModLoader directory using --path", tmlPath);

        Console.WriteLine($"Found tModLoader at {tmlPath}");

        using var resolver = new DefaultAssemblyResolver();
        Directory.GetFiles(tmlDirectory, "*.dll", SearchOption.AllDirectories).ToList().ForEach(p => resolver.AddSearchDirectory(Path.GetDirectoryName(p)));
        ModuleDefinition md = ModuleDefinition.ReadModule(tmlPath, new ReaderParameters { AssemblyResolver = resolver });
        ModuleDefinition log4net = ModuleDefinition.ReadModule(Path.Combine(tmlDirectory, "Libraries", "log4net", "2.0.8.0", "log4net.dll"));

        PatchModLoadContext(md, log4net);

        PatchMain(md);

        using MemoryStream ms = new();
        md.Write(ms);
        md.Dispose();

        File.Delete(tmlPath + ".bak");
        File.Move(tmlPath, tmlPath + ".bak");
        Console.WriteLine($"Backup created at {tmlPath + ".bak"}");

        using FileStream fs = new(string.IsNullOrEmpty(outputPath) ? tmlPath : outputPath, FileMode.Create);
        ms.Position = 0;
        ms.CopyTo(fs);

        Console.WriteLine($"{tmlPath} patched successfully, and written to {(string.IsNullOrEmpty(outputPath) ? tmlPath : outputPath)}");
    }

    public static void PatchModLoadContext(ModuleDefinition md, ModuleDefinition log4net) {
        Console.WriteLine("Patching Terraria.ModLoader.Core.AssemblyManager/ModLoadContext");
        TypeDefinition assemblyManager = md.GetType("Terraria.ModLoader.Core.AssemblyManager");
        TypeDefinition modLoadContext = md.GetType("Terraria.ModLoader.Core.AssemblyManager/ModLoadContext");
        MethodDefinition loadAssemblies = FindMethod(modLoadContext, "LoadAssemblies")!;
        FieldDefinition properties = FindField(modLoadContext, "properties");
        FieldDefinition eacPath = FindField(md.GetType("Terraria.ModLoader.Core.BuildProperties"), "eacPath");
        ILContext il = new(loadAssemblies);

        // This allows monomod to fix all of the labels apparently
        il.Invoke(new ILContext.Manipulator(ManipCtx));

        void ManipCtx(ILContext il) {
            ILCursor c = new(il);

            MethodInfo loadFromAssemblyPath = typeof(AssemblyLoadContext).GetMethod("LoadFromAssemblyPath", BindingFlags.Public | BindingFlags.Instance)!;
            if (c.TryGotoNext(MoveType.After, i => i.MatchCall(loadFromAssemblyPath)))
                throw new Exception("tModLoader is already patched");

            c.GotoNext(MoveType.After,
                i => i.MatchLdloc3(),
                i => i.MatchLdloc2(),
                i => i.MatchLdlen(),
                i => i.MatchConvI4(),
                i => i.MatchBlt(out _)
            );

            VariableDefinition tmlLogger = new(il.Module.TypeSystem.Object);
            il.Body.Variables.Add(tmlLogger);
            var tmlLoggerIdx = tmlLogger.Index;

            // ILog tml = LogManager.GetLogger("tML");
            c.Emit(OpCodes.Ldstr, "tML");
            c.Emit(OpCodes.Call, md.ImportReference(FindMethod(log4net.Types.FirstOrDefault(t => t.Name == "LogManager")!, "GetLogger")));
            c.Emit(OpCodes.Stloc, tmlLoggerIdx);

            c.GotoNext(MoveType.After,
                i => i.MatchCall(typeof(System.IO.File).GetMethod("Exists", BindingFlags.Public | BindingFlags.Static)!)
            );

            // Remove the existing brtrue.s because it points to the wrong label, need to create a new one since I move so much stuff around
            ILLabel foundPdb = c.DefineLabel();
            c.Remove();
            c.Emit(OpCodes.Brtrue_S, foundPdb);

            // Debugger.IsAttached && File.Exists(this.properties.eacPath);
            ILLabel skipDebug = c.DefineLabel();
            c.Emit(OpCodes.Call, typeof(System.Diagnostics.Debugger).GetMethod("get_IsAttached", BindingFlags.Public | BindingFlags.Static)!);
            c.Emit(OpCodes.Brfalse_S, skipDebug);

            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldfld, properties);
            c.Emit(OpCodes.Ldfld, eacPath);
            c.Emit(OpCodes.Call, typeof(string).GetMethod("IsNullOrEmpty", BindingFlags.Public | BindingFlags.Static)!);
            c.Emit(OpCodes.Brtrue_S, skipDebug);

            // tml.Debug("Located pdb at " + this.properties.eacPath + ", but the file was not found");
            c.Emit(OpCodes.Ldloc, tmlLoggerIdx);
            c.Emit(OpCodes.Ldstr, "Located pdb at ");
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldfld, properties);
            c.Emit(OpCodes.Ldfld, eacPath);
            c.Emit(OpCodes.Ldstr, ", but the file was not found");
            c.Emit(OpCodes.Call, typeof(System.String).GetMethod("Concat", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(string), typeof(string), typeof(string) })!);
            c.Emit(OpCodes.Callvirt, md.ImportReference(FindMethod(log4net.Types.FirstOrDefault(t => t.Name == "ILog")!, "Debug")));

            c.MarkLabel(skipDebug);

            c.GotoNext(MoveType.Before,
                i => i.MatchLdarg0(),
                i => i.MatchLdarg0(),
                i => i.MatchLdfld(FindField(modLoadContext, "modFile")),
                i => i.MatchCall(FindMethod(assemblyManager, "GetModAssembly")!),
                i => i.MatchLdarg0(),
                i => i.MatchLdfld(properties),
                i => i.MatchLdfld(eacPath),
                i => i.MatchCall(typeof(System.IO.File).GetMethod("ReadAllBytes", BindingFlags.Public | BindingFlags.Static)!)
            );
            c.RemoveRange(9);

            // tml.Debug("Located pdb at " + this.properties.eacPath + ", loading assembly from disk");
            c.MarkLabel(foundPdb);
            c.Emit(OpCodes.Ldloc, tmlLoggerIdx);
            c.Emit(OpCodes.Ldstr, "Located pdb at ");
            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldfld, properties);
            c.Emit(OpCodes.Ldfld, eacPath);
            c.Emit(OpCodes.Ldstr, ", loading assembly from disk");
            c.Emit(OpCodes.Call, typeof(System.String).GetMethod("Concat", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(string), typeof(string), typeof(string) })!);
            c.Emit(OpCodes.Callvirt, md.ImportReference(FindMethod(log4net.Types.FirstOrDefault(t => t.Name == "ILog")!, "Debug")));

            // base.LoadFromAssemblyPath(Path.ChangeExtension(this.properties.eacPath, ".dll"));
            c.Emit(OpCodes.Ldarg_0);

            c.Emit(OpCodes.Ldarg_0);
            c.Emit(OpCodes.Ldfld, properties);
            c.Emit(OpCodes.Ldfld, eacPath);

            c.Emit(OpCodes.Ldstr, ".dll");
            c.Emit(OpCodes.Call, typeof(System.IO.Path).GetMethod("ChangeExtension", BindingFlags.Public | BindingFlags.Static)!);

            c.Emit(OpCodes.Call, loadFromAssemblyPath);
        }

        Console.WriteLine("Terraria.ModLoader.Core.AssemblyManager/ModLoadContext patched successfully");
    }

    public static void PatchMain(ModuleDefinition md) {
        Console.WriteLine("Patching Terraria.Main");
        TypeDefinition main = md.GetType("Terraria.Main");
        MethodDefinition drawVersionNumber = FindMethod(main, "DrawVersionNumber");
        ILContext il = new(drawVersionNumber);
        ILCursor c = new(il);

        if (c.TryGotoNext(MoveType.After, i => i.MatchLdstr("Patched by ppeb!\n")))
            throw new Exception("tModLoader is already patched");

        c.GotoNext(MoveType.Before,
            i => i.MatchLdsfld(FindField(md.GetType("Terraria.GameContent.FontAssets"), "MouseText")),
            i => i.MatchCallvirt(out _),
            i => i.MatchLdloc3(),
            i => i.MatchCallvirt(out _),
            i => i.MatchStloc(4)
        );

        c.Emit(OpCodes.Ldstr, "Patched by ppeb!\n");
        c.Emit(OpCodes.Ldloc_3);
        c.Emit(OpCodes.Call, typeof(System.String).GetMethod("Concat", BindingFlags.Public | BindingFlags.Static, new Type[] { typeof(string), typeof(string) })!);
        c.Emit(OpCodes.Stloc_3);

        Console.WriteLine("Terraria.Main patched successfully");
    }

    public static FieldDefinition FindField(TypeDefinition t, string f) {
        FieldDefinition? field = t.FindField(f);

        if (field == null)
            throw new Exception($"Field {f} not found in {t}, available fields are\n{string.Join("\n", t.Fields.Select(f => f.Name))}");

        return field;
    }

    public static MethodDefinition FindMethod(TypeDefinition t, string f) {
        MethodDefinition? method = t.FindMethod(f);

        if (method == null)
            throw new Exception($"Method {f} not found in {t}, available methods are:\n{string.Join("\n", t.Methods.Select(m => m.Name))}");

        return method;
    }

    public static string TmlDirectory() {
        if (OperatingSystem.IsWindows())
            return "C:\\Program Files (x86)\\Steam\\steamapps\\common\\tModLoader"; // This shouldn't break right??
        else if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library/Application Support/Steam/steamapps/common/tModLoader");
        else if (OperatingSystem.IsLinux()) {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");

            if (!string.IsNullOrEmpty(xdg))
                return Path.Combine(xdg, "Steam/steamapps/common/tModLoader");
            else
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share", "Steam/steamapps/common/tModLoader");
        }

        throw new PlatformNotSupportedException("Unknown or unsupported platform");
    }
}
