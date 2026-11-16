using System.ComponentModel;
using System.Runtime.InteropServices;
using LiteClicker.Core;

namespace LiteClicker.Windows;

public sealed class WindowsInputInjector : IInputInjector
{
    public const uint EventMarker = 0x4C434B52;
    private static NativeInput Mouse(uint flags) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, ExtraInfo = EventMarker } } };
    private readonly NativeInput[] click = [Mouse(0x0002), Mouse(0x0004)];
    private readonly NativeInput[] down = [Mouse(0x0002)];
    private readonly NativeInput[] up = [Mouse(0x0004)];
    public InputResult Send(InputAction action)
    {
        NativeInput[] events = action switch { InputAction.Click => click, InputAction.Down => down, _ => up };
        Marshal.SetLastPInvokeError(0);
        uint sent = NativeMethods.SendInput((uint)events.Length, events, Marshal.SizeOf<NativeInput>());
        return new(sent, Marshal.GetLastPInvokeError());
    }
}

public sealed class WindowsInputEnvironment : IInputEnvironment
{
    public bool IsLeftButtonDown() => IsDown(0x01);
    private static bool IsDown(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    public bool AreKeysReleased(HotkeyBinding binding) => !IsDown(binding.Key)
        && (!binding.Modifiers.HasFlag(KeyModifiers.Ctrl) || !IsDown(0x11))
        && (!binding.Modifiers.HasFlag(KeyModifiers.Alt) || !IsDown(0x12))
        && (!binding.Modifiers.HasFlag(KeyModifiers.Shift) || !IsDown(0x10));
    public bool IsPointerOverOwnWindow()
    {
        if (!NativeMethods.GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        nint window = NativeMethods.WindowFromPoint(point);
        if (window == 0) throw new InvalidOperationException("无法确认指针下窗口。");
        if (NativeMethods.GetWindowThreadProcessId(window, out uint processId) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (processId == Environment.ProcessId) return true;
        nint root = NativeMethods.GetAncestor(window, 2);
        return root != 0 && NativeMethods.GetWindowThreadProcessId(root, out processId) != 0 && processId == Environment.ProcessId;
    }
}

public sealed class WindowsHotkeyRegistrar(nint window) : IHotkeyRegistrar
{
    public bool Register(int id, HotkeyBinding binding, out int error)
    {
        bool success = NativeMethods.RegisterHotKey(window, id, (uint)binding.Modifiers | 0x4000 /* MOD_NOREPEAT */, (uint)binding.Key);
        error = success ? 0 : Marshal.GetLastPInvokeError();
        return success;
    }
    public void Unregister(int id) => NativeMethods.UnregisterHotKey(window, id);
}

public sealed class WindowsClock : IMonotonicClock
{
    private readonly SystemMonotonicClock clock = new();
    public double Seconds => clock.Seconds;
    public Task Delay(TimeSpan duration, CancellationToken token) => clock.Delay(duration, token);
    public IDisposable? BeginActiveTiming(ClickMode mode) => mode == ClickMode.Click ? new TimerResolution() : null;
    private sealed class TimerResolution : IDisposable
    {
        private readonly bool enabled = NativeMethods.timeBeginPeriod(1) == 0;
        public void Dispose() { if (enabled) NativeMethods.timeEndPeriod(1); }
    }
}
