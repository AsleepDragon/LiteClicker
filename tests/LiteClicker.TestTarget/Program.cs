using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using LiteClicker.Core;
using LiteClicker.Windows;

namespace LiteClicker.TestTarget;

internal static class Program
{
    [STAThread] private static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new TargetForm()); }
}
internal sealed class TargetForm : Form
{
    private readonly TargetCanvas canvas = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(239, 246, 255) };
    private readonly Label metrics = new() { AutoSize = false, Height = 74, Dock = DockStyle.Bottom, Padding = new(16, 6, 16, 4) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly WindowsHotkeyRegistrar? registrar;
    private readonly List<int> blocked = new();
    private readonly double frequency = Stopwatch.Frequency;
    private readonly List<MouseEvent> records = new();
    private long? measurementStart;
    private long down, up, doubleDown, markedDown, markedUp;
    private const int MaxRecords = 500000;
    private bool overflow;
    private readonly Label header = new() { Text = "独立进程鼠标消息接收器\n在蓝色区域内启动 LiteClicker。记录 DOWN / DBLCLK / UP 与 dwExtraInfo，不以 Click 事件计数。", Dock = DockStyle.Top, Height = 70, Padding = new(16) };
    public TargetForm()
    {
        Text = "LiteClicker · Windows 验收接收器"; Width = 920; Height = 620; MinimumSize = new(640, 420);
        Font = new("Microsoft YaHei UI", 10);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(10, 4, 10, 4) };
        var reset = new Button { Text = "清空记录", AutoSize = true }; reset.Click += (_, _) => Reset();
        var arm = new Button { Text = "5 秒后开始 60 秒测量", AutoSize = true }; arm.Click += (_, _) => { Reset(); measurementStart = Stopwatch.GetTimestamp() + (long)(5 * frequency); };
        var save = new Button { Text = "导出 CSV", AutoSize = true }; save.Click += (_, _) => Export();
        var occupy = new Button { Text = "占用 F6（冲突测试）", AutoSize = true };
        occupy.Click += (_, _) =>
        {
            if (registrar == null) return;
            if (blocked.Count > 0) { registrar.Unregister(0x1201); blocked.Clear(); occupy.Text = "占用 F6（冲突测试）"; return; }
            if (registrar.Register(0x1201, new(0x75), out int error)) { blocked.Add(0x1201); occupy.Text = "释放 F6"; }
            else MessageBox.Show($"F6 注册失败：{error}。先退出 LiteClicker 再测试冲突。");
        };
        actions.Controls.AddRange(new Control[] { reset, arm, save, occupy });
        Controls.Add(canvas); Controls.Add(metrics); Controls.Add(actions); Controls.Add(header);
        canvas.MouseMessage += OnMouseMessage;
        registrar = new WindowsHotkeyRegistrar(Handle);
        timer.Tick += (_, _) => UpdateMetrics(); timer.Start();
        FormClosed += (_, _) => { timer.Dispose(); foreach (int id in blocked) registrar.Unregister(id); };
    }
    private void Reset() { records.Clear(); down = up = doubleDown = markedDown = markedUp = 0; measurementStart = null; overflow = false; }
    private void OnMouseMessage(int message, nuint extra)
    {
        long now = Stopwatch.GetTimestamp(); bool marked = extra == WindowsInputInjector.EventMarker;
        if (message == 0x202) { up++; if (marked) markedUp++; }
        else { down++; if (message == 0x203) doubleDown++; if (marked) markedDown++; }
        bool inWindow = measurementStart is { } start && now >= start && now < start + 60 * frequency;
        if (records.Count < MaxRecords) records.Add(new(now, message, marked, inWindow)); else overflow = true;
    }
    private void UpdateMetrics()
    {
        string measurement = "未启用固定测量窗口";
        if (measurementStart is { } start)
        {
            double seconds = (Stopwatch.GetTimestamp() - start) / frequency;
            long n = 0; foreach (var r in records) if (r.InWindow && r.Marked && r.Message != 0x202) n++;
            measurement = seconds < 0 ? $"测量将在 {-seconds:F1} 秒后开始" : seconds < 60 ? $"固定测量中：{seconds:F1} / 60 秒" : $"固定 60 秒已完成：{n} 次标记按下，{n / 60d:F3} 次/秒";
        }
        metrics.Text = $"全部按下 {down}（含双击按下 {doubleDown}） · 抬起 {up}  |  LiteClicker 标记：按下 {markedDown} / 抬起 {markedUp}\n{measurement}{(overflow ? "；记录已达上限，请清空" : "")}";
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"LiteClicker-target-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var text = new StringBuilder();
        text.AppendLine($"# os={Environment.OSVersion};runtime={Environment.Version};stopwatchFrequency={Stopwatch.Frequency};measurementStartTicks={measurementStart};exportedAtTicks={Stopwatch.GetTimestamp()};windowSeconds=60;overflow={overflow}");
        text.AppendLine("monotonicTicks,monotonicSeconds,message,liteClickerMarked,inFixedWindow");
        foreach (var r in records) text.AppendLine($"{r.Ticks},{(r.Ticks / frequency).ToString("F9", CultureInfo.InvariantCulture)},{(r.Message == 0x201 ? "WM_LBUTTONDOWN" : r.Message == 0x203 ? "WM_LBUTTONDBLCLK" : "WM_LBUTTONUP")},{r.Marked},{r.InWindow}");
        File.WriteAllText(dialog.FileName, text.ToString(), new UTF8Encoding(true));
    }
    private sealed record MouseEvent(long Ticks, int Message, bool Marked, bool InWindow);
}
internal sealed class TargetCanvas : Panel
{
    public event Action<int, nuint>? MouseMessage;
    [DllImport("user32.dll")] private static extern nint GetMessageExtraInfo();
    public TargetCanvas() { SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.DoubleBuffer | ControlStyles.UserPaint, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, "将鼠标放在这里\nF6 连点  ·  F7 按住  ·  F8 停止\n\n也可按住并移动，观察框选区域", Font, new Rectangle(20, 20, Width - 40, 150), Color.FromArgb(29, 78, 216));
        if (dragStart is { } from && dragCurrent is { } to)
        {
            var rect = Rectangle.FromLTRB(Math.Min(from.X, to.X), Math.Min(from.Y, to.Y), Math.Max(from.X, to.X), Math.Max(from.Y, to.Y));
            using var pen = new Pen(Color.RoyalBlue, 2); e.Graphics.DrawRectangle(pen, rect);
        }
    }
    private Point? dragStart, dragCurrent;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x201 or 0x203 or 0x202) MouseMessage?.Invoke(m.Msg, unchecked((nuint)GetMessageExtraInfo()));
        if (m.Msg is 0x201 or 0x203) { dragStart = PointToClient(Cursor.Position); dragCurrent = dragStart; }
        if (m.Msg == 0x200 && ((int)m.WParam & 1) != 0) { dragCurrent = PointToClient(Cursor.Position); Invalidate(); }
        if (m.Msg == 0x202) { dragStart = null; dragCurrent = null; Invalidate(); }
        base.WndProc(ref m);
    }
}
