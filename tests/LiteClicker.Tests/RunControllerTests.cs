using LiteClicker.Core;
using Xunit;

namespace LiteClicker.Tests;

public class RunControllerTests
{
    [Fact] public async Task HoldSendsExactlyOneDownAndOneUpWithIdempotentStop()
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Hold, new(10, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Holding);
        await Task.Delay(80);
        Assert.Equal([InputAction.Down], input.Events.ToArray());
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => engine.StopAsync()));
        Assert.Equal([InputAction.Down, InputAction.Up], input.Events.ToArray());
        Assert.False(engine.Snapshot.PendingRelease);
        Assert.Equal(EngineState.Idle, engine.Snapshot.State);
    }

    [Fact] public async Task ClickCountsOnlySuccessfulPairsAndStopWakesOneSecondWait()
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Click, new(1, 0));
        await Eventually.True(() => engine.Snapshot.ClickCount == 1);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await engine.StopAsync();
        Assert.True(timer.ElapsedMilliseconds < 300, "Must not wait for the 1-second click period.");
        Assert.Equal([InputAction.Click], input.Events.ToArray());
        Assert.Equal(1, engine.Snapshot.ClickCount);
    }

    [Fact] public async Task SwitchFromHoldReleasesBeforeFirstClick()
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Hold, new(1, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Holding);
        await engine.ToggleAsync(ClickMode.Click, new(1, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Clicking);
        await engine.StopAsync();
        Assert.Equal([InputAction.Down, InputAction.Up, InputAction.Click], input.Events.ToArray());
    }

    [Fact] public async Task PartialClickIsNotCountedAndGetsCompensatingUp()
    {
        var input = new FakeInput(); input.Results.Enqueue(new(1));
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Click, new(10, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Error);
        Assert.Equal([InputAction.Click, InputAction.Up], input.Events.ToArray());
        Assert.Equal(0, engine.Snapshot.ClickCount);
        Assert.False(engine.Snapshot.PendingRelease);
    }

    [Theory] [InlineData(ClickMode.Click)] [InlineData(ClickMode.Hold)]
    public async Task ZeroSubmissionDoesNotClaimRunningOrReleasePhysicalMouse(ClickMode mode)
    {
        var input = new FakeInput(); input.Results.Enqueue(new(0, 5));
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(mode, new(10, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Error);
        Assert.Single(input.Events);
        Assert.False(engine.Snapshot.PendingRelease);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Elapsed);
    }

    [Fact] public async Task ReleaseFailureIsBoundedAndBlocksNewDownUntilExplicitRetry()
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Hold, new(10, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Holding);
        for (int i = 0; i < 3; i++) input.Results.Enqueue(new(0));
        await engine.StopAsync();
        Assert.True(engine.Snapshot.PendingRelease);
        Assert.False(engine.Snapshot.CanEdit);
        Assert.Equal(3, input.Events.Count(e => e == InputAction.Up));
        // A start cannot bypass failed cleanup. Even if it retries cleanup it never sends a new down.
        await engine.ToggleAsync(ClickMode.Click, new(10, 0));
        Assert.Equal(1, input.Events.Count(e => e == InputAction.Down));
        Assert.DoesNotContain(InputAction.Click, input.Events);
        await engine.StopAsync();
        Assert.False(engine.Snapshot.PendingRelease);
    }

    [Fact] public async Task CancelledCountdownKeepsLastStatisticsAndCannotWakeLater()
    {
        var input = new FakeInput(); var clock = new ManualClock();
        await using var engine = new RunController(input, input, clock);
        await engine.ToggleAsync(ClickMode.Click, new(1, 0));
        await Eventually.True(() => engine.Snapshot.ClickCount == 1);
        clock.Advance(.5);
        await engine.StopAsync();
        var previous = engine.Snapshot;
        await engine.ToggleAsync(ClickMode.Hold, new(10, 2));
        await Eventually.True(() => clock.Waiters > 0);
        await engine.StopAsync();
        clock.Advance(10);
        await Task.Delay(20);
        Assert.Equal(previous.ClickCount, engine.Snapshot.ClickCount);
        Assert.Equal(previous.Elapsed, engine.Snapshot.Elapsed);
        Assert.Equal([InputAction.Click], input.Events.ToArray());
    }

    [Fact] public async Task ModifierWaitAndCountdownShareOneDeadline()
    {
        var input = new FakeInput { KeysReleased = false }; var clock = new ManualClock();
        await using var engine = new RunController(input, input, clock);
        await engine.ToggleAsync(ClickMode.Hold, new(10, 2), new(0x75, KeyModifiers.Ctrl));
        await Eventually.True(() => clock.Waiters > 0);
        clock.Advance(3);
        await Task.Delay(20);
        Assert.Empty(input.Events);
        input.KeysReleased = true;
        clock.Advance(.02);
        await Eventually.True(() => engine.Snapshot.State == EngineState.Holding);
        await engine.StopAsync();
        Assert.Equal([InputAction.Down, InputAction.Up], input.Events.ToArray());
    }

    [Fact] public async Task UnreleasedShortcutTimesOutWithoutInput()
    {
        var input = new FakeInput { KeysReleased = false }; var clock = new ManualClock();
        await using var engine = new RunController(input, input, clock);
        await engine.ToggleAsync(ClickMode.Click, new(10, 2), new(0x75));
        await Eventually.True(() => clock.Waiters > 0);
        clock.Advance(5.01);
        await Eventually.True(() => engine.Snapshot.State == EngineState.Idle);
        Assert.Empty(input.Events);
        Assert.Contains("5 秒", engine.Snapshot.Message);
    }

    [Theory] [InlineData(true, false)] [InlineData(false, true)]
    public async Task StartProtectionPreventsAnyInput(bool ownWindow, bool leftDown)
    {
        var input = new FakeInput { OwnWindow = ownWindow, LeftDown = leftDown };
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Click, new(10, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Idle);
        Assert.Empty(input.Events);
    }

    [Theory] [InlineData(ClickMode.Click)] [InlineData(ClickMode.Hold)]
    public async Task EnteringOwnWindowStopsAndReleases(ClickMode mode)
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(mode, new(100, 0));
        await Eventually.True(() => engine.Snapshot.State == (mode == ClickMode.Click ? EngineState.Clicking : EngineState.Holding));
        input.OwnWindow = true;
        await Eventually.True(() => engine.Snapshot.State == EngineState.Idle);
        Assert.False(engine.Snapshot.PendingRelease);
        if (mode == ClickMode.Hold) Assert.Equal([InputAction.Down, InputAction.Up], input.Events.ToArray());
    }

    [Fact] public async Task DelayedSchedulerSkipsMissedClicksInsteadOfBursting()
    {
        var input = new FakeInput(); var clock = new ManualClock();
        await using var engine = new RunController(input, input, clock);
        await engine.ToggleAsync(ClickMode.Click, new(100, 0));
        await Eventually.True(() => clock.Waiters > 0);
        clock.Advance(20);
        await Eventually.True(() => input.Events.Count == 2 && clock.Waiters > 0);
        await Task.Delay(20);
        Assert.Equal(2, input.Events.Count);
        await engine.StopAsync();
    }

    [Fact] public async Task OneHundredAlternatingRequestsLeaveNoStaleRestart()
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(engine.ToggleAsync(i % 2 == 0 ? ClickMode.Hold : ClickMode.Click, new(100, 1)));
            tasks.Add(engine.StopAsync());
        }
        await Task.WhenAll(tasks);
        await engine.StopAsync();
        await Task.Delay(30);
        Assert.Equal(EngineState.Idle, engine.Snapshot.State);
        Assert.Empty(input.Events);
    }

    [Fact] public async Task StopInvalidatesQueuedStartWhileNativeCallIsInFlight()
    {
        var input = new FakeInput();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        input.BeforeSend = a => { if (a == InputAction.Down) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); } };
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Hold, new(10, 0));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        var stop = Task.Run(() => engine.StopAsync());
        await Task.Delay(20);
        release.Set();
        await stop;
        Assert.Equal([InputAction.Down, InputAction.Up], input.Events.ToArray());
        Assert.Equal(EngineState.Idle, engine.Snapshot.State);
    }

    [Fact] public async Task ChangingCountdownModeRestartsFullDelay()
    {
        var input = new FakeInput(); var clock = new ManualClock();
        await using var engine = new RunController(input, input, clock);
        await engine.ToggleAsync(ClickMode.Click, new(10, 2));
        await Eventually.True(() => clock.Waiters > 0);
        clock.Advance(1.5);
        await engine.ToggleAsync(ClickMode.Hold, new(10, 2));
        await Eventually.True(() => clock.Waiters > 0);
        clock.Advance(1.5);
        await Task.Delay(20);
        Assert.Empty(input.Events);
        clock.Advance(.51);
        await Eventually.True(() => engine.Snapshot.State == EngineState.Holding);
        await engine.StopAsync();
        Assert.Equal([InputAction.Down, InputAction.Up], input.Events.ToArray());
    }

    [Fact] public async Task SuccessfulRestartClearsStatisticsOnlyWhenFirstInputSucceeds()
    {
        var input = new FakeInput(); var clock = new ManualClock();
        await using var engine = new RunController(input, input, clock);
        await engine.ToggleAsync(ClickMode.Click, new(1, 0));
        await Eventually.True(() => clock.Waiters > 0);
        clock.Advance(1);
        await Eventually.True(() => engine.Snapshot.ClickCount == 2);
        await engine.StopAsync();
        await engine.ToggleAsync(ClickMode.Click, new(1, 2));
        Assert.Equal(2, engine.Snapshot.ClickCount);
        await Eventually.True(() => clock.Waiters > 0);
        clock.Advance(2.01);
        await Eventually.True(() => engine.Snapshot.State == EngineState.Clicking);
        Assert.Equal(1, engine.Snapshot.ClickCount);
        Assert.Equal(TimeSpan.Zero, engine.Snapshot.Elapsed);
        await engine.StopAsync();
    }

    [Fact] public async Task ActiveRapidModeSwitchesNeverOverlapInputOwnership()
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        for (int i = 0; i < 30; i++)
        {
            await engine.ToggleAsync(ClickMode.Hold, new(100, 0));
            await Eventually.True(() => engine.Snapshot.State == EngineState.Holding);
            await engine.ToggleAsync(ClickMode.Click, new(100, 0));
            await Eventually.True(() => engine.Snapshot.State == EngineState.Clicking);
            await engine.StopAsync();
        }
        bool held = false;
        foreach (var action in input.Events)
        {
            if (action == InputAction.Down) { Assert.False(held); held = true; }
            else if (action == InputAction.Up) { Assert.True(held); held = false; }
            else Assert.False(held);
        }
        Assert.False(held);
    }

    [Fact] public async Task AdapterExceptionRemainsConservativeAndCompensates()
    {
        var input = new FakeInput { BeforeSend = action => { if (action == InputAction.Down) throw new InvalidOperationException("simulated adapter failure"); } };
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        await engine.ToggleAsync(ClickMode.Hold, new(10, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Error);
        Assert.Equal([InputAction.Up], input.Events.ToArray());
        Assert.False(engine.Snapshot.PendingRelease);
    }

    [Fact] public async Task HotkeyBlockerPreventsEvenDirectControllerStart()
    {
        var input = new FakeInput();
        await using var engine = new RunController(input, input, new SystemMonotonicClock());
        engine.SetStartBlocker("快捷键冲突");
        await engine.ToggleAsync(ClickMode.Click, new(10, 0));
        Assert.Equal(EngineState.Error, engine.Snapshot.State);
        Assert.Empty(input.Events);
        engine.SetStartBlocker(null);
        await engine.ToggleAsync(ClickMode.Hold, new(10, 0));
        await Eventually.True(() => engine.Snapshot.State == EngineState.Holding);
        await engine.StopAsync();
    }

    [Fact] public async Task StopAtCountdownBoundaryLeavesNoPostStopDown()
    {
        for (int i = 0; i < 30; i++)
        {
            var input = new FakeInput(); var clock = new ManualClock();
            await using var engine = new RunController(input, input, clock);
            await engine.ToggleAsync(ClickMode.Hold, new(10, 1));
            await Eventually.True(() => clock.Waiters > 0);
            await Task.WhenAll(Task.Run(() => clock.Advance(1)), Task.Run(() => engine.StopAsync()));
            int completed = input.Events.Count;
            clock.Advance(100);
            await Task.Yield();
            Assert.Equal(completed, input.Events.Count);
            Assert.False(engine.Snapshot.PendingRelease);
            Assert.True(completed == 0 || input.Events.SequenceEqual([InputAction.Down, InputAction.Up]));
        }
    }
}
