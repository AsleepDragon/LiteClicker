using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiteClicker.Core;

public sealed record WindowPlacement(double Left = 80, double Top = 80, double Width = 520, double Height = 620);
public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string SelectedMode { get; init; } = "click";
    public string SpeedSelection { get; init; } = "preset3";
    public int CustomCps { get; init; } = 10;
    public int StartDelaySeconds { get; init; } = 2;
    public HotkeySet Hotkeys { get; init; } = HotkeySet.Default;
    public WindowPlacement WindowPlacement { get; init; } = new();
    [JsonIgnore] public int Cps => SpeedSelection.StartsWith("preset", StringComparison.Ordinal) && int.TryParse(SpeedSelection.AsSpan(6), out int n) && n is >= 1 and <= 5 ? SettingsRules.Presets[n - 1] : CustomCps;
    [JsonIgnore] public ClickMode Mode => SelectedMode == "hold" ? ClickMode.Hold : ClickMode.Click;
}
public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    private readonly SemaphoreSlim writes = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new JsonStringEnumConverter<KeyModifiers>() }
    };

    public SettingsLoadResult Load()
    {
        try
        {
            if (new FileInfo(FilePath).Length > 1024 * 1024) return new(new(), "配置文件过大，已使用默认设置。");
            string text = File.ReadAllText(FilePath);
            using var doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            bool repaired = false;
            int Number(JsonElement parent, string name, int fallback, Func<int, bool> valid)
            {
                if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.TryGetInt32Safe(out int n) && valid(n)) return n;
                repaired = true;
                return fallback;
            }
            string Choice(string name, string fallback, params string[] choices)
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && choices.Contains(v.GetString())) return v.GetString()!;
                repaired = true;
                return fallback;
            }
            int version = Number(root, "schemaVersion", 1, n => n >= 1);
            string? backupWarning = null;
            if (version != 1)
            {
                try
                {
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
                    string backup = Path.Combine(DirectoryPath, $"settings.version-{version}.{hash}.bak.json");
                    if (!File.Exists(backup)) File.Copy(FilePath, backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { backupWarning = "未知版本的配置备份失败；原文件不会自动覆盖。"; preserveUnknown = true; }
                repaired = true;
            }
            var defaults = new AppSettings();
            HotkeyBinding ReadBinding(JsonElement keys, string name, HotkeyBinding fallback)
            {
                if (keys.ValueKind == JsonValueKind.Object && keys.TryGetProperty(name, out var item))
                {
                    int key = Number(item, "key", fallback.Key, n => n is >= 0x70 and <= 0x7A);
                    KeyModifiers mods = fallback.Modifiers;
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("modifiers", out var value) && value.ValueKind == JsonValueKind.String && Enum.TryParse(value.GetString(), out KeyModifiers parsed) && (parsed & ~(KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Shift)) == 0) mods = parsed;
                    else repaired = true;
                    return new(key, mods);
                }
                repaired = true;
                return fallback;
            }
            JsonElement keys = default;
            if (root.ValueKind == JsonValueKind.Object) root.TryGetProperty("hotkeys", out keys);
            var bindings = new HotkeySet(ReadBinding(keys, "click", defaults.Hotkeys.Click), ReadBinding(keys, "hold", defaults.Hotkeys.Hold), ReadBinding(keys, "stop", defaults.Hotkeys.Stop));
            if (bindings.Validate() != null) { bindings = HotkeySet.Default; repaired = true; }
            WindowPlacement placement = new();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("windowPlacement", out var window) && window.ValueKind == JsonValueKind.Object)
            {
                double Dimension(string name, double fallback, double min, double max)
                {
                    if (window.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) && d >= min && d <= max) return d;
                    repaired = true;
                    return fallback;
                }
                placement = new(Dimension("left", 80, -100000, 100000), Dimension("top", 80, -100000, 100000), Dimension("width", 520, 440, 10000), Dimension("height", 620, 540, 10000));
            }
            else repaired = true;
            var settings = new AppSettings
            {
                SelectedMode = Choice("selectedMode", "click", "click", "hold"),
                SpeedSelection = Choice("speedSelection", "preset3", "preset1", "preset2", "preset3", "preset4", "preset5", "custom"),
                CustomCps = Number(root, "customCps", 10, n => n is >= 1 and <= 100),
                StartDelaySeconds = Number(root, "startDelaySeconds", 2, n => SettingsRules.Delays.Contains(n)),
                Hotkeys = bindings, WindowPlacement = placement
            };
            return new(settings, backupWarning ?? (repaired ? (version != 1 ? "配置版本较新，已保留原文件备份并读取兼容字段。无效字段已恢复默认。" : "配置含缺失或无效字段，已按字段恢复合法设置。") : null));
        }
        catch (FileNotFoundException) { return new(new(), null); }
        catch (DirectoryNotFoundException) { return new(new(), null); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(new(), "配置无法读取或已损坏，已使用默认设置。");
        }
    }

    private bool preserveUnknown;
    public async Task<string?> SaveAsync(AppSettings settings)
    {
        await writes.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (preserveUnknown) return "设置暂时无法保存，本次仍可使用（未知版本备份未成功）。";
            Directory.CreateDirectory(DirectoryPath);
            temporary = Path.Combine(DirectoryPath, $"settings.{Guid.NewGuid():N}.tmp");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, FilePath, true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "设置暂时无法保存，本次仍可使用。";
        }
        finally
        {
            if (temporary != null) try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            writes.Release();
        }
    }
}
internal static class JsonExtensions
{
    public static bool TryGetInt32Safe(this JsonElement e, out int n) { n = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out n); }
}
