// JIT check for a patched DamageMeter.dll. Loads it the way the meter would (dependencies from the
// meter folder, framework from the running .NET) and compiles every method of the patch code plus
// the meter methods the patch rewrites. Exit 0 = the patch can run on this .NET, 1 = it cannot.
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

if (args.Length < 2) { Console.Error.WriteLine("usage: JitCheck <meter folder> <patched DamageMeter.dll>"); return 2; }
string meter = Path.GetFullPath(args[0]);
string dll = Path.GetFullPath(args[1]);
Console.WriteLine($"runtime .NET {Environment.Version}  meter {meter}");

var ctx = new AssemblyLoadContext("meter", isCollectible: false);
ctx.Resolving += (c, name) =>
{
    string p = Path.Combine(meter, name.Name + ".dll");
    return File.Exists(p) ? c.LoadFromAssemblyPath(p) : null;
};

Assembly asm;
try { asm = ctx.LoadFromStream(new MemoryStream(File.ReadAllBytes(dll))); }
catch (Exception e) { Console.WriteLine("FAIL load: " + e.Message); return 1; }

// The patch's own types plus the meter methods it injects calls into.
var targets = new List<MethodBase>();
Type[] types;
try { types = asm.GetTypes(); }
catch (ReflectionTypeLoadException e)
{
    // A type that cannot even load is the same failure the meter would hit, just earlier.
    foreach (var msg in e.LoaderExceptions.Where(x => x != null).Select(x => x!.Message).Distinct().Take(5))
        Console.WriteLine("FAIL type load: " + msg);
    return 1;
}

foreach (var t in types.Where(t => t.FullName!.StartsWith("ShinraRotationPatch.")))
    targets.AddRange(Methods(t));
foreach (var (type, name) in new[] { ("DamageMeter.DataExporter", "AutomatedExport"), ("DamageMeter.DataExporter", "CheckAndSendFightData") })
    if (asm.GetType(type) is { } t)
        targets.AddRange(Methods(t).Where(m => m.Name == name));

if (!targets.Any(m => m.DeclaringType!.FullName!.StartsWith("ShinraRotationPatch.")))
{ Console.WriteLine("FAIL: no ShinraRotationPatch code found in the DLL"); return 1; }

int failed = 0;
foreach (var m in targets)
{
    if (m.IsAbstract || m.ContainsGenericParameters) continue;
    try { RuntimeHelpers.PrepareMethod(m.MethodHandle); }
    catch (Exception e)
    {
        failed++;
        var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
        Console.WriteLine($"FAIL {m.DeclaringType!.Name}.{m.Name}: {inner.GetType().Name}: {inner.Message}");
    }
}
Console.WriteLine(failed == 0 ? $"OK   {targets.Count} methods compiled on .NET {Environment.Version.Major}" : $"{failed} method(s) cannot run on this .NET");
return failed == 0 ? 0 : 1;

static IEnumerable<MethodBase> Methods(Type t)
{
    const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    IEnumerable<MethodBase> own;
    try { own = t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)).ToList(); }
    catch { own = Array.Empty<MethodBase>(); }
    foreach (var m in own) yield return m;
    Type[] nested;
    try { nested = t.GetNestedTypes(all); } catch { nested = Array.Empty<Type>(); }
    foreach (var n in nested)
        foreach (var m in Methods(n)) yield return m;
}
