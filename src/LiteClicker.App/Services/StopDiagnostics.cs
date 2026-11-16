using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LiteClicker.Core;

namespace LiteClicker.App.Services;

/// <summary>Opt-in measurement: one row per stop, no target, content, or click-by-click trace.</summary>
public sealed class StopDiagnostics(string directory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task RecordAsync(EngineSnapshot snapshot, double completedAt)
    {
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "stop-0.csv");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024 - 1024)
            {
                File.Delete(Path.Combine(directory, "stop-2.csv"));
                if (File.Exists(Path.Combine(directory, "stop-1.csv"))) File.Move(Path.Combine(directory, "stop-1.csv"), Path.Combine(directory, "stop-2.csv"));
                File.Move(path, Path.Combine(directory, "stop-1.csv"));
            }
            if (!File.Exists(path)) await File.WriteAllTextAsync(path, "utc,mode,state,requestedSeconds,lastDownSeconds,releasedSeconds,completedSeconds,stopLatencyMs,pendingRelease\n");
            string N(double? d) => d?.ToString("F9", CultureInfo.InvariantCulture) ?? "";
            string row = $"{DateTime.UtcNow:O},{snapshot.Mode},{snapshot.State},{N(snapshot.StopRequestedAt)},{N(snapshot.LastDownSubmittedAt)},{N(snapshot.ReleaseCompletedAt)},{N(completedAt)},{N(snapshot.StopRequestedAt.HasValue ? (completedAt - snapshot.StopRequestedAt) * 1000 : null)},{snapshot.PendingRelease}\n";
            await File.AppendAllTextAsync(path, row);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Optional instrumentation cannot block input cleanup. */ }
        finally { gate.Release(); }
    }
}
