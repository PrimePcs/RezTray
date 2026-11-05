using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RezTray
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--dump")
            {
                File.WriteAllText(args[1], Dump(), Encoding.UTF8);
                return;
            }

            using (var mutex = new Mutex(true, @"Local\RezTray", out bool first))
            {
                if (!first) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
#if DEBUG
                if (args.Length == 2 && args[0].StartsWith("--shot-"))
                {
                    Shots.Run(args[0], args[1]);
                    return;
                }
#endif
                Application.Run(new TrayApp());
            }
        }

        /// <summary>Reports what the app sees. Reads only: nothing on the system changes.</summary>
        private static string Dump()
        {
            var report = new StringBuilder();
            report.AppendLine($"nvapi: {NvApi.Initialize()}");

            foreach (var monitor in Monitors.Read())
            {
                report.AppendLine($"{monitor.Name} ({monitor.GdiName}) primary={monitor.IsPrimary} sideways={monitor.Sideways}");
                report.AppendLine($"  current {monitor.Current}, native {monitor.Native}, panel {monitor.PanelWidth}x{monitor.PanelHeight}");
                report.AppendLine($"  vibrance {NvApi.GetVibrance(monitor.NvHandle)}, ramp neutral {RampIsNeutral(monitor)}");
                report.AppendLine($"  native test: {Monitors.CanApply(monitor, monitor.Native)}");

                foreach (var added in monitor.Added)
                    report.AppendLine($"  added {added}: usable={added.Usable} test={Monitors.CanApply(monitor, added.Mode)}");

                var signal = NvApi.CalculateTiming(monitor.DisplayId, monitor.PanelWidth, monitor.PanelHeight, monitor.Native.Hz);
                report.AppendLine($"  signal for new modes: {signal?.Width}x{signal?.Height} {signal?.Hz} Hz reported={signal?.ReportedByMonitor}");
            }
            return report.ToString();
        }

        private static bool RampIsNeutral(Monitor monitor)
        {
            var ramp = ColorEngine.ReadRamp(monitor.GdiName);
            if (ramp == null) return false;
            return ramp.Take(256).Select((v, i) => Math.Abs((v >> 8) - i)).Max() <= 1;
        }
    }

#if DEBUG
    /// <summary>Saves pictures of the windows and menu so the layout can be checked.</summary>
    internal static class Shots
    {
        private static void Render(Control control, string path)
        {
            using (var bitmap = new Bitmap(control.Width, control.Height))
            {
                control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
                bitmap.Save(path);
            }
        }

        public static void Run(string which, string path)
        {
            if (!NvApi.Initialize()) return;

            if (which == "--shot-menu")
            {
                using (var tray = new TrayApp())
                {
                    tray.Menu.AutoClose = false;
                    tray.Menu.Show(new Point(200, 120));
                    Application.DoEvents();
                    Render(tray.Menu, path);

                    var color = tray.Menu.Items.OfType<ToolStripMenuItem>().First(i => i.Text.StartsWith("Color:"));
                    color.DropDown.AutoClose = false;
                    color.ShowDropDown();
                    Application.DoEvents();
                    Render(color.DropDown, path + ".color.png");
                    color.DropDown.Close();
                    tray.Menu.Close();
                    tray.ExitThread();
                }
                return;
            }

            var monitors = Monitors.Read();
            using (Form form = which == "--shot-create"
                ? new ResolutionWindow(monitors[0])
                : (Form)new ProfileEditor(AppSettings.Load(), monitors, livePreview: false))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-32000, -32000);
                form.ShowInTaskbar = false;
                form.Show();
                Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    bitmap.Save(path);
                }
            }
        }
    }
#endif
}
