using System.Text;

namespace EnragedON.Setup.Core;

public enum MeterState { NotInstalled, Outdated, UpToDate }

public sealed class MeterInfo
{
    public required string Folder { get; init; }
    public required Variant Variant { get; init; }
    public MeterState State { get; set; }
    public string? InstalledVersion { get; set; }
    public bool HasBackup { get; set; }

    public string DllPath => Path.Combine(Folder, "DamageMeter.dll");
    public string KindName => Variant == Variant.ClassicPlus ? L.KindClassicPlus : L.KindToolbox;
}

/// <summary>Finds ShinraMeter installs and reads what state each one is in.</summary>
public static class MeterScanner
{
    static readonly string[] NameHints = { "tera", "toolbox", "shinra", "crazy-esports" };

    public static List<MeterInfo> ScanPc(CancellationToken ct = default)
    {
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in CandidateRoots())
        {
            ct.ThrowIfCancellationRequested();
            foreach (var dir in FindMeterFolders(root, ct))
                if (seen.Add(dir)) folders.Add(dir);
        }
        // Most useful first: the Classic+ launcher's own meter, then anything already patched or
        // with a backup (a meter this person really uses), then the rest. Someone can easily have
        // a dozen old copies lying around in Desktop/Downloads and those should not come first.
        return folders.Select(Inspect).Where(m => m != null).Cast<MeterInfo>()
                      .OrderBy(m => Rank(m)).ThenBy(m => m.Folder).ToList();
    }

    static int Rank(MeterInfo m)
    {
        if (m.Variant == Variant.ClassicPlus) return 0;
        if (m.State != MeterState.NotInstalled || m.HasBackup) return 1;
        return 2;
    }

    /// <summary>For the manual folder picker: the folder itself or anything inside it.</summary>
    public static List<MeterInfo> ScanFolder(string folder)
    {
        return FindMeterFolders(folder, default).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Inspect).Where(m => m != null).Cast<MeterInfo>().ToList();
    }

    static IEnumerable<string> CandidateRoots()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Classic+ launcher's own meter, checked first (fast path).
        yield return Path.Combine(appData, "Crazy-eSports-ClassicPlus");

        // Folders with a TERA-ish name in the usual places people unzip or install things.
        var parents = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(user, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            appData, local,
        };
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Fixed && d.IsReady) { parents.Add(d.RootDirectory.FullName); parents.Add(Path.Combine(d.RootDirectory.FullName, "Games")); } }
            catch { }
        }

        foreach (var parent in parents.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(parent).ToList(); }
            catch { continue; }
            foreach (var sub in subs)
            {
                string name = Path.GetFileName(sub);
                if (NameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    yield return sub;
            }
        }
    }

    static IEnumerable<string> FindMeterFolders(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 8,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        List<string> hits;
        try { hits = Directory.EnumerateFiles(root, "DamageMeter.dll", opts).ToList(); }
        catch { yield break; }

        foreach (var file in hits)
        {
            ct.ThrowIfCancellationRequested();
            string dir = Path.GetDirectoryName(file)!;
            if (dir.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                dir.Contains(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) continue;
            // A real meter has its sniffer next to it; build/work folders and loose copies don't.
            if (!File.Exists(Path.Combine(dir, "DamageMeter.Sniffing.dll"))) continue;
            if (!File.Exists(Path.Combine(dir, "ShinraMeter.dll")) && !File.Exists(Path.Combine(dir, "ShinraMeter.exe"))) continue;
            yield return dir;
        }
    }

    public static MeterInfo? Inspect(string folder)
    {
        try
        {
            var m = new MeterInfo { Folder = folder, Variant = DetectVariant(folder) };
            Refresh(m);
            return m;
        }
        catch { return null; }
    }

    public static void Refresh(MeterInfo m)
    {
        byte[] dll = ReadShared(m.DllPath);
        string hash = Payload.Sha256(dll);
        m.HasBackup = File.Exists(m.DllPath + ".prepatch.bak");
        m.InstalledVersion = null;
        if (hash == Payload.Hash(m.Variant))
        {
            m.State = MeterState.UpToDate;
            m.InstalledVersion = Payload.Version;
        }
        else if (Payload.ReleasedVersion(hash) is { } ver)
        {
            m.State = MeterState.Outdated;
            m.InstalledVersion = ver;
        }
        else
        {
            m.State = IsPatched(dll) ? MeterState.Outdated : MeterState.NotInstalled;
        }
    }

    /// <summary>
    /// The stock TeraToolbox meter's sniffer defines ToolboxSniffer; forks like the Classic+ one
    /// don't, and need the Classic+ build of the patch (the other one fails to load there).
    /// </summary>
    public static Variant DetectVariant(string folder)
    {
        string sniff = Path.Combine(folder, "DamageMeter.Sniffing.dll");
        try
        {
            byte[] b = ReadShared(sniff);
            return Contains(b, "ToolboxSniffer") ? Variant.Toolbox : Variant.ClassicPlus;
        }
        catch { return Variant.Toolbox; }
    }

    public static bool IsPatched(byte[] dll) => Contains(dll, "ShinraRotationPatch");

    public static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    static bool Contains(byte[] data, string text)
    {
        return data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 ||
               data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
    }
}
