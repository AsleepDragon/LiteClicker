using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using LiteClicker.Core;

namespace LiteClicker.App.ViewModels;

public sealed class HotkeyEditorRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public static IReadOnlyList<string> Keys { get; } = Enumerable.Range(1, 11).Select(n => $"F{n}").ToArray();
    public string Key { get; set; } = "F6";
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public HotkeyEditorRow(string name, HotkeyBinding binding) { Name = name; Load(binding); }
    public HotkeyBinding ToBinding() => new(0x6F + int.Parse(Key.AsSpan(1)), (Ctrl ? KeyModifiers.Ctrl : 0) | (Alt ? KeyModifiers.Alt : 0) | (Shift ? KeyModifiers.Shift : 0));
    public void Load(HotkeyBinding binding)
    {
        Key = $"F{binding.Key - 0x6F}";
        Ctrl = binding.Modifiers.HasFlag(KeyModifiers.Ctrl); Alt = binding.Modifiers.HasFlag(KeyModifiers.Alt); Shift = binding.Modifiers.HasFlag(KeyModifiers.Shift);
        PropertyChanged?.Invoke(this, new(null));
    }
}
