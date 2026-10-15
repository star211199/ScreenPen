using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenPen
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // ---- DPI awareness -------------------------------------------------
            // Set before any window is created so screen metrics are physical
            // pixels and the overlay exactly covers the desktop.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } // PER_MONITOR_AWARE_V2
            catch
            {
                try { Native.SetProcessDPIAware(); }
                catch { }
            }

            bool startMinimized = HasFlag(args, "--tray");
            bool forceSettings = HasFlag(args, "--settings");

            // ---- single instance ----------------------------------------------
            if (!SingleInstance.Acquire())
            {
                // Another copy is already running: ask it to show its settings
                // window instead of silently doing nothing.
                SingleInstance.SignalExistingInstance();
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // A crash must never leave a headless process behind.
            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                ReportCrash(e.ExceptionObject as Exception);

            try
            {
                // The settings file is the single source of truth: reconcile the
                // HKCU Run entry with it on every launch. Without this, editing
                // RunAtStartup in the JSON (or restoring a backup config) would
                // not take effect until the user opened the dialog and saved.
                ReconcileAutoStart();

                Application.Run(new AppContext2(startMinimized && !forceSettings));
            }
            catch (Exception ex)
            {
                ReportCrash(ex);
            }
        }

        /// <summary>
        /// Makes the registry autostart entry match <c>RunAtStartup</c>. Failures
        /// are ignored: a locked-down machine must not stop the app from running.
        /// </summary>
        static void ReconcileAutoStart()
        {
            try
            {
                Settings cfg = SettingsStore.Load();
                bool wanted = cfg.RunAtStartup;
                if (AutoStart.IsEnabled() != wanted) AutoStart.Apply(wanted);
            }
            catch { }
        }

        static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void ReportCrash(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string log = Path.ChangeExtension(Settings.ExePath(), ".error.log");
                File.AppendAllText(log,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    ex.ToString() + Environment.NewLine + Environment.NewLine);
            }
            catch { }

            try
            {
                // The overlay is a full-screen TopMost form, so an owner-less
                // message box would be shown *behind* it and the app would look
                // frozen. Drop the topmost flag and hide those forms first, each
                // in its own try/catch so one failure cannot stop the rest.
                foreach (Form f in Application.OpenForms)
                {
                    try
                    {
                        if (f.TopMost)
                        {
                            f.TopMost = false;
                            f.Hide();
                        }
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                MessageBox.Show(
                    "ScreenPen 遇到错误：\n\n" + ex.Message +
                    "\n\n详细信息已写入 EXE 同目录的 .error.log。",
                    "ScreenPen", MessageBoxButtons.OK, MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
            }
            catch { }
        }
    }
}
