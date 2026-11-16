using System.Runtime.InteropServices;
using LiteClicker.Core;
using LiteClicker.Windows;
using Xunit;

namespace LiteClicker.Tests;

public class ConfigurationTests
{
    [Theory] [InlineData("")] [InlineData("0")] [InlineData("-1")] [InlineData("101")] [InlineData("1.5")] [InlineData("abc")] [InlineData(" 1 ")] [InlineData("+1")]
    public void InvalidSpeedIsNeverSilentlyCoerced(string value) => Assert.False(SettingsRules.TryParseCps(value, out _));
    [Theory] [InlineData("1",1)] [InlineData("37",37)] [InlineData("100",100)]
    public void CustomValuesAreIntegers(string text, int expected) { Assert.True(SettingsRules.TryParseCps(text, out int actual)); Assert.Equal(expected, actual); }
    [Fact] public void X64InputLayoutMatchesWin32Abi()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(32, Marshal.SizeOf<MouseInput>());
        Assert.Equal(24, Marshal.OffsetOf<MouseInput>(nameof(MouseInput.ExtraInfo)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Data)).ToInt32());
        Assert.Equal(40, Marshal.SizeOf<NativeInput>());
    }
    [Fact] public void InvalidHotkeyRangeAndDuplicatesAreRejected()
    {
        Assert.NotNull(new HotkeySet(new(0x7B), new(0x76), new(0x77)).Validate());
        Assert.NotNull(new HotkeySet(new(0x75), new(0x75), new(0x77)).Validate());
        Assert.NotNull(new HotkeySet(new(0x75,(KeyModifiers)8), new(0x76), new(0x77)).Validate());
    }

    [Fact] public void FailedRegistrationRollsBackOldBindings()
    {
        var registrar = new FakeRegistrar(); using var manager = new HotkeyManager(registrar);
        Assert.True(manager.Apply(HotkeySet.Default).Success);
        registrar.Blocked.Add(0x70);
        var result = manager.Apply(HotkeySet.Default with { Hold = new(0x70) });
        Assert.False(result.Success); Assert.True(result.Ready); Assert.Equal(HotkeySet.Default, manager.Active);
        Assert.Equal(3, registrar.Active.Count);
        manager.Dispose(); Assert.Empty(registrar.Active);
    }
    [Fact] public void RollbackFailureDoesNotAdvertiseReadyOrLeakPartialRegistration()
    {
        var registrar = new FakeRegistrar(); using var manager = new HotkeyManager(registrar);
        Assert.True(manager.Apply(HotkeySet.Default).Success);
        registrar.Blocked.UnionWith([0x70,0x75]);
        var result = manager.Apply(HotkeySet.Default with { Click = new(0x70) });
        Assert.False(result.Ready); Assert.Null(manager.Active); Assert.Empty(registrar.Active);
    }
    [Fact] public void InitialConflictDisablesAllStartPaths()
    {
        var registrar = new FakeRegistrar(); registrar.Blocked.Add(0x77);
        using var manager = new HotkeyManager(registrar);
        Assert.False(manager.Apply(HotkeySet.Default).Ready); Assert.Empty(registrar.Active);
    }

    [Fact] public async Task SettingsRoundTripPersistsConfigurationButNotRuntimeState()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(folder);
            var settings = new AppSettings { SelectedMode = "hold", SpeedSelection = "custom", CustomCps = 37, Hotkeys = HotkeySet.Default with { Click = new(0x75, KeyModifiers.Ctrl | KeyModifiers.Shift) } };
            Assert.Null(await store.SaveAsync(settings));
            var result = store.Load(); Assert.Null(result.Warning); Assert.Equal(settings, result.Settings);
            string json = File.ReadAllText(store.FilePath); Assert.DoesNotContain("running", json); Assert.DoesNotContain("pendingRelease", json);
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [Fact] public void CorruptFieldsFallbackIndependentlyAndUnknownVersionGetsBackup()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var store = new SettingsStore(folder);
            File.WriteAllText(store.FilePath, """{"schemaVersion":99,"selectedMode":"hold","speedSelection":"custom","customCps":101,"startDelaySeconds":5}""");
            var result = store.Load(); Assert.NotNull(result.Warning); Assert.Equal("hold", result.Settings.SelectedMode);
            Assert.Equal(10, result.Settings.CustomCps); Assert.Equal(5, result.Settings.StartDelaySeconds);
            Assert.Single(Directory.GetFiles(folder, "*.bak.json"));
            File.WriteAllText(store.FilePath, "{broken");
            Assert.Equal(new AppSettings(), store.Load().Settings);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact] public async Task UnwritableConfigurationReturnsWarningWithoutInvalidatingMemorySettings()
    {
        string file = Path.GetTempFileName();
        try { var store = new SettingsStore(file); Assert.NotNull(await store.SaveAsync(new())); }
        finally { File.Delete(file); }
    }
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public readonly HashSet<int> Blocked = [];
        public readonly Dictionary<int,HotkeyBinding> Active = [];
        public bool Register(int id, HotkeyBinding binding, out int error)
        {
            error = Blocked.Contains(binding.Key) ? 1409 : 0;
            if (error != 0) return false;
            Assert.False(Active.ContainsKey(id)); Active.Add(id, binding); return true;
        }
        public void Unregister(int id) => Active.Remove(id);
    }
}
