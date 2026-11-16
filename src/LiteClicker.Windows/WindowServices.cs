using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiteClicker.Windows;

public static class WindowServices
{
    public const int WmHotkey = 0x0312, WmQueryEndSession = 0x0011, WmEndSession = 0x0016;
    public const int WmPowerBroadcast = 0x0218, WmSessionChange = 0x02B1, WmDpiChanged = 0x02E0, WmDisplayChange = 0x007E;
    public static void RegisterSessionNotifications(nint window)
    {
        if (!NativeMethods.WTSRegisterSessionNotification(window, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    public static void UnregisterSessionNotifications(nint window) => NativeMethods.WTSUnRegisterSessionNotification(window);
    public static void ClampWindow(nint window)
    {
        if (!NativeMethods.GetWindowRect(window, out var rect)) return;
        var (work, _) = WorkArea(window);
        int width = Math.Min(rect.Right - rect.Left, work.Right - work.Left);
        int height = Math.Min(rect.Bottom - rect.Top, work.Bottom - work.Top);
        int left = Math.Clamp(rect.Left, work.Left, work.Right - width);
        int top = Math.Clamp(rect.Top, work.Top, work.Bottom - height);
        if (left != rect.Left || top != rect.Top || width != rect.Right - rect.Left || height != rect.Bottom - rect.Top)
            NativeMethods.SetWindowPos(window, 0, left, top, width, height, 0x0004 | 0x0010);
    }
    public static (NativeRect Work, double Scale) WorkArea(nint window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(window, 2), ref info)) throw new InvalidOperationException("无法读取显示器工作区。");
        uint dpi = NativeMethods.GetDpiForWindow(window);
        return (info.Work, dpi == 0 ? 1 : dpi / 96d);
    }
}

/// <summary>The mutex name includes user + logon session. Access denied also means do not start a second engine.</summary>
public sealed class SingleInstance : IDisposable
{
    public const string WindowTitle = "轻点连点器 LiteClicker";
    public uint ActivationMessage { get; } = NativeMethods.RegisterWindowMessage("LiteClicker.Activate.v1");
    private readonly Mutex? mutex;
    public bool IsPrimary { get; }
    public string? Failure { get; }
    public SingleInstance(string userId, int sessionId)
    {
        try
        {
            mutex = new Mutex(false, $"Local\\LiteClicker.{userId}.{sessionId}", out bool created);
            IsPrimary = created;
        }
        catch (UnauthorizedAccessException) { Failure = "已有实例可能以不同权限运行。请切换到原实例并停止，或关闭原实例后重试。"; }
        catch (IOException) { Failure = "无法建立单实例保护，程序未启动。请关闭已有实例后重试。"; }
        if (ActivationMessage == 0) { IsPrimary = false; Failure = "无法注册实例唤醒消息，程序未启动。"; }
    }
    public bool TryActivateExisting()
    {
        nint window = NativeMethods.FindWindow(null, WindowTitle);
        return window != 0 && NativeMethods.SendMessageTimeout(window, ActivationMessage, 0, 0, 0x0002 | 0x0008, 1500, out nuint result) != 0 && result == 1;
    }
    public void Dispose() => mutex?.Dispose();
}
