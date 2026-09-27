using System.Reflection;
using System.Security.Cryptography;

namespace EnragedON.Setup.Core;

public enum Variant { Toolbox, ClassicPlus }

/// <summary>The patch DLLs embedded in the exe, plus the hashes of every build ever released.</summary>
public static class Payload
{
    public static readonly string Version = "1.7";

    static readonly Dictionary<Variant, byte[]> _bytes = new();
    static readonly Dictionary<Variant, string> _hash = new();

    // SHA-256 of DamageMeter.dll from every published release (git tags v1.0..v1.6),
    // so the list can say exactly which version a meter currently has.
    static readonly Dictionary<string, string> _released = new(StringComparer.OrdinalIgnoreCase)
    {
        ["998d0e5f94b02a5009c3820e79e9b880302ab3b2cc663b85a9891c3e8c504afe"] = "1.0",
        ["b8ca8682d5081f17a483296b6d3e469c45d7be6f4fc1f13d82a9172c60d9b77b"] = "1.4",
        ["30bdd66e0af65f74675bc440b964f317ac4ee7dd8636cbb8be3bc7597d13399c"] = "1.4",
        ["2d69791c88e28101552af25be1f06adedb03d16b7943cd1dc43927a4fe8e2cdd"] = "1.5",
        ["46bef5dd372dda9878208ae1ff333ded2753836a981f017003044432eef02c31"] = "1.5",
        // v1.6 toolbox build: net8 framework refs inside a net7 meter, crashed at the end of the
        // first fight. The Classic+ build of v1.6 (e211af3b...) is still the current payload.
        ["790e6524e04829060c9881a0cc753ed44d720717b60b2ae0e615313e5b5ae10e"] = "1.6",
    };

    static readonly Dictionary<Variant, int> _refMajor = new();

    /// <summary>
    /// Highest .NET major among the payload's framework references (System.*, WPF, ...). The patch
    /// code is JIT-compiled at the end of the first fight, so a payload that references a newer
    /// .NET than the meter runs on opens fine and then crashes. Compared against
    /// MeterScanner.RuntimeMajor before installing.
    /// </summary>
    public static int FrameworkMajor(Variant v)
    {
        byte[] b = Bytes(v);
        lock (_refMajor)
        {
            if (_refMajor.TryGetValue(v, out int m)) return m;
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(b));
            var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            m = 0;
            foreach (var h in md.AssemblyReferences)
            {
                var r = md.GetAssemblyReference(h);
                if (IsFrameworkRef(md.GetString(r.Name))) m = Math.Max(m, r.Version.Major);
            }
            return _refMajor[v] = m;
        }
    }

    // Same list as the build's verify step (src/Patcher.cs IsFrameworkRef): assemblies that come
    // with the shared .NET runtime, not NuGet packages shipped next to the meter.
    static bool IsFrameworkRef(string name) =>
        name == "System" || name == "netstandard" || name == "mscorlib" || name == "WindowsBase" ||
        name == "PresentationCore" || name == "PresentationFramework" ||
        (name.StartsWith("System.") && !name.StartsWith("System.Data.SQLite")) ||
        name.StartsWith("Microsoft.Win32.") || name.StartsWith("Microsoft.VisualBasic") || name == "Microsoft.CSharp";

    public static byte[] Bytes(Variant v)
    {
        lock (_bytes)
        {
            if (_bytes.TryGetValue(v, out var b)) return b;
            string name = v == Variant.ClassicPlus ? "payload.classicplus.dll" : "payload.toolbox.dll";
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                          ?? throw new InvalidOperationException("missing payload " + name);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            b = ms.ToArray();
            _bytes[v] = b;
            _hash[v] = Sha256(b);
            return b;
        }
    }

    public static string Hash(Variant v) { Bytes(v); lock (_bytes) return _hash[v]; }

    public static string? ReleasedVersion(string hash) => _released.TryGetValue(hash, out var ver) ? ver : null;

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
