using EnragedON.Setup.Core;
using System.Text.Json.Nodes;

// Installer test harness. Copies a real meter folder of each variant into %TEMP% and runs
// the real install/uninstall code against the copies. Nothing outside %TEMP% is written.
//
//   dotnet run --project installer/SetupTests
//   dotnet run --project installer/SetupTests -- --cp "<classic+ meter>" --tb "<toolbox meter>"
//
// Exit code 0 = everything passed.

int fail = 0, checks = 0;
void Check(string what, bool ok, string detail = "")
{
    checks++;
    if (!ok) fail++;
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(ok ? "  PASS  " : "  FAIL  ");
    Console.ResetColor();
    Console.WriteLine(what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
}
void Section(string title) { Console.WriteLine(); Console.WriteLine("== " + title); }
string Hash(string path) => Payload.Sha256(File.ReadAllBytes(path));
var quiet = new Progress<StepReport>(_ => { });

// ---------------------------------------------------------------- pick source meters

string? argCp = null, argTb = null;
for (int i = 0; i + 1 < args.Length; i += 2)
{
    if (args[i] == "--cp") argCp = args[i + 1];
    if (args[i] == "--tb") argTb = args[i + 1];
}

Section("scanning this PC");
var found = MeterScanner.ScanPc();
Console.WriteLine($"   {found.Count} meter folder(s) found");
foreach (var m in found.Take(5)) Console.WriteLine($"   - {m.KindName,-22} {m.State,-12} v{m.InstalledVersion} {m.Folder}");
if (found.Count > 5) Console.WriteLine($"   ... and {found.Count - 5} more");

string? srcCp = argCp ?? found.FirstOrDefault(m => m.Variant == Variant.ClassicPlus)?.Folder;
string? srcTb = argTb ?? found.FirstOrDefault(m => m.Variant == Variant.Toolbox)?.Folder;
Check("found at least one meter to test against", srcCp != null || srcTb != null);
if (srcCp == null && srcTb == null) return 1;

// ---------------------------------------------------------------- copy into %TEMP%

string root = Path.Combine(Path.GetTempPath(), "enragedon-setup-tests");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);
Console.WriteLine($"   working copies in {root}");

// Only the files the installer cares about, plus the sniffer (variant detection needs it).
string Copy(string src, string name)
{
    string dst = Path.Combine(root, name);
    Directory.CreateDirectory(Path.Combine(dst, "resources", "config"));
    foreach (var f in new[] { "DamageMeter.dll", "DamageMeter.Sniffing.dll", "ShinraMeter.dll", "ShinraMeter.exe",
                              "module.json", "manifest.json", "DamageMeter.dll.prepatch.bak" })
    {
        string from = Path.Combine(src, f);
        if (File.Exists(from)) try { File.Copy(from, Path.Combine(dst, f), true); } catch { }
    }
    // window.xml can be locked while the meter runs; a synthetic one tests the same code path
    string cfg = Path.Combine(dst, "resources", "config", "window.xml");
    try { File.Copy(Path.Combine(src, "resources", "config", "window.xml"), cfg, true); } catch { }
    if (!File.Exists(cfg))
        File.WriteAllText(cfg, "<Window><packets_collect>true</packets_collect></Window>");
    else
        File.WriteAllText(cfg, File.ReadAllText(cfg).Replace("<packets_collect>false</packets_collect>", "<packets_collect>true</packets_collect>"));
    return dst;
}

// A believable "untouched meter file": the real backup when there is one, otherwise random
// bytes. The installer never parses the DLL, it only hashes it and looks for a marker string.
byte[] StockBytes(string folder)
{
    string bak = Path.Combine(folder, "DamageMeter.dll.prepatch.bak");
    if (File.Exists(bak))
    {
        var b = File.ReadAllBytes(bak);
        if (!MeterScanner.IsPatched(b)) return b;
    }
    var rnd = new byte[64 * 1024];
    new Random(1234).NextBytes(rnd);
    return rnd;
}

// ---------------------------------------------------------------- per variant

