namespace LiteClicker.Core;

/// <summary>Owns all input calls. The generation invalidates queued commands before they can send.
/// The short gate covers checks + one native call; the command semaphore never queues clicks.</summary>
public sealed class RunController(IInputInjector injector, IInputEnvironment environment, IMonotonicClock clock) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private long generation;
    private Session? session;
    private ClickMode? requestedMode;
    private ClickMode? resultMode;
    private EngineState state = EngineState.Idle;
    private bool pendingRelease, disposed;
    private string? blocker, message;
    private int targetCps = 10;
    private long count;
    private double? startedAt, stopRequestedAt, lastDownAt, releaseCompletedAt;
    private double elapsed;

    private sealed class Session(long id, ClickMode mode, RunOptions options, HotkeyBinding? trigger, double requestedAt)
    {
        public long Id { get; } = id;
        public ClickMode Mode { get; } = mode;
        public RunOptions Options { get; } = options;
        public HotkeyBinding? Trigger { get; } = trigger;
        public double RequestedAt { get; } = requestedAt;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Work { get; set; } = Task.CompletedTask;
        public bool Begun { get; set; }
    }

    public EngineSnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                double remaining = session is null ? 0 : Math.Max(0, session.Options.DelaySeconds - (clock.Seconds - session.RequestedAt));
                double duration = startedAt.HasValue ? Math.Max(0, clock.Seconds - startedAt.Value) : elapsed;
                return new(state, session?.Mode ?? resultMode, targetCps, count, TimeSpan.FromSeconds(duration), remaining,
                    pendingRelease, message, stopRequestedAt, lastDownAt, releaseCompletedAt);
            }
        }
    }

    public void SetStartBlocker(string? reason)
    {
        lock (gate)
        {
            if (session != null || pendingRelease) throw new InvalidOperationException("必须先完成停止和释放。");
            blocker = reason;
            message = reason;
            state = reason is null ? EngineState.Idle : EngineState.Error;
        }
    }

    public Task ToggleAsync(ClickMode mode, RunOptions options, HotkeyBinding? trigger = null) => Request(mode, options, trigger);
    public Task StopAsync() => Request(null, null, null);

    private Task Request(ClickMode? mode, RunOptions? options, HotkeyBinding? trigger)
    {
        // Increment before waiting for an in-flight native call: its next iteration is stale.
        long id = Interlocked.Increment(ref generation);
        double at = clock.Seconds;
        ClickMode? target;
        lock (gate)
        {
            if (disposed && mode != null) return Task.CompletedTask;
            if (id != Volatile.Read(ref generation)) return Task.CompletedTask;
            target = mode == requestedMode ? null : mode;
            if (target != null && (pendingRelease && session is null || blocker != null || options?.Validate() != null))
            {
                target = null;
                message = blocker ?? options?.Validate() ?? "无法确认左键已释放，请再次停止。";
            }
            requestedMode = target;
            session?.Cancellation.Cancel();
            if (startedAt.HasValue) { elapsed = Math.Max(0, at - startedAt.Value); startedAt = null; }
            stopRequestedAt = at;
            if (session != null || pendingRelease) state = EngineState.Stopping;
        }
        return ApplyAsync(id, target, options, trigger);
    }

    private async Task ApplyAsync(long id, ClickMode? target, RunOptions? options, HotkeyBinding? trigger)
    {
        await commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (id != Volatile.Read(ref generation)) return;
            Session? previous;
            lock (gate) previous = session;
            if (previous != null) await previous.Work.ConfigureAwait(false);
            if (id != Volatile.Read(ref generation)) return;
            // A failed worker already made its bounded attempt. A later explicit stop may retry.
            if (previous is null && target is null) await ReleaseAsync().ConfigureAwait(false);
            lock (gate)
            {
                if (id != Volatile.Read(ref generation)) return;
                if (pendingRelease) { state = EngineState.Error; requestedMode = null; message = "无法确认左键已释放，请再次点击“停止所有动作”。"; return; }
                if (target is null)
                {
                    state = blocker is null ? EngineState.Idle : EngineState.Error;
                    message = blocker ?? message;
                    return;
                }
                if (blocker != null) { state = EngineState.Error; message = blocker; requestedMode = null; return; }
                var next = new Session(id, target.Value, options!, trigger, clock.Seconds);
                session = next;
                state = EngineState.Countdown;
                message = null;
                next.Work = Task.Run(() => RunAsync(next));
            }
        }
        finally { commands.Release(); }
    }

    private bool Current(Session s) => s.Id == Volatile.Read(ref generation) && !s.Cancellation.IsCancellationRequested;
    private void CheckCurrent(Session s) { if (!Current(s)) throw new OperationCanceledException(s.Cancellation.Token); }

    private async Task RunAsync(Session s)
    {
        string? failure = null;
        bool isError = false;
        IDisposable? timing = null;
        try
        {
            await AwaitStartAsync(s).ConfigureAwait(false);
            timing = clock.BeginActiveTiming(s.Mode);
            double period = 1d / s.Options.Cps;
            double due = clock.Seconds;
            while (true)
            {
                lock (gate)
                {
                    CheckCurrent(s);
                    if (environment.IsPointerOverOwnWindow()) throw new StartupCancelledException("鼠标进入本应用窗口，已停止。请移至目标窗口后重新开始。");
                    if (!s.Begun && environment.IsLeftButtonDown()) throw new StartupCancelledException("请先松开鼠标左键，再重新开始。");
                    if (!s.Begun || s.Mode == ClickMode.Click) Submit(s);
                }
                if (s.Mode == ClickMode.Hold)
                    await clock.Delay(TimeSpan.FromMilliseconds(25), s.Cancellation.Token).ConfigureAwait(false);
                else
                {
                    due += period;
                    double now = clock.Seconds;
                    // Skip missed periods; never replay a burst of overdue clicks.
                    if (due <= now) due = now + period;
                    await clock.Delay(TimeSpan.FromSeconds(due - now), s.Cancellation.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!Current(s)) { }
        catch (StartupCancelledException ex) { failure = ex.Message; }
        catch (Exception ex)
        {
            failure = ex is InputSubmissionException ? ex.Message : "输入任务异常，已停止。请检查当前桌面状态后重试。";
            isError = true;
        }
        finally
        {
            lock (gate)
            {
                state = EngineState.Stopping;
                if (startedAt.HasValue) { elapsed = Math.Max(0, clock.Seconds - startedAt.Value); startedAt = null; }
                stopRequestedAt ??= clock.Seconds;
            }
            await ReleaseAsync().ConfigureAwait(false);
            timing?.Dispose();
            lock (gate)
            {
                session = null;
                if (s.Id == Volatile.Read(ref generation)) requestedMode = null;
                message = pendingRelease ? "无法确认左键已释放，请再次点击“停止所有动作”。" : failure ?? blocker;
                state = pendingRelease || isError || blocker != null ? EngineState.Error : EngineState.Idle;
                s.Cancellation.Dispose();
            }
        }
    }

    private async Task AwaitStartAsync(Session s)
    {
        while (true)
        {
            CheckCurrent(s);
            double waited = clock.Seconds - s.RequestedAt;
            bool released = s.Trigger is null || environment.AreKeysReleased(s.Trigger);
            if (!released && waited >= 5) throw new StartupCancelledException("快捷键超过 5 秒未松开，已取消启动。");
            if (released && waited >= s.Options.DelaySeconds) return;
            await clock.Delay(TimeSpan.FromMilliseconds(10), s.Cancellation.Token).ConfigureAwait(false);
        }
    }

    // Caller owns gate; this is the only down submission path.
    private void Submit(Session s)
    {
        CheckCurrent(s);
        pendingRelease = true; // Remains conservative if the adapter throws after entering native code.
        lastDownAt = clock.Seconds;
        var result = injector.Send(s.Mode == ClickMode.Click ? InputAction.Click : InputAction.Down);
        uint expected = s.Mode == ClickMode.Click ? 2u : 1u;
        pendingRelease = result.Sent != 0 && (s.Mode == ClickMode.Hold || result.Sent != expected);
        if (result.Sent != expected)
            throw new InputSubmissionException($"输入未能发送，请检查目标窗口权限与当前桌面状态（已提交 {result.Sent}/{expected}，系统代码 {result.NativeError}）。");
        if (!s.Begun)
        {
            s.Begun = true;
            count = 0;
            elapsed = 0;
            startedAt = clock.Seconds;
            stopRequestedAt = null;
            releaseCompletedAt = null;
            resultMode = s.Mode;
            targetCps = s.Options.Cps;
        }
        if (s.Mode == ClickMode.Click) count++;
        state = s.Mode == ClickMode.Click ? EngineState.Clicking : EngineState.Holding;
    }

    private async Task ReleaseAsync()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            lock (gate)
            {
                if (!pendingRelease) return;
                try
                {
                    if (injector.Send(InputAction.Up).Sent == 1)
                    {
                        pendingRelease = false;
                        releaseCompletedAt = clock.Seconds;
                        return;
                    }
                }
                catch { /* An uncertain release must remain blocked, including an adapter failure. */ }
            }
            if (attempt < 2) await clock.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        await StopAsync().ConfigureAwait(false);
        // The semaphore stays alive for already queued obsolete requests to drain safely.
    }
    private sealed class StartupCancelledException(string message) : Exception(message);
    private sealed class InputSubmissionException(string message) : Exception(message);
}
