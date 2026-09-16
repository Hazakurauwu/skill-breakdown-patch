using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EnragedON.Setup.Core;

public enum StepStatus { Pending, Active, Done }

public sealed class StepReport
{
    public required int Index { get; init; }
    public required StepStatus Status { get; init; }
    public string? Note { get; init; }
}

/// <summary>A failure with a message that is safe to show as-is.</summary>
public sealed class FriendlyException(string message) : Exception(message);

/// <summary>Install / uninstall of the patch in one meter folder. Mirrors install.ps1 / uninstall.ps1.</summary>
public static class PatchOps
{
    static readonly string[] BackedUp = { "DamageMeter.dll", "module.json", "manifest.json" };
    static readonly string[] ConfigFiles = { "window.xml", "window_backup.xml" };
    static readonly Regex PacketsOn = new(@"<packets_collect>\s*true\s*</packets_collect>", RegexOptions.IgnoreCase);
    static readonly UTF8Encoding Utf8NoBom = new(false);
    static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string[] InstallSteps => new[] { L.StepBackup, L.StepPatch, L.StepAutoUpdate, L.StepVerify };
    public static string[] UninstallSteps => new[] { L.StepRestore, L.StepCleanup, L.StepVerify };

    public const int StepDelayMs = 320;

    public static async Task InstallAsync(MeterInfo m, IProgress<StepReport> progress)
    {
        await Run(progress, 0, L.StepBackupNote, () => { Backup(m); return null; });
        // "Export packets logs" is turned off as part of installing, without a step of its
        // own: the option's name means nothing to a player and only adds confusion.
        await Run(progress, 1, null, () => { WriteDll(m); TurnOffPacketExport(m.Folder); return null; });
        await Run(progress, 2, null, () => KeepPatchOnUpdate(m.Folder) ? null : L.NoteNotNeeded);
        await Run(progress, 3, null, () => { VerifyInstall(m); return null; });
    }

    public static async Task UninstallAsync(MeterInfo m, IProgress<StepReport> progress)
    {
        await Run(progress, 0, null, () => { Restore(m); return null; });
        await Run(progress, 1, null, () => { Cleanup(m); return null; });
        await Run(progress, 2, null, () => { VerifyUninstall(m); return null; });
    }

    /// <summary>Runs one step off the UI thread. The step returns an optional note that replaces the default one.</summary>
    static async Task Run(IProgress<StepReport> progress, int index, string? defaultNote, Func<string?> work)
    {
        progress.Report(new StepReport { Index = index, Status = StepStatus.Active, Note = defaultNote });
        var sw = Stopwatch.StartNew();
        string? note;
        try { note = await Task.Run(work); }
        catch (Exception e) { throw Friendly(e); }
        int left = StepDelayMs - (int)sw.ElapsedMilliseconds;
        if (left > 0) await Task.Delay(left);   // lets each step be readable instead of flashing by
        progress.Report(new StepReport { Index = index, Status = StepStatus.Done, Note = note ?? defaultNote });
    }

    // ------------------------------------------------------------------ install

    static void Backup(MeterInfo m)
    {
        bool currentIsStock = !MeterScanner.IsPatched(MeterScanner.ReadShared(m.DllPath));
        foreach (var f in BackedUp)
        {
            string src = Path.Combine(m.Folder, f), bak = src + ".prepatch.bak";
            if (!File.Exists(src)) continue;
            // A stock file always becomes the backup (the meter may have updated itself since the
            // last install). A file we already patched never overwrites an existing backup.
            if (currentIsStock || !File.Exists(bak)) File.Copy(src, bak, true);
        }
    }

    static void WriteDll(MeterInfo m)
    {
        WriteAtomic(m.DllPath, Payload.Bytes(m.Variant));
        string old = Path.Combine(m.Folder, "ShinraRotationPatch.dll");   // leftover from v1.0
        if (File.Exists(old)) File.Delete(old);
    }

    /// <summary>
    /// "Export packets logs" re-parses the session's packets through the live parser when a boss
    /// dies, which flips the protocol version under the running meter and makes it drop every new
    /// buff/debuff until restart. Returns false when the meter has no config file here.
    /// </summary>
    public static bool TurnOffPacketExport(string folder)
    {
        bool any = false;
        foreach (var name in ConfigFiles)
        {
            string cfg = Path.Combine(folder, "resources", "config", name);
            if (!File.Exists(cfg)) continue;
            any = true;
            string raw = File.ReadAllText(cfg);
            if (!PacketsOn.IsMatch(raw)) continue;
            string bak = cfg + ".prepatch.bak";
            if (!File.Exists(bak)) File.Copy(cfg, bak, false);
            File.WriteAllText(cfg, PacketsOn.Replace(raw, "<packets_collect>false</packets_collect>"), Utf8NoBom);
        }
        return any;
    }

