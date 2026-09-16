using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EnragedON.Setup.Core;

namespace EnragedON.Setup;

public partial class MainWindow : Window
{
    const string SiteUrl = "https://enragedon.com";
    const string RepoUrl = "https://github.com/Hazakurauwu/skill-breakdown-patch#readme";

    enum ResultKind { Installed, Removed, Error }

    readonly string[] _args;
    MeterInfo? _pendingUninstall;
    MeterInfo? _lastTarget;
    bool _lastWasUninstall;
    bool _busy;
    CancellationTokenSource? _waitCts;
    DispatcherTimer? _pickErrorTimer;
    ResultKind _resultKind;

    public MainWindow(string[] args)
    {
        _args = args;
        InitializeComponent();
        HomeBrand.Content = BuildBrand(L.Product, string.Format(L.VersionFmt, Payload.Version));
        HomeFoot.Content = BuildFoot();
        Loaded += OnLoaded;
    }

    async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_args.Length >= 2 && (_args[0] == "--install" || _args[0] == "--uninstall"))
        {
            // Relaunched with admin rights for a specific folder: go straight to the action.
            var m = MeterScanner.Inspect(_args[1]);
            if (m != null) { await RunAction(m, uninstall: _args[0] == "--uninstall", skipElevation: true); return; }
        }
        if (_args.Length >= 2 && _args[0] == "--snapshot") return;
        await ScanAsync();
    }

    // ------------------------------------------------------------------ home

    async Task ScanAsync()
    {
        ShowScreen(ScreenHome);
        HomeLead.Text = L.ScanningLead;
        MeterList.ItemsSource = null;
        ScanPanel.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        AddLink.Visibility = Visibility.Collapsed;

        var sw = Stopwatch.StartNew();
        List<MeterInfo> found;
        try { found = await Task.Run(() => MeterScanner.ScanPc()); }
        catch { found = new(); }
        if (sw.ElapsedMilliseconds < 600) await Task.Delay(600 - (int)sw.ElapsedMilliseconds);

        ShowMeters(found);
    }

    List<MeterInfo> _meters = new();

    void ShowMeters(List<MeterInfo> meters)
    {
        _meters = meters;
        ScanPanel.Visibility = Visibility.Collapsed;
        AddLink.Visibility = Visibility.Visible;
        if (meters.Count == 0)
        {
            HomeLead.Text = L.NothingLead;
            MeterList.ItemsSource = null;
            EmptyPanel.Visibility = Visibility.Visible;
            AddLink.Visibility = Visibility.Collapsed;
            return;
        }
        HomeLead.Text = L.HomeLead;
        EmptyPanel.Visibility = Visibility.Collapsed;
        var vms = meters.Select(m => new MeterVM(m)).ToList();
        // Exactly one red button: the first meter that still needs something.
        var first = vms.FirstOrDefault(v => v.NeedsAction);
        if (first != null) first.IsPrimary = true;
        MeterList.ItemsSource = vms;
    }

    void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.PickFolderTitle, Multiselect = false };
        if (dlg.ShowDialog(this) != true) return;
        var hits = MeterScanner.ScanFolder(dlg.FolderName);
        if (hits.Count == 0)
        {
            ShowPickError();
            return;
        }
        var merged = _meters.ToList();
        foreach (var h in hits)
            if (!merged.Any(x => x.Folder.Equals(h.Folder, StringComparison.OrdinalIgnoreCase))) merged.Add(h);
        ShowMeters(merged);
    }

    void ShowPickError()
    {
        PickError.Visibility = Visibility.Visible;
        if (EmptyPanel.Visibility == Visibility.Visible) AddLink.Visibility = Visibility.Visible;
        _pickErrorTimer?.Stop();
        _pickErrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pickErrorTimer.Tick += (_, _) => { PickError.Visibility = Visibility.Collapsed; _pickErrorTimer.Stop(); };
        _pickErrorTimer.Start();
    }

    async void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is MeterVM vm) await RunAction(vm.Info, uninstall: false);
    }

    void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not MeterVM vm) return;
        _pendingUninstall = vm.Info;
        DlgTitle.Text = string.Format(L.UninstallDlgTitleFmt, vm.Info.Variant == Variant.ClassicPlus ? "Classic+" : "TeraToolbox");
        HomeBody.Effect = new BlurEffect { Radius = 3 };
        UninstallDialog.Visibility = Visibility.Visible;
        Fade(UninstallDialog);
    }

    void CloseDialog()
    {
        UninstallDialog.Visibility = Visibility.Collapsed;
        HomeBody.Effect = null;
    }

    void OnDialogKeep(object sender, RoutedEventArgs e) { CloseDialog(); _pendingUninstall = null; }

    async void OnDialogUninstall(object sender, RoutedEventArgs e)
    {
        var m = _pendingUninstall;
        CloseDialog();
        _pendingUninstall = null;
        if (m != null) await RunAction(m, uninstall: true);
    }

    // ------------------------------------------------------------------ flow

    async Task RunAction(MeterInfo m, bool uninstall, bool skipElevation = false)
    {
        _lastTarget = m;
        _lastWasUninstall = uninstall;

        if (!skipElevation && !PatchOps.CanWrite(m.Folder) && !IsAdmin())
        {
            if (Relaunch(uninstall ? "--uninstall" : "--install", m.Folder)) { Close(); return; }
            ShowResult(ResultKind.Error, L.ErrAdminCancelled);
            return;
        }

        if (!await WaitUntilClosed(m)) { await ScanAsync(); return; }

        bool wasOutdated = m.State == MeterState.Outdated;
        var steps = (uninstall ? PatchOps.UninstallSteps : PatchOps.InstallSteps)
                    .Select(t => new StepVM { Text = t, Status = StepStatus.Pending }).ToList();
        StepList.ItemsSource = steps;
        ProgressTitle.Text = uninstall ? L.Uninstalling : L.Installing;
        ProgressBrand.Content = BuildBrand(m.KindName,
            uninstall ? L.SubUninstall : string.Format(wasOutdated ? L.SubUpdateFmt : L.SubInstallFmt, Payload.Version));
        BarFill.Width = 0;
        ShowScreen(ScreenProgress);
        SetBusy(true);

        var progress = new Progress<StepReport>(r =>
        {
            steps[r.Index].Status = r.Status;
            steps[r.Index].Note = r.Note;
            double units = steps.Sum(s => s.Status == StepStatus.Done ? 1.0 : s.Status == StepStatus.Active ? 0.5 : 0);
            AnimateBar(units / steps.Count);
        });

        try
        {
            if (uninstall) await PatchOps.UninstallAsync(m, progress);
            else await PatchOps.InstallAsync(m, progress);
            await Task.Delay(250);
            ShowResult(uninstall ? ResultKind.Removed : ResultKind.Installed, null);
        }
        catch (Exception ex)
        {
            ShowResult(ResultKind.Error, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Shows the "close the game" screen until the meter lets go of its files. False = cancelled.</summary>
    async Task<bool> WaitUntilClosed(MeterInfo m)
    {
        var blockers = Blockers(m);
        if (blockers.Count == 0) return true;

        ProcList.ItemsSource = new ObservableCollection<string>(blockers);
        ShowScreen(ScreenClose);
        _waitCts = new CancellationTokenSource();
        try
        {
            while (true)
            {
                await Task.Delay(800, _waitCts.Token);
                blockers = await Task.Run(() => Blockers(m));
                if (blockers.Count == 0) { await Task.Delay(700); return true; }   // give the process a moment to fully exit
                if (!blockers.SequenceEqual((IEnumerable<string>)ProcList.ItemsSource))
                    ProcList.ItemsSource = new ObservableCollection<string>(blockers);
            }
        }
        catch (TaskCanceledException) { return false; }
        finally { _waitCts = null; }
    }

    void OnCancelWait(object sender, RoutedEventArgs e) => _waitCts?.Cancel();

    static List<string> Blockers(MeterInfo m)
    {
        bool locked = PatchOps.IsLocked(m.DllPath);
        bool meterProc = ProcessRunningFrom("ShinraMeter", m.Folder);
        bool toolbox = m.Variant == Variant.Toolbox && (Running("TeraToolbox") || Running("tera-toolbox"));
        var list = new List<string>();
        if (!locked && !meterProc && !toolbox) return list;
        if (Running("TERA")) list.Add("TERA");
        if (toolbox) list.Add("TeraToolbox");
        if (locked || meterProc) list.Add("ShinraMeter");
        return list;
    }

    static bool Running(string name)
    {
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    /// <summary>A ShinraMeter process counts only if it runs from this folder (or we can't tell).</summary>
    static bool ProcessRunningFrom(string name, string folder)
    {
        bool hit = false;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path == null || Path.GetDirectoryName(path)!.Equals(folder, StringComparison.OrdinalIgnoreCase)) hit = true;
            }
            catch { hit = true; }
            finally { p.Dispose(); }
        }
        return hit;
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static bool Relaunch(string verb, string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{verb} \"{folder}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (Win32Exception) { return false; }   // UAC prompt declined
    }

    // ---------------------------------------------------------------- results

    void ShowResult(ResultKind kind, string? error)
    {
        _resultKind = kind;
        bool ok = kind != ResultKind.Error;
        OkGlow.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        OkIcon.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        ErrIcon.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        ResultRing.Stroke = new SolidColorBrush(ok ? C("#5957F287") : C("#59E8404A"));
        ResultRing.Fill = new RadialGradientBrush(ok ? C("#2E57F287") : C("#2EE8404A"), ok ? C("#0A57F287") : C("#0AE8404A"));
        NewsPanel.Visibility = kind == ResultKind.Installed ? Visibility.Visible : Visibility.Collapsed;

        switch (kind)
        {
            case ResultKind.Installed:
                ResultTitle.Text = L.DoneTitle;
                ResultLead.Text = L.DoneLead;
                ResultSecBtn.Content = L.Close;
                ResultPriBtn.Content = L.OpenSite;
                break;
            case ResultKind.Removed:
                ResultTitle.Text = L.RemovedTitle;
                ResultLead.Text = L.RemovedLead;
                ResultSecBtn.Content = L.BackToList;
                ResultPriBtn.Content = L.Close;
                break;
            default:
                ResultTitle.Text = L.ErrorTitle;
                ResultLead.Text = (error ?? "") + "\n" + L.ErrorNothingChanged;
                ResultSecBtn.Content = L.BackToList;
                ResultPriBtn.Content = L.TryAgain;
                break;
        }
        ShowScreen(ScreenResult);
    }

    async void OnResultSecondary(object sender, RoutedEventArgs e)
    {
        if (_resultKind == ResultKind.Installed) Close();
        else await ScanAsync();
    }

    async void OnResultPrimary(object sender, RoutedEventArgs e)
    {
        switch (_resultKind)
        {
            case ResultKind.Installed: OpenUrl(SiteUrl); break;
            case ResultKind.Removed: Close(); break;
            default:
                if (_lastTarget != null)
                {
                    try { MeterScanner.Refresh(_lastTarget); } catch { }
                    await RunAction(_lastTarget, _lastWasUninstall);
                }
                else await ScanAsync();
                break;
        }
    }

    // ------------------------------------------------------------------ chrome

    void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnCloseWindow(object sender, RoutedEventArgs e) { if (!_busy) Close(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;   // never leave a meter half-installed
        base.OnClosing(e);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        CloseBtn.IsEnabled = !busy;
    }

    void ShowScreen(Grid screen)
    {
        foreach (var s in new[] { ScreenHome, ScreenClose, ScreenProgress, ScreenResult })
            s.Visibility = s == screen ? Visibility.Visible : Visibility.Collapsed;
        Fade(screen);
    }

    static void Fade(UIElement el)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    void AnimateBar(double fraction)
    {
        double target = Math.Max(0, Math.Min(1, fraction)) * BarTrack.ActualWidth;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuadraticEase() };
        BarFill.BeginAnimation(WidthProperty, anim);
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    UIElement BuildBrand(string title, string sub)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var logo = new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")),
            Width = 44, Height = 44,
            Effect = new DropShadowEffect { Color = C("#C8171D"), BlurRadius = 16, ShadowDepth = 0, Opacity = .6 },
        };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        grid.Children.Add(logo);

        var on = new TextBlock
        {
            Text = "ON",
            FontFamily = (FontFamily)FindResource("Cinzel"),
            FontSize = 22,
            FontWeight = FontWeights.Black,
            Foreground = new SolidColorBrush(C("#DDE3EE")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(on, 1);
        grid.Children.Add(on);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = title, FontSize = 13, LineHeight = 19.5, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Text"), HorizontalAlignment = HorizontalAlignment.Right });
        right.Children.Add(new TextBlock { Text = sub, FontSize = 11, LineHeight = 16.5, Foreground = (Brush)FindResource("Text3"), HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);
        return grid;
    }

    UIElement BuildFoot()
    {
        var border = new Border
        {
            Height = 54,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(C("#0DFFFFFF")),
            Padding = new Thickness(30, 0, 30, 0),
        };
        var grid = new Grid();
        var site = new Button { Style = (Style)FindResource("BtnLink"), Content = L.Site, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        site.Click += (_, _) => OpenUrl(SiteUrl);
        var what = new Button { Style = (Style)FindResource("BtnLink"), Content = L.WhatDoesItDo, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        what.Click += (_, _) => OpenUrl(RepoUrl);
        grid.Children.Add(site);
        grid.Children.Add(what);
        border.Child = grid;
        return border;
    }

#if DEBUG
    /// <summary>Renders every screen to PNG off-screen, for visual checks without showing a window.</summary>
    public void RenderSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        Left = -20000; Top = -20000; ShowActivated = false; ShowInTaskbar = false;
        Show();

        MeterInfo Fake(string folder, Variant v, MeterState st, string? ver, bool bak) =>
            new() { Folder = folder, Variant = v, State = st, InstalledVersion = ver, HasBackup = bak };
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var cp = Fake(Path.Combine(appData, @"Crazy-eSports-ClassicPlus\mods\external\classicplus.shinra"), Variant.ClassicPlus, MeterState.Outdated, "1.5", true);
        var tb = Fake(@"C:\Program Files (x86)\TeraToolbox Classic Plus\mods\ShinraMeter", Variant.Toolbox, MeterState.NotInstalled, null, false);

        void Snap(string name)
        {
            UpdateLayout();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var root = (FrameworkElement)Content;
            var rtb = new RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
        }

        ShowScreen(ScreenHome); ShowMeters(new() { cp, tb }); ScreenHome.BeginAnimation(OpacityProperty, null); ScreenHome.Opacity = 1;
        Snap("1-home");

        // Per variant shots. The site shows these per server: a Classic+ player must not see
        // a TeraToolbox row in the screenshot and the other way around.
        ShowMeters(new() { cp }); ScreenHome.Opacity = 1;
        Snap("1-home-classicplus");
        ShowMeters(new() { tb }); ScreenHome.Opacity = 1;
        Snap("1-home-toolbox");
        ShowMeters(new() { cp, tb });

        ProcList.ItemsSource = new[] { "TERA", "ShinraMeter" };
        ShowScreen(ScreenClose); ScreenClose.BeginAnimation(OpacityProperty, null); ScreenClose.Opacity = 1;
        Snap("2-close");

        var steps = PatchOps.InstallSteps.Select(t => new StepVM { Text = t }).ToList();
        steps[0].Status = StepStatus.Done; steps[0].Note = L.StepBackupNote;
        steps[1].Status = StepStatus.Active;
        StepList.ItemsSource = steps;
        ProgressTitle.Text = L.Installing;
        ProgressBrand.Content = BuildBrand(cp.KindName, string.Format(L.SubUpdateFmt, Payload.Version));
        ShowScreen(ScreenProgress); ScreenProgress.BeginAnimation(OpacityProperty, null); ScreenProgress.Opacity = 1;
        UpdateLayout();
        BarFill.Width = BarTrack.ActualWidth * .62;
        Snap("3-progress");
        Snap("3-progress-classicplus");

        ProgressBrand.Content = BuildBrand(tb.KindName, string.Format(L.SubInstallFmt, Payload.Version));
        UpdateLayout();
        BarFill.Width = BarTrack.ActualWidth * .62;
        Snap("3-progress-toolbox");

        ShowResult(ResultKind.Installed, null); ScreenResult.BeginAnimation(OpacityProperty, null); ScreenResult.Opacity = 1;
        Snap("4-done");

        cp.State = MeterState.UpToDate; cp.InstalledVersion = Payload.Version;
        ShowScreen(ScreenHome); ShowMeters(new() { cp }); ScreenHome.BeginAnimation(OpacityProperty, null); ScreenHome.Opacity = 1;
        _pendingUninstall = cp;
        DlgTitle.Text = string.Format(L.UninstallDlgTitleFmt, "Classic+");
        HomeBody.Effect = new BlurEffect { Radius = 3 };
        UninstallDialog.Visibility = Visibility.Visible;
        UninstallDialog.BeginAnimation(OpacityProperty, null); UninstallDialog.Opacity = 1;
        Snap("5-uninstall");
        CloseDialog();

        ShowResult(ResultKind.Error, L.ErrLocked); ScreenResult.BeginAnimation(OpacityProperty, null); ScreenResult.Opacity = 1;
        Snap("6-error");

        ShowScreen(ScreenHome); ShowMeters(new()); ScreenHome.BeginAnimation(OpacityProperty, null); ScreenHome.Opacity = 1;
        Snap("7-empty");
        Close();
    }
#endif
}
