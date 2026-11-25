// Stampa le firme dei membri di un tipo interop. Uso: sig.sh <Assembly> <regex nome tipo> [regex membro]
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

static class Sig
{
    static string N(Type t) => t.IsGenericType ? t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(N)) + ">" : t.Name;
    static int Main(string[] a)
    {
        string game = Environment.GetEnvironmentVariable("GAME");
        string[] dirs = { Path.Combine(game, "BepInEx/interop"), Path.Combine(game, "BepInEx/core") };
        AssemblyLoadContext.Default.Resolving += (c, n) =>
        {
            foreach (var d in dirs) { var p = Path.Combine(d, n.Name + ".dll"); if (File.Exists(p)) return c.LoadFromAssemblyPath(p); }
            return null;
        };
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dirs[0], a[0] + ".dll"));
        var tr = new Regex(a[1]); var mr = new Regex(a.Length > 2 ? a[2] : ".");
        Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in types.Where(t => tr.IsMatch(t.FullName ?? t.Name)))
        {
            Console.WriteLine($"== {t.FullName} : {(t.BaseType == null ? "" : N(t.BaseType))}{(t.IsEnum ? " [enum " + string.Join(",", Enum.GetNames(t).Zip(Enum.GetValues(t).Cast<object>(), (n, v) => n + "=" + Convert.ToInt64(v))) + "]" : "")}");
            if (t.IsEnum) continue;
            foreach (var m in t.GetMembers(F).Where(m => mr.IsMatch(m.Name) && !m.Name.StartsWith("NativeMethodInfoPtr") && !m.Name.StartsWith("NativeFieldInfoPtr")))
            {
                try
                {
                    switch (m)
                    {
                        case MethodInfo mi when !mi.IsSpecialName || mi.Name.StartsWith("op_"):
                            Console.WriteLine($"  {(mi.IsStatic ? "static " : "")}{(mi.IsVirtual ? "virtual " : "")}{N(mi.ReturnType)} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => N(p.ParameterType) + " " + p.Name))})"); break;
                        case ConstructorInfo ci: Console.WriteLine($"  .ctor({string.Join(", ", ci.GetParameters().Select(p => N(p.ParameterType) + " " + p.Name))})"); break;
                        case PropertyInfo pi: Console.WriteLine($"  prop {N(pi.PropertyType)} {pi.Name} {{{(pi.CanRead ? "get;" : "")}{(pi.CanWrite ? "set;" : "")}}}"); break;
                    }
                }
                catch (Exception e) { Console.WriteLine($"  ?? {m.Name}: {e.GetType().Name}"); }
            }
        }
        return 0;
    }
}
