using System.Globalization;

namespace LiteClicker.Core;

public enum ClickMode { Click, Hold }
public enum EngineState { Idle, Countdown, Clicking, Holding, Stopping, Error }
public enum InputAction { Click, Down, Up }
[Flags] public enum KeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4 }
public enum HotkeyAction { Click, Hold, Stop }

public sealed record HotkeyBinding(int Key, KeyModifiers Modifiers = KeyModifiers.None)
{
    public bool IsValid => Key is >= 0x70 and <= 0x7A && (Modifiers & ~(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift)) == 0;
    public override string ToString() => $"{(Modifiers.HasFlag(KeyModifiers.Ctrl) ? "Ctrl+" : "")}{(Modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "")}{(Modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")}F{Key - 0x6F}";
}

public sealed record HotkeySet(HotkeyBinding Click, HotkeyBinding Hold, HotkeyBinding Stop)
{
    public static HotkeySet Default => new(new(0x75), new(0x76), new(0x77));
    public HotkeyBinding this[HotkeyAction action] => action switch { HotkeyAction.Click => Click, HotkeyAction.Hold => Hold, _ => Stop };
    public string? Validate() => !Click.IsValid || !Hold.IsValid || !Stop.IsValid ? "快捷键仅支持 F1–F11 和 Ctrl、Alt、Shift。" :
        Click == Hold || Click == Stop || Hold == Stop ? "三个动作不能使用相同的快捷键。" : null;
}

public sealed record RunOptions(int Cps = 10, int DelaySeconds = 2)
{
    public string? Validate() => Cps is < 1 or > 100 ? "请输入 1–100 之间的整数。" :
        !SettingsRules.Delays.Contains(DelaySeconds) ? "请选择有效的启动延迟。" : null;
}

public static class SettingsRules
{
    public static readonly int[] Presets = [1, 5, 10, 20, 50];
    public static readonly int[] Delays = [0, 1, 2, 3, 5];
    public static bool TryParseCps(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value is >= 1 and <= 100;
    public static string Interval(int cps) => (1000d / cps).ToString("0.##", CultureInfo.InvariantCulture) + " 毫秒";
}

public sealed record EngineSnapshot(EngineState State, ClickMode? Mode, int TargetCps, long ClickCount,
    TimeSpan Elapsed, double CountdownRemaining, bool PendingRelease, string? Message,
    double? StopRequestedAt, double? LastDownSubmittedAt, double? ReleaseCompletedAt)
{
    public bool CanEdit => !PendingRelease && State is EngineState.Idle or EngineState.Error;
    public bool IsActive => State is EngineState.Countdown or EngineState.Clicking or EngineState.Holding or EngineState.Stopping;
}

public readonly record struct InputResult(uint Sent, int NativeError = 0);
public interface IInputInjector { InputResult Send(InputAction action); }
public interface IInputEnvironment
{
    bool IsLeftButtonDown();
    bool IsPointerOverOwnWindow();
    bool AreKeysReleased(HotkeyBinding binding);
}
public interface IMonotonicClock
{
    IDisposable? BeginActiveTiming(ClickMode mode) => null;
    double Seconds { get; }
    Task Delay(TimeSpan duration, CancellationToken token);
}
public sealed class SystemMonotonicClock : IMonotonicClock
{
    public double Seconds => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
    public Task Delay(TimeSpan duration, CancellationToken token) => Task.Delay(duration, token);
}
