using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace RezTray
{
    internal struct DisplayMode
    {
        public int Width, Height, Hz;
        public string Size => $"{Width}\u00D7{Height}";
        public override string ToString() => $"{Size} \u00B7 {Hz} Hz";
    }

    /// <summary>A resolution saved in the driver for one monitor.</summary>
    internal sealed class AddedResolution
    {
        public DisplayMode Mode;
        public bool Usable;         // false when Windows does not offer it right now
        public NvCustomDisplay Driver;
        public override string ToString() => Mode.ToString();
    }

    internal sealed class Monitor
    {
        public string GdiName;
        public string Name;
        public string Key;          // stable across reboots, unlike GdiName
        public IntPtr NvHandle;
        public uint DisplayId;
        public bool IsPrimary;
        public bool Sideways;       // rotated 90 or 270 degrees in Windows
        public DisplayMode Current;
        public DisplayMode Native;
        public List<AddedResolution> Added = new List<AddedResolution>();

        // The driver works in panel orientation; Windows swaps width and height when rotated.
        public int PanelWidth => Sideways ? Native.Height : Native.Width;
        public int PanelHeight => Sideways ? Native.Width : Native.Height;

        public bool IsRunning(DisplayMode mode) =>
            Current.Width == mode.Width && Current.Height == mode.Height;

        public override string ToString() => Name;
    }

    /// <summary>Reads monitors, and changes resolution through the normal Windows API.</summary>
    internal static class Monitors
    {
        /// <summary>Every active NVIDIA-driven monitor, primary first.</summary>
        public static List<Monitor> Read()
        {
            var names = MonitorNames.Read();
            var result = new List<Monitor>();

            foreach (var nv in NvApi.GetDisplays())
            {
                var current = Win32.CurrentMode(nv.GdiName);
                if (current == null) continue;
                var dm = current.Value;

                var modes = Win32.Modes(nv.GdiName);
                names.TryGetValue(nv.GdiName, out var id);

                var monitor = new Monitor
                {
                    GdiName = nv.GdiName,
                    Name = string.IsNullOrWhiteSpace(id.Name) ? nv.GdiName.Replace(@"\\.\", "") : id.Name,
                    Key = id.Path ?? nv.GdiName,
                    NvHandle = nv.Handle,
                    DisplayId = nv.DisplayId,
                    IsPrimary = dm.dmPositionX == 0 && dm.dmPositionY == 0,
                    Sideways = dm.dmDisplayOrientation == 1 || dm.dmDisplayOrientation == 3,
                    Current = ToMode(dm),
                };

                var drivers = NvApi.GetCustomDisplays(nv.DisplayId);
                var saved = new HashSet<(int, int)>(drivers.Select(d =>
                    monitor.Sideways ? (d.Height, d.Width) : (d.Width, d.Height)));

                // Native is the largest built-in mode, at its highest refresh rate.
                monitor.Native = modes
                    .Where(m => !saved.Contains((m.Width, m.Height)))
                    .OrderByDescending(m => (long)m.Width * m.Height)
                    .ThenByDescending(m => m.Hz)
                    .DefaultIfEmpty(monitor.Current)
                    .First();

                foreach (var driver in drivers)
                {
                    int width = monitor.Sideways ? driver.Height : driver.Width;
                    int height = monitor.Sideways ? driver.Width : driver.Height;

                    var sameSize = modes.Where(m => m.Width == width && m.Height == height).ToList();
                    int hz = sameSize.Count == 0 || sameSize.Any(m => m.Hz == driver.RefreshHz)
                        ? driver.RefreshHz
                        : sameSize.Max(m => m.Hz);

                    monitor.Added.Add(new AddedResolution
                    {
                        Mode = new DisplayMode { Width = width, Height = height, Hz = hz },
                        Usable = sameSize.Count > 0,
                        Driver = driver,
                    });
                }

                monitor.Added = monitor.Added
                    .OrderByDescending(a => (long)a.Mode.Width * a.Mode.Height)
                    .ToList();
                result.Add(monitor);
            }

            return result.OrderByDescending(m => m.IsPrimary).ToList();
        }

        /// <summary>Asks the driver whether a mode would work, without changing anything.</summary>
        public static bool CanApply(Monitor monitor, DisplayMode mode) =>
            Change(monitor, mode, Win32.CDS_TEST) == Win32.DISP_CHANGE_SUCCESSFUL;

        /// <summary>Switches resolution and saves it. Returns null, or what went wrong.</summary>
        public static string Apply(Monitor monitor, DisplayMode mode)
        {
            int test = Change(monitor, mode, Win32.CDS_TEST);
            if (test != Win32.DISP_CHANGE_SUCCESSFUL) return Describe(test);

            int result = Change(monitor, mode, Win32.CDS_UPDATEREGISTRY);
            return result == Win32.DISP_CHANGE_SUCCESSFUL ? null : Describe(result);
        }

        private static int Change(Monitor monitor, DisplayMode mode, uint flags)
        {
            var current = Win32.CurrentMode(monitor.GdiName);
            if (current == null) return Win32.DISP_CHANGE_FAILED;

            var dm = current.Value;
            dm.dmPelsWidth = mode.Width;
            dm.dmPelsHeight = mode.Height;
            dm.dmDisplayFrequency = mode.Hz;
            dm.dmFields = Win32.DM_PELSWIDTH | Win32.DM_PELSHEIGHT | Win32.DM_DISPLAYFREQUENCY;
            return Win32.ChangeDisplaySettingsExW(monitor.GdiName, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
        }

        private static string Describe(int code)
        {
            switch (code)
            {
                case 1: return "Windows needs a restart to use this resolution.";
                case -2: return "The driver no longer offers this resolution.";
                default: return $"Windows refused the change (code {code}).";
            }
        }

        private static DisplayMode ToMode(Win32.DEVMODE dm) =>
            new DisplayMode { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, Hz = dm.dmDisplayFrequency };

        private static class Win32
        {
            public const uint CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;
            public const int DISP_CHANGE_SUCCESSFUL = 0, DISP_CHANGE_FAILED = -1;
            public const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
            private const int ENUM_CURRENT_SETTINGS = -1;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct DEVMODE
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
                public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
                public int dmFields;
                public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
                public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
                public short dmLogPixels;
                public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
                public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
                public int dmPanningWidth, dmPanningHeight;
            }

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern int ChangeDisplaySettingsExW(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);

            private static DEVMODE NewDevMode() =>
                new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

            public static DEVMODE? CurrentMode(string device)
            {
                var dm = NewDevMode();
                return EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS, ref dm) ? dm : (DEVMODE?)null;
            }

            public static List<DisplayMode> Modes(string device)
            {
                var modes = new HashSet<DisplayMode>();
                var dm = NewDevMode();
                for (int i = 0; EnumDisplaySettingsW(device, i, ref dm); i++)
                    if (dm.dmBitsPerPel == 32) modes.Add(ToMode(dm));
                return modes.ToList();
            }
        }
    }

    /// <summary>Maps \\.\DISPLAYn to the monitor's real name, via the DisplayConfig API.</summary>
    internal static class MonitorNames
    {
        public struct Identity { public string Name, Path; }

        public static Dictionary<string, Identity> Read()
        {
            var result = new Dictionary<string, Identity>(StringComparer.OrdinalIgnoreCase);

            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
                return result;
            var paths = new PATH[pathCount];
            var modes = new byte[modeCount * ModeInfoSize];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return result;

            for (int i = 0; i < pathCount; i++)
            {
                var source = new SOURCE_NAME();
                source.header.type = GET_SOURCE_NAME;
                source.header.size = Marshal.SizeOf(typeof(SOURCE_NAME));
                source.header.adapterId = paths[i].sourceAdapter;
                source.header.id = paths[i].sourceId;
                if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

                var target = new TARGET_NAME();
                target.header.type = GET_TARGET_NAME;
                target.header.size = Marshal.SizeOf(typeof(TARGET_NAME));
                target.header.adapterId = paths[i].targetAdapter;
                target.header.id = paths[i].targetId;
                if (DisplayConfigGetDeviceInfo(ref target) != 0) continue;

                result[source.gdiName] = new Identity
                {
                    Name = target.friendlyName?.Trim().TrimEnd('-'),
                    Path = target.devicePath,
                };
            }
            return result;
        }

        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2;
        private const int ModeInfoSize = 64;   // sizeof(DISPLAYCONFIG_MODE_INFO)

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID   // two 32-bit parts, so it is only 4-byte aligned
        {
            public uint low;
            public int high;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PATH   // DISPLAYCONFIG_PATH_INFO, 72 bytes
        {
            public LUID sourceAdapter;
            public uint sourceId, sourceModeIdx, sourceFlags;
            public LUID targetAdapter;
            public uint targetId, targetModeIdx, outputTechnology, rotation, scaling;
            public uint refreshNumerator, refreshDenominator, scanLineOrdering, targetAvailable, targetFlags;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HEADER
        {
            public int type, size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SOURCE_NAME
        {
            public HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TARGET_NAME
        {
            public HEADER header;
            public uint flags, outputTechnology;
            public ushort edidManufacturer, edidProduct;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PATH[] paths,
            ref uint modeCount, [Out] byte[] modes, IntPtr topology);

        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
    }
}
