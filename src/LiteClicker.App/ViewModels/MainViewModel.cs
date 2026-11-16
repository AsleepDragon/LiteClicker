using System.Linq;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using LiteClicker.App.Services;
using LiteClicker.Core;

namespace LiteClicker.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public RunController Controller { get; }
    private readonly SettingsStore store;
    private readonly IMonotonicClock clock;
    private readonly StopDiagnostics? diagnostics;
    private AppSettings settings;
    private HotkeyManager? hotkeys;
    private EngineSnapshot snapshot;
    private string speedText, speedSelection, speedError = "", notice = "", hotkeyError = "";
    private bool holdMode, fatal;
    private int delay;
    public MainViewModel(RunController controller, SettingsStore store, SettingsLoadResult loaded, IMonotonicClock clock, bool diagnosticsEnabled)
    {
        Controller = controller; this.store = store; this.clock = clock; settings = loaded.Settings;
        diagnostics = diagnosticsEnabled ? new(store.DirectoryPath) : null;
        speedText = settings.Cps.ToString(CultureInfo.InvariantCulture); speedSelection = settings.SpeedSelection;
        holdMode = settings.Mode == ClickMode.Hold; delay = settings.StartDelaySeconds;
        notice = loaded.Warning ?? "";
        snapshot = Controller.Snapshot;
        HotkeyRows = new() { new("连点", settings.Hotkeys.Click), new("按住", settings.Hotkeys.Hold), new("紧急停止", settings.Hotkeys.Stop) };
        StartCommand = new(StartOrCancelAsync, () => CanStart, ReportException);
        StopCommand = new(StopAsync, () => true, ReportException);
        PresetCommand = new(p => SelectPreset(int.Parse((string)p!, CultureInfo.InvariantCulture)), _ => CanEdit && !holdMode);
        ApplyHotkeysCommand = new(ApplyHotkeysAsync, () => CanEdit && hotkeys != null, ReportException);
        ResetHotkeysCommand = new(async () => { LoadHotkeyRows(HotkeySet.Default); await ApplyHotkeysAsync(); }, () => CanEdit && hotkeys != null, ReportException);
        DismissNoticeCommand = new(_ => { notice = ""; Notify(); });
    }
    public ObservableCollection<HotkeyEditorRow> HotkeyRows { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StopCommand { get; }
    public ActionCommand PresetCommand { get; }
    public AsyncCommand ApplyHotkeysCommand { get; }
    public AsyncCommand ResetHotkeysCommand { get; }
    public ActionCommand DismissNoticeCommand { get; }
    public int[] Delays => SettingsRules.Delays;
    public bool CanEdit => snapshot.CanEdit;
    public bool CanEditSpeed => CanEdit && !holdMode;
    public bool IsClickMode { get => !holdMode; set { if (value) SetMode(false); } }
    public bool IsHoldMode { get => holdMode; set { if (value) SetMode(true); } }
    public bool ShowHoldHint => holdMode;
    public string SpeedText
    {
        get => speedText;
        set
        {
            if (!CanEdit || value == speedText) return;
            speedText = value; speedSelection = "custom";
            if (SettingsRules.TryParseCps(value, out _)) speedError = "";
            Notify();
        }
    }
    public string SpeedSelection => speedSelection;
    public string Interval => SettingsRules.TryParseCps(speedText, out int n) ? $"间隔 {SettingsRules.Interval(n)}" : "间隔 —";
    public string SpeedError => speedError;
    public bool HasSpeedError => speedError.Length > 0;
    public int Delay
    {
        get => delay;
        set
        {
            if (!CanEdit || value == delay || !SettingsRules.Delays.Contains(value)) return;
            delay = value; settings = settings with { StartDelaySeconds = value }; SaveInBackground(); Notify();
        }
    }
    public string Notice => notice;
    public bool HasNotice => notice.Length > 0;
    public string HotkeyError => hotkeyError;
    public bool HasHotkeyError => hotkeyError.Length > 0;
    public string HotkeySummary => $"{settings.Hotkeys.Click} 连点   ·   {settings.Hotkeys.Hold} 按住   ·   {settings.Hotkeys.Stop} 停止";
    public string EmergencyLabel => $"{settings.Hotkeys.Stop}  紧急停止";
    public string TrayText => $"LiteClicker · {StateTitle} · {settings.Hotkeys.Stop} 停止";
    public string StateTitle => snapshot.State switch
    {
        EngineState.Idle => "已停止", EngineState.Countdown => "准备开始", EngineState.Clicking => "连点中",
        EngineState.Holding => "左键按住中", EngineState.Stopping => "正在停止", _ => "需要处理"
    };
    public string StateColor => snapshot.State switch
    {
        EngineState.Clicking or EngineState.Holding => "#2563EB", EngineState.Error => "#B42318",
        EngineState.Countdown => "#9A6700", _ => "#5B6573"
    };
    public string StateDescription => snapshot.Message ?? (snapshot.State switch
    {
        EngineState.Countdown when snapshot.CountdownRemaining > 0 => $"将在 {Math.Ceiling(snapshot.CountdownRemaining):0} 秒后开始，请将鼠标移至目标位置。",
        EngineState.Countdown => "等待快捷键松开，请将鼠标移至目标位置。",
        EngineState.Clicking => $"目标 {snapshot.TargetCps} 次 / 秒 · 作用于当前鼠标位置",
        EngineState.Holding => "避免同时按物理左键；再次按快捷键即可释放。",
        EngineState.Stopping => "正在取消任务并确认左键释放…",
        _ => "设置完成后，用快捷键在目标窗口开始。"
    });
    public string ElapsedText => snapshot.Elapsed.TotalHours >= 1 ? $"{(long)snapshot.Elapsed.TotalHours:00}:{snapshot.Elapsed.Minutes:00}:{snapshot.Elapsed.Seconds:00}" : snapshot.Elapsed.ToString(@"mm\:ss");
    public string CountText => snapshot.Mode == ClickMode.Hold ? "持续按住" : $"已发送 {snapshot.ClickCount:N0} 次";
    public bool CanStart => !fatal && (snapshot.State == EngineState.Countdown || (CanEdit && hotkeys?.Ready == true && SettingsRules.TryParseCps(speedText, out _)));
    public string StartLabel => snapshot.State switch
    {
        EngineState.Countdown => "取消启动", EngineState.Clicking or EngineState.Holding => "正在运行", EngineState.Stopping => "正在停止…",
        _ => holdMode ? "开始按住" : "开始连点"
    };
    public WindowPlacement Placement => settings.WindowPlacement;

    public void AttachHotkeys(HotkeyManager manager)
    {
        hotkeys = manager;
        var result = manager.Apply(settings.Hotkeys);
        hotkeyError = result.Message ?? "";
        Controller.SetStartBlocker(result.Ready ? null : hotkeyError);
        Refresh();
    }
    public void SetUnavailable(string reason) { fatal = true; Controller.SetStartBlocker(reason); Refresh(); }
    private void SetMode(bool hold)
    {
        if (!Controller.Snapshot.CanEdit || holdMode == hold) return;
        holdMode = hold;
        settings = settings with { SelectedMode = hold ? "hold" : "click" };
        SaveInBackground(); Notify();
    }
    private void SelectPreset(int index)
    {
        if (!Controller.Snapshot.CanEdit || index is < 1 or > 5) return;
        speedSelection = $"preset{index}"; speedText = SettingsRules.Presets[index - 1].ToString(CultureInfo.InvariantCulture); speedError = "";
        settings = settings with { SpeedSelection = speedSelection }; SaveInBackground(); Notify();
    }
    public async Task CommitSpeedAsync()
    {
        if (!Controller.Snapshot.CanEdit) return;
        if (!SettingsRules.TryParseCps(speedText, out int cps)) { speedError = "请输入 1–100 之间的整数，不接受空值或小数。"; Notify(); return; }
        speedError = "";
        settings = settings with { SpeedSelection = speedSelection, CustomCps = speedSelection == "custom" ? cps : settings.CustomCps };
        await SaveAsync(); Notify();
    }
    private async Task StartOrCancelAsync()
    {
        if (Controller.Snapshot.State == EngineState.Countdown) { await StopAsync(); return; }
        await StartModeAsync(holdMode ? ClickMode.Hold : ClickMode.Click, null);
    }
    public async Task HandleHotkeyAsync(HotkeyAction action)
    {
        if (action == HotkeyAction.Stop) { await StopAsync(); return; }
        if (hotkeys?.Ready != true || fatal) return;
        var mode = action == HotkeyAction.Click ? ClickMode.Click : ClickMode.Hold;
        await StartModeAsync(mode, settings.Hotkeys[action]);
    }
    private async Task StartModeAsync(ClickMode mode, HotkeyBinding? trigger)
    {
        var current = Controller.Snapshot;
        if (current.Mode == mode && current.IsActive) { await StopAsync(); return; }
        if (hotkeys?.Ready != true || fatal || current.PendingRelease && current.State == EngineState.Error) return;
        if (!SettingsRules.TryParseCps(speedText, out int cps)) { speedError = "请输入 1–100 之间的整数。"; Notify(); return; }
        // Submit the intent before disk I/O; an old save continuation cannot start after F8.
        settings = settings with { SelectedMode = mode == ClickMode.Hold ? "hold" : "click", SpeedSelection = speedSelection, CustomCps = speedSelection == "custom" ? cps : settings.CustomCps };
        holdMode = mode == ClickMode.Hold;
        var task = Controller.ToggleAsync(mode, new(cps, delay), trigger);
        Refresh(); await task; Refresh();
        await SaveAsync();
    }
    public async Task StopAsync()
    {
        bool measure = Controller.Snapshot.IsActive || Controller.Snapshot.PendingRelease;
        var task = Controller.StopAsync(); Refresh(); await task;
        double completed = clock.Seconds;
        Refresh();
        if (diagnostics != null && measure) await diagnostics.RecordAsync(Controller.Snapshot, completed);
    }
    private async Task ApplyHotkeysAsync()
    {
        if (!Controller.Snapshot.CanEdit || hotkeys is null) return;
        var candidate = new HotkeySet(HotkeyRows[0].ToBinding(), HotkeyRows[1].ToBinding(), HotkeyRows[2].ToBinding());
        var result = hotkeys.Apply(candidate);
        hotkeyError = result.Message ?? "";
        Controller.SetStartBlocker(result.Ready ? null : hotkeyError);
        if (result.Success) { settings = settings with { Hotkeys = candidate }; await SaveAsync(); }
        Refresh();
    }
    private void LoadHotkeyRows(HotkeySet set) { HotkeyRows[0].Load(set.Click); HotkeyRows[1].Load(set.Hold); HotkeyRows[2].Load(set.Stop); }
    public void Refresh() { snapshot = Controller.Snapshot; Notify(); }
    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(null));
        StartCommand.Refresh(); StopCommand.Refresh(); PresetCommand.Refresh(); ApplyHotkeysCommand.Refresh(); ResetHotkeysCommand.Refresh();
    }
    public void ShowNotice(string text) { notice = text; Notify(); }
    private async void SaveInBackground() { try { await SaveAsync(); } catch (Exception ex) { ReportException(ex); } }
    private async Task SaveAsync() { if (await store.SaveAsync(settings) is { } error) { notice = error; Notify(); } }
    public async Task SavePlacementAsync(WindowPlacement placement) { settings = settings with { WindowPlacement = placement }; await SaveAsync(); }
    public void ReportException(Exception _) { notice = "操作未能完成，请停止后重试。"; Notify(); }
    public async ValueTask DisposeAsync() { hotkeys?.Dispose(); await Controller.DisposeAsync(); }
}
