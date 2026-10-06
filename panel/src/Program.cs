using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using DevExpress.XtraEditors;

namespace Valhalla
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                ConfigureDependencyResolution();
                ConfigureGlobalErrorLogging();
                ValhallaStorage.EnsureDirectories();

                // Keep WinForms and DevExpress on the same Per-Monitor-V2 DPI policy.
                // This must be established before any DevExpress control/skin is created.
                WindowsFormsSettings.SetPerMonitorDpiAware();
                WindowsFormsSettings.ScrollUIMode = ScrollUIMode.Desktop;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
            }
            catch (Exception ex)
            {
                ShowFatalStartupMessage(ex);
                Environment.ExitCode = 1;
            }
        }

        private static string BaseDirectory => AppDomain.CurrentDomain.BaseDirectory;
        private static string StartupLogPath => Path.Combine(BaseDirectory, "startup-error.log");

        private static void ConfigureDependencyResolution()
        {
            // All managed runtime assemblies are deployed beside the executable.
            // The CLR probes the application base directory by default, so no custom
            // subdirectory probing or native DLL directory override is required.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveExternalAssembly;
        }

        private static Assembly ResolveExternalAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requested = new AssemblyName(args.Name);
                string name = requested.Name;
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                string candidate = Path.Combine(BaseDirectory, name + ".dll");
                if (!File.Exists(candidate))
                    return null;

                return Assembly.LoadFrom(candidate);
            }
            catch (Exception ex)
            {
                WriteStartupError("Assembly resolution failure for " + args.Name, ex);
                return null;
            }
        }

        private static void ConfigureGlobalErrorLogging()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, args) =>
                HandleGlobalException("Windows Forms thread exception", args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                HandleGlobalException("Unhandled application-domain exception", args.ExceptionObject as Exception);
        }

        private static void HandleGlobalException(string context, Exception exception)
        {
            WriteStartupError(context, exception);

            try
            {
                MessageBox.Show(
                    "Valhalla could not continue.\r\n\r\n" +
                    (exception == null ? context : exception.Message) +
                    "\r\n\r\nA diagnostic log was written to:\r\n" + StartupLogPath,
                    "Valhalla - Startup Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // Never allow diagnostic UI handling to throw another exception.
            }
        }

        private static void WriteStartupError(string context, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string details = exception == null ? string.Empty : exception.ToString();
                using (StreamWriter writer = new StreamWriter(StartupLogPath, true))
                {
                    writer.WriteLine("============================================================");
                    writer.WriteLine(stamp);
                    writer.WriteLine(context);
                    writer.WriteLine("Application base: " + BaseDirectory);
                    writer.WriteLine("Runtime: " + Environment.Version);
                    if (!string.IsNullOrEmpty(details))
                        writer.WriteLine(details);
                    writer.WriteLine();
                }
                string errorLogPath = Path.Combine(BaseDirectory, "error.log");
                using (StreamWriter writer = new StreamWriter(errorLogPath, true))
                {
                    writer.WriteLine("[" + stamp + "] " + context +
                        (string.IsNullOrEmpty(details) ? string.Empty : ": " + details));
                }
            }
            catch
            {
                // Startup diagnostics must never mask the original exception.
            }
        }

        private static void ShowFatalStartupMessage(Exception exception)
        {
            HandleGlobalException("Fatal startup exception", exception);
        }
    }
}
