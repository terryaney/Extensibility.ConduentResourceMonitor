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
            .WithParsed(options =>
            {
                var settings = AppSettings.Load(options.Mode);
                settings.ApplyOverrides(options);
                Application.Run(new TrayApp(options.Mode, settings, options.ShowLog));
            })
            .WithNotParsed(errors =>
            {
                var messages = errors
                    .Where(e => e is not HelpRequestedError and not VersionRequestedError)
                    .Select(e => e.ToString());
                if (messages.Any())
                    MessageBox.Show(
                        $"Invalid arguments:\n{string.Join("\n", messages)}\n\nUsage: ConduentResourceMonitor.exe --mode Hub|Travel",
                        "ResourceMonitor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                Environment.Exit(1);
            });
    }
}
