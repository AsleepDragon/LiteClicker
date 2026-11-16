using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using LiteClicker.Core;
using LiteClicker.Windows;

namespace LiteClicker.App;

public partial class App : Application
{
    private SingleInstance? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!Environment.Is64BitProcess || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            MessageBox.Show("轻点连点器需要 Windows 11 x64。", "无法启动", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1); return;
        }
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null) { MessageBox.Show("无法确认当前用户，单实例保护未建立。", "无法启动"); Shutdown(1); return; }
        instance = new(sid, Process.GetCurrentProcess().SessionId);
        if (!instance.IsPrimary)
        {
            if (!instance.TryActivateExisting()) MessageBox.Show(instance.Failure ?? "已有实例正在运行，但无法唤醒。它可能正在启动或使用不同权限，请切换到原实例，或关闭它后重试。本次不会启动第二个输入任务。", "轻点连点器", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        DispatcherUnhandledException += async (_, args) =>
        {
            args.Handled = true;
            if (MainWindow is MainWindow window) await window.HandleFatalErrorAsync();
            else Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => BestEffortStop();
        var clock = new WindowsClock();
        var controller = new RunController(new WindowsInputInjector(), new WindowsInputEnvironment(), clock);
        string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteClicker");
        var store = new SettingsStore(directory);
        var windowMain = new MainWindow(instance.ActivationMessage, controller, store, store.Load(), clock, e.Args.Contains("--diagnostics", StringComparer.OrdinalIgnoreCase));
        MainWindow = windowMain;
        windowMain.Show();
    }
    private void BestEffortStop()
    {
        try { if (MainWindow is MainWindow window) window.Controller.StopAsync().GetAwaiter().GetResult(); }
        catch { /* Process termination is best effort; never claim confirmed cleanup. */ }
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { BestEffortStop(); base.OnSessionEnding(e); }
    protected override void OnExit(ExitEventArgs e) { BestEffortStop(); instance?.Dispose(); base.OnExit(e); }
}
