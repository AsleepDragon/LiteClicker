using System;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LiteClicker.App.ViewModels;
using LiteClicker.Core;
using LiteClicker.Windows;
using Forms = System.Windows.Forms;

namespace LiteClicker.App;

public partial class MainWindow : Window
{
    public RunController Controller => viewModel.Controller;
    private readonly MainViewModel viewModel;
    private readonly uint activationMessage;
    private readonly DispatcherTimer statistics;
    private HwndSource? source;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private Icon? trayIcon;
    private bool closing, allowClose, sourceReady, sessionRegistered;
    private nint handle;

    public MainWindow(uint activationMessage, RunController controller, SettingsStore store, SettingsLoadResult loaded, IMonotonicClock clock, bool diagnostics)
    {
        this.activationMessage = activationMessage;
        InitializeComponent();
        viewModel = new(controller, store, loaded, clock, diagnostics);
        DataContext = viewModel;
        var placement = viewModel.Placement;
        Left = placement.Left; Top = placement.Top; Width = placement.Width; Height = placement.Height;
        statistics = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        SourceInitialized += InitializeNative;
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;
        Loaded += (_, _) => ConstrainToWorkArea();
    }

    private void InitializeNative(object? sender, EventArgs e)
    {
        handle = new WindowInteropHelper(this).Handle;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WindowProcedure);
        viewModel.AttachHotkeys(new HotkeyManager(new WindowsHotkeyRegistrar(handle)));
        try { WindowServices.RegisterSessionNotifications(handle); sessionRegistered = true; }
        catch { viewModel.SetUnavailable("无法接收系统会话通知，启动已禁用。请退出后重新打开。"); }
        InitializeTray();
        sourceReady = true;
        Refresh();
    }
    private void InitializeTray()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LiteClicker.TrayIcon") ?? throw new InvalidOperationException("托盘图标资源缺失。");
        using var embedded = new Icon(stream);
        trayIcon = (Icon)embedded.Clone();
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("显示主窗口", null, async (_, _) => await RestoreAsync());
        trayMenu.Items.Add("停止所有动作", null, async (_, _) => await viewModel.StopAsync());
        trayMenu.Items.Add("退出", null, (_, _) => Close());
        // Opening is synchronous: the menu is never shown before the release attempt finishes.
        trayMenu.Opening += (_, _) => { Controller.StopAsync().GetAwaiter().GetResult(); Refresh(); };
        tray = new Forms.NotifyIcon { Icon = trayIcon, ContextMenuStrip = trayMenu, Visible = true, Text = "LiteClicker · 已停止 · F8 停止" };
        tray.DoubleClick += async (_, _) => await RestoreAsync();
    }
    private void Refresh()
    {
        viewModel.Refresh();
        if (tray != null)
        {
            string tooltip = viewModel.TrayText;
            tray.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        }
    }
    private nint WindowProcedure(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == activationMessage)
        {
            // The acknowledgement is returned only after stopping; displaying is queued on this dispatcher.
            Controller.StopAsync().GetAwaiter().GetResult();
            Dispatcher.BeginInvoke(new Action(() => ShowStoppedWindow()));
            handled = true; return 1;
        }
        if (msg == WindowServices.WmHotkey && HotkeyManager.FromId(wParam.ToInt32()) is { } action)
        {
            HandleHotkey(action); handled = true;
        }
        if (msg == WindowServices.WmQueryEndSession || (msg == WindowServices.WmEndSession && wParam != 0) ||
            (msg == WindowServices.WmSessionChange && wParam.ToInt32() is 1 or 2 or 3 or 4 or 6 or 7) ||
            (msg == WindowServices.WmPowerBroadcast && wParam.ToInt32() is 0 or 4))
        {
            Controller.StopAsync().GetAwaiter().GetResult(); Refresh();
            if (msg == WindowServices.WmQueryEndSession) { handled = true; return 1; }
        }
        if (msg is WindowServices.WmDpiChanged or WindowServices.WmDisplayChange)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ConstrainToWorkArea));
        return 0;
    }
    private async void HandleHotkey(HotkeyAction action)
    {
        try { await viewModel.HandleHotkeyAsync(action); }
        catch (Exception ex) { await Controller.StopAsync(); viewModel.ReportException(ex); }
    }
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
        else if (sourceReady) { Controller.StopAsync().GetAwaiter().GetResult(); Refresh(); }
    }
    public async Task RestoreAsync()
    {
        await viewModel.StopAsync();
        ShowStoppedWindow();
    }
    private void ShowStoppedWindow()
    {
        if (closing) return;
        Show(); WindowState = WindowState.Normal; Activate();
        ConstrainToWorkArea(); Refresh();
    }
    private void ConstrainToWorkArea()
    {
        if (handle == 0 || WindowState != WindowState.Normal) return;
        var (work, scale) = WindowServices.WorkArea(handle);
        double width = (work.Right - work.Left) / scale, height = (work.Bottom - work.Top) / scale;
        MinWidth = Math.Min(440, width); MinHeight = Math.Min(540, height);
        WindowServices.ClampWindow(handle);
    }
    private async void SpeedLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        try { await viewModel.CommitSpeedAsync(); }
        catch (Exception ex) { viewModel.ReportException(ex); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        try
        {
            await viewModel.StopAsync();
            if (Controller.Snapshot.PendingRelease)
            {
                ShowStoppedWindowForError();
                if (MessageBox.Show(this, "无法确认左键已释放。选择“否”留在程序中再次停止；选择“是”仍然退出。\n\n仍然退出吗？", "左键释放尚未确认", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                { closing = false; return; }
            }
            Rect bounds = RestoreBounds;
            if (!bounds.IsEmpty) await viewModel.SavePlacementAsync(new(bounds.Left, bounds.Top, bounds.Width, bounds.Height));
            statistics.Stop();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            trayMenu?.Dispose(); trayIcon?.Dispose();
            if (sessionRegistered) WindowServices.UnregisterSessionNotifications(handle);
            source?.RemoveHook(WindowProcedure);
            await viewModel.DisposeAsync();
            allowClose = true;
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex) { closing = false; viewModel.ReportException(ex); }
    }
    private void ShowStoppedWindowForError() { Show(); WindowState = WindowState.Normal; Activate(); Refresh(); }
    public async Task HandleFatalErrorAsync()
    {
        await Controller.StopAsync();
        viewModel.ShowNotice("程序发生异常，已请求停止。请确认左键已释放后退出并重新打开。");
        if (!Controller.Snapshot.PendingRelease) viewModel.SetUnavailable("程序异常后已禁用启动，请退出并重新打开。");
        ShowStoppedWindowForError();
    }
}
