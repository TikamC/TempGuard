using System.Diagnostics;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

namespace TempGuard;

// ─────────────────────────────────────────────────────────────
//  Настройки
// ─────────────────────────────────────────────────────────────
internal static class Config
{
    public const float ThresholdC   = 92f;   // порог срабатывания, °C
    public const float HysteresisC  = 2f;    // индикация гаснет, когда температура < порог - гистерезис
    public const int   IntervalMs   = 1000;  // период опроса
    public const int   MarginPx     = 20;    // отступ окна от края экрана
    public const float MaxSaneC     = 150f;  // всё выше считаем мусором от датчика
}

internal sealed record Reading(string Id, string Hardware, string Sensor, float Value);

// ─────────────────────────────────────────────────────────────
//  Чтение всех температурных датчиков
// ─────────────────────────────────────────────────────────────
internal sealed class SensorReader : IDisposable
{
    private readonly Computer _pc = new()
    {
        IsCpuEnabled         = true,
        IsGpuEnabled         = true,
        IsMemoryEnabled      = true,   // температура DIMM (если модуль/плата поддерживают)
        IsMotherboardEnabled = true,   // датчики материнской платы / Super I/O
        IsStorageEnabled     = true,   // SSD / NVMe / HDD
        IsControllerEnabled  = true,
        IsNetworkEnabled     = true,
        IsPsuEnabled         = true,
        IsBatteryEnabled     = true
    };

    public SensorReader() => _pc.Open();

    public List<Reading> ReadAll()
    {
        var list = new List<Reading>();
        foreach (var hw in _pc.Hardware)
            Collect(hw, list);
        return list;
    }

    private static void Collect(IHardware hw, List<Reading> list)
    {
        try { hw.Update(); } catch { /* отдельное устройство может сбоить — пропускаем */ }

        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || s.Value is not float v)
                continue;
            if (float.IsNaN(v) || v <= 0 || v > Config.MaxSaneC)
                continue;

            // «Запас до TjMax» — это не температура, а оставшийся запас; даёт ложные срабатывания
            if (s.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains("Thermal Margin",    StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add(new Reading(s.Identifier.ToString(), hw.Name, s.Name, v));
        }

        foreach (var sub in hw.SubHardware)
            Collect(sub, list);
    }

    public void Dispose() => _pc.Close();
}

//  Оверлей-предупреждение (поверх всех окон, клики проходят сквозь него)
internal sealed class OverlayForm : Form
{
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    private readonly Font _font = new("Segoe UI", 8f, FontStyle.Bold);
    private string _text = "";

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        BackColor       = Color.FromArgb(190, 25, 25);
        Opacity         = 0.80;
        DoubleBuffered  = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void ShowWarning(IReadOnlyList<Reading> hot, bool rightSide)
    {
        var lines = new List<string> { "⚠ OVERHEATING!" };
        lines.AddRange(hot.OrderByDescending(r => r.Value)
                          .Select(r => $"{Short(r.Hardware)} / {r.Sensor}: {r.Value:0} °C"));
        var text = string.Join(Environment.NewLine, lines);

        if (text != _text)
        {
            _text = text;
            var size = TextRenderer.MeasureText(_text, _font);
            Size = new Size(size.Width + 24, size.Height + 18);
            Invalidate();
        }

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(
            rightSide ? wa.Right - Width - Config.MarginPx : wa.Left + Config.MarginPx,
            wa.Top + Config.MarginPx);

        if (!Visible) Show();
        TopMost = false; TopMost = true;
    }

    public void HideWarning()
    {
        if (Visible) Hide();
        _text = "";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        TextRenderer.DrawText(e.Graphics, _text, _font,
            new Rectangle(12, 9, Width - 12, Height - 9), Color.White,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding);
    }

    private static string Short(string s) => s.Length > 28 ? s[..28] + "…" : s;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _font.Dispose();
        base.Dispose(disposing);
    }
}

//  Приложение в трее + фоновый опрос

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly OverlayForm _overlay = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext _ui = SynchronizationContext.Current!;
    private readonly ToolStripMenuItem _rightItem = new("Показывать справа");
    private readonly ToolStripMenuItem _leftItem  = new("Показывать слева");

    private readonly HashSet<string> _alarmed = new();   // датчики, находящиеся «в тревоге»
    private volatile bool _right;

    public TrayApp(bool startLeft)
    {
        _right = !startLeft;
        _rightItem.Checked = _right;
        _leftItem.Checked  = !_right;
        _rightItem.Click += (_, _) => SetSide(true);
        _leftItem.Click  += (_, _) => SetSide(false);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"TempGuard — порог {Config.ThresholdC:0} °C") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_rightItem);
        menu.Items.Add(_leftItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "TempGuard: запуск…",
            ContextMenuStrip = menu,
            Visible = true
        };

        Task.Run(() => PollLoop(_cts.Token));
    }

    private void SetSide(bool right)
    {
        _right = right;
        _rightItem.Checked = right;
        _leftItem.Checked  = !right;
    }

    private async Task PollLoop(CancellationToken ct)
    {
        SensorReader? reader = null;
        try
        {
            reader = new SensorReader();
        }
        catch (Exception ex)
        {
            _ui.Post(_ => _tray.ShowBalloonTip(5000, "TempGuard",
                "Не удалось инициализировать датчики: " + ex.Message, ToolTipIcon.Error), null);
            return;
        }

        using (reader)
        {
            while (!ct.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var readings = reader.ReadAll();
                    _ui.Post(_ => Apply(readings), null);
                }
                catch { /* временная ошибка чтения — попробуем в следующую секунду */ }

                var wait = Config.IntervalMs - (int)sw.ElapsedMilliseconds;
                try { await Task.Delay(Math.Max(wait, 50), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // выполняется в UI-потоке
    private void Apply(List<Reading> readings)
    {
        if (readings.Count == 0)
        {
            _tray.Text = "TempGuard: датчики температуры не найдены";
            return;
        }

        var max = readings.MaxBy(r => r.Value)!;
        var tip = $"TempGuard: макс. {max.Value:0} °C ({max.Hardware})";
        _tray.Text = tip.Length > 120 ? tip[..120] : tip;

        // гистерезис: входим в тревогу при >= порога, выходим при < порога - гистерезис
        foreach (var r in readings)
        {
            if (r.Value >= Config.ThresholdC) _alarmed.Add(r.Id);
            else if (r.Value < Config.ThresholdC - Config.HysteresisC) _alarmed.Remove(r.Id);
        }

        var hot = readings.Where(r => _alarmed.Contains(r.Id)).ToList();
        if (hot.Count > 0) _overlay.ShowWarning(hot, _right);
        else               _overlay.HideWarning();
    }

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        base.ExitThreadCore();
    }
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Global\TempGuard_SingleInstance", out bool isNew);
        if (!isNew) return;   // уже запущено

        ApplicationConfiguration.Initialize();
        bool startLeft = args.Any(a => a.Equals("--left", StringComparison.OrdinalIgnoreCase));
        Application.Run(new TrayApp(startLeft));
    }
}
