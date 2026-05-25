using System.Drawing.Drawing2D;
using ConduentResourceMonitor.Checks;
using ConduentResourceMonitor.Repairs;
using ConduentResourceMonitor.Services;

namespace ConduentResourceMonitor;

public class TrayApp : ApplicationContext
{
    private readonly AppMode _mode;
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly MonitorService _monitor;
    private readonly PacServerService _pacServer;
    private readonly List<IRepair> _repairs;
    private readonly LogForm _logForm;
    private readonly Icon _greenIcon;
    private readonly Icon _redIcon;

    public TrayApp(AppSettings settings, bool showLog, bool repairOnStart = false)
    {
        _settings = settings;
        _mode = settings.AppMode!.Value; // validated non-null before TrayApp is constructed

        _greenIcon = CreateCircleIcon(Color.LimeGreen);
        _redIcon = CreateCircleIcon(Color.Red);

        _logForm = new LogForm();

        _pacServer = new PacServerService(settings);
        _pacServer.Start();

        var checks = BuildChecks(_mode, settings);
        _repairs = BuildRepairs(_mode, settings);

        _monitor = new MonitorService(checks, settings.CheckIntervalSeconds);
        _monitor.ResultsUpdated += OnResultsUpdated;
        _monitor.CheckFailed += OnCheckFailed;

        _tray = new NotifyIcon
        {
            Icon = _greenIcon,
            Text = $"{_mode} Monitor - Starting...",
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        if (showLog) _logForm.Show();

        if (repairOnStart && _mode == AppMode.Hub)
        {
            var repair = _repairs.OfType<PortProxyRepair>().FirstOrDefault();
            repair?.Execute(startupDelay: false);
        }

        _ = _monitor.Start();
    }

    private static IReadOnlyList<ICheck> BuildChecks(AppMode mode, AppSettings settings) => mode switch
    {
        AppMode.Hub =>
        [
            new ProxyCheck("pproxy", settings),
            new PortForwardCheck("localhost", 8888, 13389),
            new WireGuardCheck(settings)
        ],
        AppMode.Travel =>
        [
            new ProxyCheck("VPN", settings),
            new PortForwardCheck("conduent-resource", 13389),
            new PacServerCheck(settings),
            new WireGuardCheck(settings)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private List<IRepair> BuildRepairs(AppMode mode, AppSettings settings)
    {
        var repairs = new List<IRepair>();
        if (mode == AppMode.Hub)
        {
            repairs.Add(new ResourcePproxyRepair());
            repairs.Add(new PortProxyRepair(settings));
        }
        repairs.Add(new WireGuardRepair(settings));
        if (mode == AppMode.Travel)
            repairs.Add(new PacServerRepair(_pacServer));
        return repairs;
    }

    private void OnResultsUpdated(IReadOnlyList<CheckResult> results)
    {
        var allOk = results.All(r => r.Ok);
        _tray.Icon = allOk ? _greenIcon : _redIcon;

        var parts = results.Select(r => $"{r.Name}: {(r.Ok ? "OK" : "FAIL")}");
        var hover = string.Join(" | ", parts);
        _tray.Text = hover.Length > 63 ? hover[..63] : hover;

        var ts = DateTime.Now.ToString("HH:mm:ss");
        foreach (var r in results)
            _logForm.AppendLine($"[{ts}] {r.Name}: {(r.Ok ? "OK" : "FAIL")} ({r.Detail})");
    }

    private void OnCheckFailed(CheckResult result)
    {
        _tray.ShowBalloonTip(
            _settings.NotifyTimeoutMs,
            $"{_mode} Monitor - {result.Name} Failed",
            result.Detail,
            ToolTipIcon.Warning);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenu(menu);
        return menu;
    }

    private void RebuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var failing = _monitor.LastResults
            .Where(r => !r.Ok)
            .Select(r => r.Name)
            .ToHashSet();

        var fixItems = _repairs.Where(r => failing.Contains(r.TargetCheckName)).ToList();
        foreach (var repair in fixItems)
        {
            var r = repair;
            var item = new ToolStripMenuItem($"Fix: {r.Label}");
            item.Click += (_, _) => r.Execute();
            menu.Items.Add(item);
        }
        if (fixItems.Count > 0)
            menu.Items.Add(new ToolStripSeparator());

        var showLog = new ToolStripMenuItem("Show Log");
        showLog.Click += (_, _) => { _logForm.Show(); _logForm.BringToFront(); };
        menu.Items.Add(showLog);

        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) =>
        {
            var oldPort = _settings.PacPort;
            var oldDir = _settings.PacDirectory;
            using var form = new SettingsForm(_settings, allowModeChange: false);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _monitor.UpdateInterval(_settings.CheckIntervalSeconds);
                if (_settings.PacPort != oldPort || _settings.PacDirectory != oldDir)
                    _pacServer.Restart();
            }
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);
    }

    private void Shutdown()
    {
        _monitor.Stop();
        _pacServer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Stop();
            _pacServer.Stop();
            _logForm.Dispose();
            _greenIcon.Dispose();
            _redIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon CreateCircleIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(color))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(brush, 2, 2, 28, 28);
        }
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }
}
