using System;
using System.Threading;
using System.Windows.Forms;
using Dds120.UI;
using Dds120.Util;

namespace Dds120
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Report(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);

            // One instance at a time: two programs cannot share the scope's USB interface.
            bool first;
            using (new Mutex(true, "DDS120Scope.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show("DDS-120 Scope is already running.", "DDS-120 Scope", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.Run(new MainForm(SettingsFile.Load()));
            }
        }

        static void Report(Exception ex)
        {
            if (ex == null)
                return;
            MessageBox.Show(ex.ToString(), "DDS-120 Scope - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