    /// <summary>disableAutoUpdate in module.json + new hash in manifest.json. False when neither file exists.</summary>
    static bool KeepPatchOnUpdate(string folder)
    {
        bool any = false;
        string mod = Path.Combine(folder, "module.json");
        if (File.Exists(mod))
        {
            any = true;
            var node = JsonNode.Parse(File.ReadAllText(mod))!.AsObject();
            node["disableAutoUpdate"] = true;
            File.WriteAllText(mod, node.ToJsonString(JsonOut), Utf8NoBom);
        }
        string man = Path.Combine(folder, "manifest.json");
        if (File.Exists(man))
        {
            any = true;
            var node = JsonNode.Parse(File.ReadAllText(man))!.AsObject();
            if (node["files"] is JsonObject files)
            {
                string hash = Payload.Sha256(MeterScanner.ReadShared(Path.Combine(folder, "DamageMeter.dll")));
                if (files.ContainsKey("DamageMeter.dll")) files["DamageMeter.dll"] = hash;
                files.Remove("ShinraRotationPatch.dll");
                File.WriteAllText(man, node.ToJsonString(JsonOut), Utf8NoBom);
            }
        }
        return any;
    }

    static void VerifyInstall(MeterInfo m)
    {
        string hash = Payload.Sha256(MeterScanner.ReadShared(m.DllPath));
        if (hash != Payload.Hash(m.Variant)) throw new FriendlyException(L.ErrVerify);

        string man = Path.Combine(m.Folder, "manifest.json");
        if (File.Exists(man) && JsonNode.Parse(File.ReadAllText(man))?["files"]?["DamageMeter.dll"] is JsonNode h &&
            !string.Equals(h.GetValue<string>(), hash, StringComparison.OrdinalIgnoreCase))
            throw new FriendlyException(L.ErrVerify);

        foreach (var name in ConfigFiles)
        {
            string cfg = Path.Combine(m.Folder, "resources", "config", name);
            if (File.Exists(cfg) && PacketsOn.IsMatch(File.ReadAllText(cfg))) throw new FriendlyException(L.ErrVerify);
        }
    }

    // ---------------------------------------------------------------- uninstall

    static void Restore(MeterInfo m)
    {
        if (!File.Exists(m.DllPath + ".prepatch.bak")) throw new FriendlyException(L.ErrNoBackup);
        foreach (var f in BackedUp)
        {
            string target = Path.Combine(m.Folder, f), bak = target + ".prepatch.bak";
            if (File.Exists(bak)) WriteAtomic(target, File.ReadAllBytes(bak));
        }
    }

    static void Cleanup(MeterInfo m)
    {
        foreach (var f in BackedUp)
        {
            string bak = Path.Combine(m.Folder, f) + ".prepatch.bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
        // "Export packets logs" stays off on purpose, so its config backup is no longer needed.
        foreach (var name in ConfigFiles)
        {
            string bak = Path.Combine(m.Folder, "resources", "config", name) + ".prepatch.bak";
            if (File.Exists(bak)) File.Delete(bak);
        }
        string old = Path.Combine(m.Folder, "ShinraRotationPatch.dll");
        if (File.Exists(old)) File.Delete(old);
    }

    static void VerifyUninstall(MeterInfo m)
    {
        if (MeterScanner.IsPatched(MeterScanner.ReadShared(m.DllPath))) throw new FriendlyException(L.ErrVerify);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Write next to the target, then swap it in, so a failure never leaves a half-written DLL.</summary>
    static void WriteAtomic(string target, byte[] data)
    {
        string tmp = target + ".enragedon.tmp";
        File.WriteAllBytes(tmp, data);
        try { File.Move(tmp, target, true); }
        catch (Exception e)
        {
            try { File.Delete(tmp); } catch { }
            // Replacing a file another process has open throws "access denied" here, not the
            // sharing-violation IOException -- without this check the person would be told to
            // run as administrator when the real problem is that the meter is still running.
            if (e is UnauthorizedAccessException && IsLocked(target)) throw new FriendlyException(L.ErrLocked);
            throw;
        }
    }

    /// <summary>True when the current user can write into the folder (false = needs admin).</summary>
    public static bool CanWrite(string folder)
    {
        string probe = Path.Combine(folder, ".enragedon-write-test");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            // The DLL itself must be replaceable too (it can carry its own ACL).
            using (new FileStream(Path.Combine(folder, "DamageMeter.dll"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return true; }   // in use is not a permission problem
    }

    /// <summary>True while something holds DamageMeter.dll open (the meter is running).</summary>
    public static bool IsLocked(string dllPath)
    {
        try
        {
            using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (UnauthorizedAccessException) { return false; }   // permission, handled by elevation
        catch (IOException) { return true; }
    }

    static Exception Friendly(Exception e) => e switch
    {
        FriendlyException => e,
        UnauthorizedAccessException => new FriendlyException(L.ErrDenied),
        IOException io when (io.HResult & 0xFFFF) is 32 or 33 => new FriendlyException(L.ErrLocked),
        _ => new FriendlyException(e.Message),
    };
}
