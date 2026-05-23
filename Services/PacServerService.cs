using System.Diagnostics;

namespace ConduentResourceMonitor.Services;

public class PacServerService
{
    private Process? _process;
    private readonly AppSettings _settings;

    public PacServerService(AppSettings settings)
    {
        _settings = settings;
    }

    public void Start()
    {
        try
        {
            _process = Process.Start(new ProcessStartInfo("python", $"-m http.server {_settings.PacPort}")
            {
                WorkingDirectory = _settings.PacDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
        }
        catch { /* PacServerCheck will surface the failure */ }
    }

    public void Stop()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process = null;
    }

    public void Restart()
    {
        Stop();
        Start();
    }
}
