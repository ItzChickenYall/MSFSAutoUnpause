using System;
using System.Threading;
using System.Windows.Forms;

namespace MSFSAutoUnpause;

internal static class Program
{
    /// <summary>App version from the csproj &lt;Version&gt;, e.g. "1.1.0".</summary>
    public static readonly string Version =
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "dev";

    [STAThread]
    private static void Main()
    {
        // Single-instance guard
        using var mutex = new Mutex(true, "Global\\MSFSAutoUnpause_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "MSFS Auto Unpause is already running.\nCheck the system tray.",
                "MSFS Auto Unpause",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) =>
            Logger.Error($"Unhandled UI thread exception: {e.Exception.Message}\n{e.Exception.StackTrace}");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error($"Unhandled domain exception: {e.ExceptionObject}");

        Application.Run(new MainForm());
    }
}
