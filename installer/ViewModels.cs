using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using EnragedON.Setup.Core;

namespace EnragedON.Setup;

public sealed class MeterVM
{
    public MeterInfo Info { get; }
    public bool IsPrimary { get; set; }

    public MeterVM(MeterInfo info) { Info = info; }

    public string Title => Info.KindName;
    public string IconText => Info.Variant == Variant.ClassicPlus ? "C+" : "TB";
    public Brush IconBrush => Info.Variant == Variant.ClassicPlus ? Grad("#3A4C8C", "#1F2A55") : Grad("#2F6A4F", "#18382A");
    public string FullPath => Info.Folder;

    public string ShortPath
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var root in new[] { appData, local })
                if (Info.Folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    return "…" + Info.Folder.Substring(root.Length);
            return Info.Folder;
        }
    }

    public bool NeedsAction => Info.State != MeterState.UpToDate;

    public string PillText => Info.State switch
    {
        MeterState.UpToDate => string.Format(L.PillUpToDateFmt, Payload.Version),
        MeterState.Outdated when Info.InstalledVersion != null => string.Format(L.PillUpdateFmt, Info.InstalledVersion),
        MeterState.Outdated => L.PillUpdateUnknown,
        _ => L.PillNotInstalled,
    };
    public Brush PillBg => Info.State switch
    {
        MeterState.UpToDate => Solid("#1F57F287"),
        MeterState.Outdated => Solid("#24FAA61A"),
        _ => Solid("#248B909A"),
    };
    public Brush PillFg => Info.State switch
    {
        MeterState.UpToDate => Solid("#57F287"),
        MeterState.Outdated => Solid("#FAA61A"),
        _ => Solid("#C0C4CC"),
    };

    public string ActionText => Info.State switch
    {
        MeterState.UpToDate => L.BtnReinstall,
        MeterState.Outdated => L.BtnUpdate,
        _ => L.BtnInstall,
    };
    public Visibility PriVisibility => IsPrimary ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SecVisibility => IsPrimary ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UninstallVisibility =>
        Info.State != MeterState.NotInstalled && Info.HasBackup ? Visibility.Visible : Visibility.Collapsed;

    static Brush Solid(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    static Brush Grad(string a, string b)
    {
        var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
    }
}

public sealed class StepVM : INotifyPropertyChanged
{
    StepStatus _status;
    string? _note;

    public required string Text { get; init; }
    public StepStatus Status { get => _status; set { _status = value; On(); } }
    public string? Note { get => _note; set { _note = value; On(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
