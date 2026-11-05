using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

#if DEBUG
            const string InstanceName = @"Local\RezTrayDebug";   // runs beside the installed copy
#else
            const string InstanceName = @"Local\RezTray";
#endif
            using (var mutex = new Mutex(true, InstanceName, out bool first))
            {
                if (!first) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
#if DEBUG
                if (args.Length == 2 && args[0] == "--selftest")
                {
                    File.WriteAllText(args[1], SelfTest.Run(), Encoding.UTF8);
                    return;
                }
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
    /// <summary>Checks the app's logic. Nothing here changes a display, or the real settings file.</summary>
    internal static class SelfTest
    {
        private static readonly StringBuilder Log = new StringBuilder();
        private static int _passed, _failed;

        private static void Check(string name, bool ok)
        {
            if (ok) _passed++; else _failed++;
            Log.AppendLine($"{(ok ? "pass" : "FAIL")}  {name}");
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); return false; }
            catch (T) { return true; }
        }

        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

        public static string Run()
        {
            Check("nvapi", NvApi.Initialize());
            TestKeys();
            TestHotkeyDelivery();
            TestSettingsFiles();
            TestSharing();
            TestUpdates();
            TestMonitors();
            Log.AppendLine($"{_passed} passed, {_failed} failed");
            return Log.ToString();
        }

        private static void TestKeys()
        {
            int ctrlAlt = Hotkey.Ctrl | Hotkey.Alt;
            Check("format Ctrl+Alt+1", Hotkey.Format(ctrlAlt, Keys.D1) == "Ctrl+Alt+1");
            Check("format Ctrl+Shift+F5", Hotkey.Format(Hotkey.Ctrl | Hotkey.Shift, Keys.F5) == "Ctrl+Shift+F5");
            Check("allow Ctrl+Alt+1", Hotkey.IsAllowed(ctrlAlt, Keys.D1));
            Check("allow F9 alone", Hotkey.IsAllowed(0, Keys.F9));
            Check("refuse plain letter", !Hotkey.IsAllowed(0, Keys.A));
            Check("refuse Shift+letter", !Hotkey.IsAllowed(Hotkey.Shift, Keys.A));
            Check("refuse a modifier alone", !Hotkey.IsAllowed(Hotkey.Ctrl, Keys.ControlKey));
        }

        private static void PressTestCombo()
        {
            const byte VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_F24 = 0x87;
            const uint KEYUP = 2;
            foreach (var key in new[] { VK_CONTROL, VK_MENU, VK_SHIFT, VK_F24 }) keybd_event(key, 0, 0, IntPtr.Zero);
            foreach (var key in new[] { VK_F24, VK_SHIFT, VK_MENU, VK_CONTROL }) keybd_event(key, 0, KEYUP, IntPtr.Zero);
        }

        /// <summary>A real key press through Windows, on a combo that nothing else uses.</summary>
        private static void TestHotkeyDelivery()
        {
            using (var manager = new HotkeyManager())
            {
                var binding = new HotkeyBinding { Modifiers = Hotkey.Ctrl | Hotkey.Alt | Hotkey.Shift, Key = (int)Keys.F24, Label = "test" };
                int fired = 0;
                manager.Pressed += b => { if (b == binding) fired++; };

                Check("hotkey registers", manager.Register(new[] { binding }).Count == 0);
                var bad = new HotkeyBinding { Modifiers = 0, Key = (int)Keys.A };
                Check("plain key is reported as failed", manager.Register(new[] { bad }).Count == 1);
                manager.Register(new[] { binding });

                PressTestCombo();
                for (int i = 0; i < 40 && fired == 0; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Check("hotkey fires from a real key press", fired == 1);

                manager.Clear();
                PressTestCombo();
                for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(10); }
                Check("hotkey stops after clearing", fired == 1);
            }
        }

        private static AppSettings RoundTrip(AppSettings settings)
        {
            var stream = new MemoryStream();
            settings.Write(stream);
            return AppSettings.Normalized(AppSettings.Read(new MemoryStream(stream.ToArray())));
        }

        private static void TestSettingsFiles()
        {
            const string fromVersion1 = "{\"Active\":{\"path\":\"abc\"},\"Profiles\":[{\"Brightness\":50,\"Contrast\":50,\"Gamma\":100,\"Id\":\"default\",\"Name\":\"Default\",\"Vibrance\":50},{\"Brightness\":50,\"Contrast\":50,\"Gamma\":100,\"Id\":\"abc\",\"Name\":\"Mine\",\"Vibrance\":70}]}";
            var old = AppSettings.Normalized(AppSettings.Read(new MemoryStream(Encoding.UTF8.GetBytes(fromVersion1))));
            Check("1.0 settings keep profiles", old.Profiles.Count == 2 && old.Profiles[1].Name == "Mine");
            Check("1.0 settings keep active color", old.Active["path"] == "abc");
            Check("1.0 settings gain empty lists", old.Hotkeys.Count == 0 && old.Setups.Count == 0);
            Check("1.0 settings leave update alerts on", !old.UpdateAlertsOff);

            var settings = AppSettings.Normalized(null);
            settings.Hotkeys.Add(new HotkeyBinding { Modifiers = 3, Key = (int)Keys.D1, Action = HotkeyBinding.Native, MonitorKey = "m", Label = "x" });
            settings.Setups.Add(new Setup
            {
                Name = "Gaming",
                Monitors = { new SetupMonitor { MonitorKey = "m", MonitorName = "VG27A", Width = 1720, Height = 1080, Hz = 165, ProfileId = "p" } },
            });
            var back = RoundTrip(settings);
            Check("hotkeys survive saving", back.Hotkeys.Count == 1 && back.Hotkeys[0].Keys == "Ctrl+Alt+1");
            Check("setups survive saving", back.Setups[0].Monitors[0].Width == 1720 && back.Setups[0].Name == "Gaming");
        }

        private static void TestSharing()
        {
            string folder = Path.Combine(Path.GetTempPath(), "RezTrayTest" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var monitors = Monitors.Read();
                var mine = AppSettings.Normalized(null);
                string file = Path.Combine(folder, "a.reztray");
                Sharing.Export(file, mine, monitors);

                string text = File.ReadAllText(file);
                Check("export leaves out monitor ids", !text.Contains("DISPLAY") && !text.Contains("Key") && !text.Contains("DEV_"));
                Check("export leaves out Default", !text.Contains("\"default\""));

                var friend = AppSettings.Normalized(null);
                var same = Sharing.Import(file, friend);
                Check("same profiles are not doubled", same.ProfilesAdded == 0 && friend.Profiles.Count == mine.Profiles.Count);
                Check("resolutions come through", same.Resolutions.Count == monitors.SelectMany(m => m.Added).Select(a => a.Mode).Distinct().Count());

                friend.Profiles.First(p => p.Name == "Vibrant").Vibrance = 10;
                Sharing.Import(file, friend);
                Check("different colors keep both", friend.Profiles.Any(p => p.Name == "Vibrant (imported)") && friend.Profiles.First(p => p.Name == "Vibrant").Vibrance == 10);

                string hostile = Path.Combine(folder, "b.reztray");
                string longName = new string('x', 500);
                File.WriteAllText(hostile, "{\"App\":\"RezTray\",\"Format\":1,\"Profiles\":[{\"Id\":\"default\",\"Name\":\"Bad\\n" + longName + "\",\"Vibrance\":9999,\"Brightness\":-5,\"Contrast\":50,\"Gamma\":99999}],\"Resolutions\":[{\"Width\":-1,\"Height\":10,\"Hz\":60},{\"Width\":1600,\"Height\":900,\"Hz\":999}]}");
                var clean = AppSettings.Normalized(null);
                var result = Sharing.Import(hostile, clean);
                var bad = clean.Profiles.Last();
                Check("bad numbers are clamped", bad.Vibrance == 100 && bad.Brightness == 0 && bad.Gamma == 280);
                Check("bad names are cleaned", bad.Name.Length <= 40 && !bad.Name.Contains("\n"));
                Check("imported file cannot take the Default id", bad.Id != ColorProfile.DefaultId && clean.Profiles.Count(p => p.IsDefault) == 1);
                Check("bad resolutions are dropped", result.Resolutions.Count == 1);
                Check("import activates nothing", clean.Active.Count == 0);

                string garbage = Path.Combine(folder, "c.reztray");
                File.WriteAllText(garbage, "not json at all");
                Check("garbage is refused", Throws<InvalidDataException>(() => Sharing.Import(garbage, AppSettings.Normalized(null))));

                string other = Path.Combine(folder, "d.reztray");
                File.WriteAllText(other, "{\"something\":\"else\"}");
                Check("other json is refused", Throws<InvalidDataException>(() => Sharing.Import(other, AppSettings.Normalized(null))));

                string newer = Path.Combine(folder, "e.reztray");
                File.WriteAllText(newer, "{\"App\":\"RezTray\",\"Format\":2}");
                Check("newer format is refused", Throws<InvalidDataException>(() => Sharing.Import(newer, AppSettings.Normalized(null))));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        private static void TestUpdates()
        {
            var current = new Version(1, 1, 0, 0);
            Check("newer tag", Updates.IsNewer("v1.2.0", current));
            Check("newer patch", Updates.IsNewer("1.1.1", current));
            Check("same tag is not newer", !Updates.IsNewer("v1.1.0", current));
            Check("older tag is not newer", !Updates.IsNewer("v1.0.0", current));
            Check("10 beats 9", Updates.IsNewer("v1.10.0", new Version(1, 9, 0, 0)));
            Check("bad tag is not newer", !Updates.IsNewer("latest", current) && !Updates.IsNewer(null, current));
            Check("app reports 1.1.0", Updates.Current.ToString(3) == "1.1.0");

            string tag = Updates.LatestTagAsync().GetAwaiter().GetResult();
            Log.AppendLine($"      GitHub says the latest is {tag ?? "(unreachable)"}");
            Check("GitHub answers", tag != null);
        }

        private static void TestMonitors()
        {
            var monitors = Monitors.Read();
            var settings = AppSettings.Normalized(null);
            Check("two monitors", monitors.Count == 2);

            foreach (var monitor in monitors)
            {
                Check($"{monitor.Name}: current rate listed", monitor.Rates.Contains(monitor.Current.Hz));
                Log.AppendLine($"      {monitor.Name}: {monitor.Current}, rates {string.Join(" ", monitor.Rates)}");
                foreach (int hz in monitor.Rates)
                    Check($"{monitor.Name}: driver accepts {hz} Hz", Monitors.CanApply(monitor,
                        new DisplayMode { Width = monitor.Current.Width, Height = monitor.Current.Height, Hz = hz }));
            }

            var setup = Setup.Capture("Test", monitors, settings);
            Check("setup covers every monitor", setup.Monitors.Count == monitors.Count);
            Check("setup modes are accepted", setup.Monitors.All(e => Monitors.CanApply(monitors.First(m => m.Key == e.MonitorKey), e.Mode)));

            var actions = HotkeyActions.All(monitors, settings);
            Check("hotkey actions list native", actions.Count(a => a.Action == HotkeyBinding.Native) == monitors.Count);
            Check("hotkey actions list colors", actions.Count(a => a.Action == HotkeyBinding.Color) == monitors.Count * settings.Profiles.Count);
        }
    }
#endif

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

                    foreach (var prefix in new[] { "Refresh:", "Setups", "More" })
                    {
                        var parent = tray.Menu.Items.OfType<ToolStripMenuItem>().First(i => i.Text.StartsWith(prefix));
                        parent.DropDown.AutoClose = false;
                        parent.ShowDropDown();
                        Application.DoEvents();
                        Render(parent.DropDown, path + "." + prefix.TrimEnd(':') + ".png");
                        parent.DropDown.Close();
                    }
                    tray.Menu.Close();
                    tray.ExitThread();
                }
                return;
            }

            var monitors = Monitors.Read();
            var sample = AppSettings.Normalized(null);
            sample.Setups.Add(Setup.Capture("Gaming", monitors, sample));
            sample.Hotkeys.Add(HotkeyActions.Native(monitors[0]).WithKeys(Hotkey.Ctrl | Hotkey.Alt, Keys.D0));

            Form Build()
            {
                switch (which)
                {
                    case "--shot-create":
                        var create = new ResolutionWindow(monitors[0]);
                        create.ShowImported(new[] { new DisplayMode { Width = 1920, Height = 1080, Hz = 240 } }.ToList());
                        return create;
                    case "--shot-hotkeys":
                        return new HotkeyWindow(sample, monitors, _ => { }, () => new System.Collections.Generic.List<HotkeyBinding>());
                    case "--shot-setups":
                        return new SetupWindow(sample, () => monitors, _ => { });
                    default:
                        return new ProfileEditor(AppSettings.Load(), monitors, livePreview: false);
                }
            }

            using (var form = Build())
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
