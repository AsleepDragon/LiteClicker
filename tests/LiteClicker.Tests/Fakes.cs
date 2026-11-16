using System.Collections.Concurrent;
using LiteClicker.Core;
using Xunit;

namespace LiteClicker.Tests;

internal sealed class FakeInput : IInputInjector, IInputEnvironment
{
    public readonly ConcurrentQueue<InputAction> Events = new();
    public readonly ConcurrentQueue<InputResult> Results = new();
    public volatile bool OwnWindow, LeftDown;
    public volatile bool KeysReleased = true;
    public Action<InputAction>? BeforeSend;
    public InputResult Send(InputAction action)
    {
        BeforeSend?.Invoke(action);
        Events.Enqueue(action);
        return Results.TryDequeue(out var result) ? result : new(action == InputAction.Click ? 2u : 1u);
    }
    public bool IsLeftButtonDown() => LeftDown;
    public bool IsPointerOverOwnWindow() => OwnWindow;
    public bool AreKeysReleased(HotkeyBinding binding) => KeysReleased;
}

internal sealed class ManualClock : IMonotonicClock
{
    private readonly object gate = new();
    private double now;
    private readonly List<(double At, TaskCompletionSource Source, CancellationTokenRegistration Registration)> waiters = [];
    public double Seconds { get { lock (gate) return now; } }
    public int Waiters { get { lock (gate) return waiters.Count(x => !x.Source.Task.IsCompleted); } }
    public Task Delay(TimeSpan duration, CancellationToken token)
    {
        lock (gate)
        {
            if (token.IsCancellationRequested) return Task.FromCanceled(token);
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = token.Register(() => source.TrySetCanceled(token));
            waiters.Add((now + duration.TotalSeconds, source, registration));
            return source.Task;
        }
    }
    public void Advance(double seconds)
    {
        lock (gate)
        {
            now += seconds;
            foreach (var w in waiters.Where(x => x.At <= now || x.Source.Task.IsCompleted).ToArray())
            {
                w.Source.TrySetResult();
                w.Registration.Dispose();
                waiters.Remove(w);
            }
        }
    }
}

internal static class Eventually
{
    public static async Task True(Func<bool> predicate)
    {
        for (int i = 0; i < 500; i++)
        {
            if (predicate()) return;
            await Task.Delay(2);
        }
        Assert.True(predicate(), "Timed out waiting for observable state.");
    }
}
