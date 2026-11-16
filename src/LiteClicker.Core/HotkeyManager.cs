namespace LiteClicker.Core;

public interface IHotkeyRegistrar
{
    bool Register(int id, HotkeyBinding binding, out int error);
    void Unregister(int id);
}
public sealed record HotkeyApplyResult(bool Success, bool Ready, HotkeySet? Active, string? Message);

/// <summary>UI-thread service: register all three or restore all three. Never claims partial readiness.</summary>
public sealed class HotkeyManager(IHotkeyRegistrar registrar) : IDisposable
{
    public const int FirstId = 0x4C01;
    private readonly HashSet<int> registered = [];
    public HotkeySet? Active { get; private set; }
    public bool Ready { get; private set; }
    public static string ActionName(HotkeyAction action) => action switch { HotkeyAction.Click => "连点", HotkeyAction.Hold => "按住", _ => "紧急停止" };
    public static HotkeyAction? FromId(int id) => id >= FirstId && id < FirstId + 3 ? (HotkeyAction)(id - FirstId) : null;

    public HotkeyApplyResult Apply(HotkeySet candidate)
    {
        if (candidate.Validate() is { } error) return new(false, Ready, Active, error);
        HotkeySet? previous = Ready ? Active : null;
        Clear();
        if (TryRegister(candidate, out string? reason))
        {
            Active = candidate;
            Ready = true;
            return new(true, true, Active, null);
        }
        Clear();
        if (previous != null && TryRegister(previous, out _))
        {
            Active = previous;
            Ready = true;
            return new(false, true, Active, reason + " 已恢复原快捷键；请在快捷键设置中修改。");
        }
        Clear();
        Active = null;
        return new(false, false, null, reason + " 当前无法完整注册快捷键，启动已禁用。请在快捷键设置中修改后重新应用。");
    }

    private bool TryRegister(HotkeySet set, out string? reason)
    {
        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            int id = FirstId + (int)action;
            if (!registrar.Register(id, set[action], out int error))
            {
                reason = $"{ActionName(action)}快捷键 {set[action]} 注册失败（系统代码 {error}）。";
                return false;
            }
            registered.Add(id);
        }
        reason = null;
        return true;
    }
    private void Clear()
    {
        foreach (int id in registered) registrar.Unregister(id);
        registered.Clear();
        Ready = false;
    }
    public void Dispose() { Clear(); Active = null; }
}
