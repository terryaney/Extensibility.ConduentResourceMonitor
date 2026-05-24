using CommandLine;
using ConduentResourceMonitor;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Parser.Default.ParseArguments<Options>(args)
            .WithParsed(Run)
            .WithNotParsed(errors =>
            {
                var fatal = errors
                    .Where(e => e is not HelpRequestedError and not VersionRequestedError)
                    .ToList();
                if (fatal.Count > 0)
                    ShowError($"Invalid arguments:\n{string.Join("\n", fatal)}\n\nUsage: ConduentResourceMonitor.exe [--mode Hub|Travel] [options]");
                Environment.Exit(1);
            });
    }

    static void Run(Options options)
    {
        var settings = AppSettings.Load();
        settings.ApplyOverrides(options); // --mode overrides saved mode; --repair-on-start never touches settings

        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            // Give the user a chance to fix settings (including picking a mode) before failing
            using var form = new SettingsForm(settings, allowModeChange: true, validationErrors: errors);
            form.ShowDialog();

            errors = settings.Validate();
            if (errors.Count > 0)
            {
                ShowError(
                    $"Cannot start monitor — configuration problems remain:\n\n" +
                    string.Join("\n", errors.Select(e => $"  • {e}")));
                Environment.Exit(1);
                return;
            }
        }

        Application.Run(new TrayApp(settings, options.ShowLog, options.RepairOnStart));
    }

    static void ShowError(string message) =>
        MessageBox.Show(message, "Conduent Resource Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