void RunVariantTests(string src, Variant variant, string label)
{
    Section($"{label}: install, reinstall, uninstall");
    string dir = Copy(src, variant == Variant.ClassicPlus ? "classicplus" : "toolbox");
    string dll = Path.Combine(dir, "DamageMeter.dll");
    string bak = dll + ".prepatch.bak";
    string cfg = Path.Combine(dir, "resources", "config", "window.xml");

    var stock = StockBytes(dir);
    File.WriteAllBytes(dll, stock);
    if (File.Exists(bak)) File.Delete(bak);

    var m = MeterScanner.Inspect(dir)!;
    Check($"{label}: variant detected", m.Variant == variant, m.Variant.ToString());
    Check($"{label}: clean meter reads as not installed", m.State == MeterState.NotInstalled, m.State.ToString());

    PatchOps.InstallAsync(m, quiet).GetAwaiter().GetResult();
    Check($"{label}: patched dll matches the embedded payload", Hash(dll) == Payload.Hash(variant));
    Check($"{label}: original file backed up", File.Exists(bak) && Hash(bak) == Payload.Sha256(stock));
    Check($"{label}: 'Export packets logs' turned off",
          File.ReadAllText(cfg).Contains("<packets_collect>false</packets_collect>") && !File.ReadAllText(cfg).Contains("<packets_collect>true"));
    Check($"{label}: window.xml backed up", File.Exists(cfg + ".prepatch.bak"));

    if (File.Exists(Path.Combine(dir, "module.json")))
        Check($"{label}: auto-update disabled in module.json",
              (bool)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "module.json")))!["disableAutoUpdate"]!);
    if (File.Exists(Path.Combine(dir, "manifest.json")))
        Check($"{label}: manifest.json carries the new hash",
              (string)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")))!["files"]!["DamageMeter.dll"]! == Payload.Hash(variant));

    MeterScanner.Refresh(m);
    Check($"{label}: reads as up to date v{Payload.Version}", m.State == MeterState.UpToDate && m.InstalledVersion == Payload.Version);

    // a patched build that is not a published release = "older version, update available"
    File.WriteAllBytes(dll, File.ReadAllBytes(dll).Concat(new byte[] { 0 }).ToArray());
    MeterScanner.Refresh(m);
    Check($"{label}: unknown patched build reads as outdated", m.State == MeterState.Outdated && m.InstalledVersion == null);

    PatchOps.InstallAsync(m, quiet).GetAwaiter().GetResult();
    Check($"{label}: reinstall never overwrites the original backup", Hash(bak) == Payload.Sha256(stock));

    PatchOps.UninstallAsync(m, quiet).GetAwaiter().GetResult();
    Check($"{label}: uninstall restores the original byte for byte", Hash(dll) == Payload.Sha256(stock));
    Check($"{label}: backups cleaned up", !File.Exists(bak) && !File.Exists(cfg + ".prepatch.bak"));
    Check($"{label}: 'Export packets logs' stays off after uninstall", File.ReadAllText(cfg).Contains("<packets_collect>false"));
    MeterScanner.Refresh(m);
    Check($"{label}: reads as not installed again", m.State == MeterState.NotInstalled);

    Section($"{label}: meter updated itself since the last install");
    PatchOps.InstallAsync(m, quiet).GetAwaiter().GetResult();
    var newerStock = stock.Concat(new byte[] { 7, 7, 7 }).ToArray();
    File.WriteAllBytes(dll, newerStock);                       // toolbox dropped a fresh original on top
    MeterScanner.Refresh(m);
    Check($"{label}: detected as not installed", m.State == MeterState.NotInstalled);
    PatchOps.InstallAsync(m, quiet).GetAwaiter().GetResult();
    Check($"{label}: backup now holds the NEWER original", Hash(bak) == Payload.Sha256(newerStock));

    Section($"{label}: meter still running (file locked)");
    using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check($"{label}: lock detected", PatchOps.IsLocked(dll));
        try
        {
            PatchOps.InstallAsync(m, quiet).GetAwaiter().GetResult();
            Check($"{label}: install refuses while locked", false, "no exception thrown");
        }
        catch (FriendlyException e)
        {
            Check($"{label}: install refuses with the 'close the meter' message", e.Message == L.ErrLocked, e.Message);
        }
        Check($"{label}: patched dll left intact", Hash(dll) == Payload.Hash(variant));
        Check($"{label}: no leftover .tmp file", !File.Exists(dll + ".enragedon.tmp"));
    }
    Check($"{label}: lock released after closing", !PatchOps.IsLocked(dll));

    Section($"{label}: uninstall with no backup");
    File.Delete(bak);
    try
    {
        PatchOps.UninstallAsync(m, quiet).GetAwaiter().GetResult();
        Check($"{label}: uninstall refuses without a backup", false, "no exception thrown");
    }
    catch (FriendlyException e)
    {
        Check($"{label}: uninstall refuses with the 'no backup' message", e.Message == L.ErrNoBackup, e.Message);
    }
    Check($"{label}: dll untouched by the failed uninstall", Hash(dll) == Payload.Hash(variant));
}

if (srcCp != null) RunVariantTests(srcCp, Variant.ClassicPlus, "Classic+");
if (srcTb != null) RunVariantTests(srcTb, Variant.Toolbox, "TeraToolbox");

Section("permissions");
Check("can write into a normal folder", PatchOps.CanWrite(Path.Combine(root, Directory.GetDirectories(root).Length > 0 ? Path.GetFileName(Directory.GetDirectories(root)[0]) : "")));
string protectedMeter = found.FirstOrDefault(m => m.Folder.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase))?.Folder ?? "";
if (protectedMeter.Length > 0 && !IsElevated())
    Check("Program Files reads as not writable (would trigger the UAC prompt)", !PatchOps.CanWrite(protectedMeter));

Section("release payloads");
Check("toolbox payload embedded", Payload.Bytes(Variant.Toolbox).Length > 100_000, Payload.Hash(Variant.Toolbox)[..16]);
Check("classic+ payload embedded", Payload.Bytes(Variant.ClassicPlus).Length > 100_000, Payload.Hash(Variant.ClassicPlus)[..16]);
Check("the two payloads are different builds", Payload.Hash(Variant.Toolbox) != Payload.Hash(Variant.ClassicPlus));
Check("payload is not mistaken for an older release", Payload.ReleasedVersion(Payload.Hash(Variant.Toolbox)) == null);

Console.WriteLine();
Console.ForegroundColor = fail == 0 ? ConsoleColor.Green : ConsoleColor.Red;
Console.WriteLine(fail == 0 ? $"ALL {checks} CHECKS PASSED" : $"{fail} of {checks} CHECKS FAILED");
Console.ResetColor();
return fail == 0 ? 0 : 1;

static bool IsElevated()
{
    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
