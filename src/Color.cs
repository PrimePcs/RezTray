using System;
using System.Runtime.InteropServices;

namespace RezTray
{
    /// <summary>Applies a color profile to one monitor.</summary>
    internal static class ColorEngine
    {
        private const int RampSize = 256;

        /// <summary>Returns null, or what could not be applied.</summary>
        public static string Apply(Monitor monitor, ColorProfile profile)
        {
            bool vibrance = NvApi.SetVibrance(monitor.NvHandle, profile.Vibrance);
            bool ramp = SetRamp(monitor.GdiName, BuildRamp(profile));

            if (!ramp)
                return "Windows blocked these brightness, contrast or gamma values. Try a gentler setting.";
            return vibrance ? null : "The NVIDIA driver rejected the vibrance change.";
        }

        /// <summary>Brightness and contrast are 0-100 with 50 neutral; gamma 100 means 1.00.</summary>
        public static ushort[] BuildRamp(ColorProfile profile)
        {
            double gamma = 1.0 / (profile.Gamma / 100.0);
            double contrast = 0.5 + profile.Contrast / 100.0;
            double brightness = (profile.Brightness - 50) / 200.0;

            var ramp = new ushort[RampSize * 3];
            for (int i = 0; i < RampSize; i++)
            {
                double v = Math.Pow(i / 255.0, gamma);
                v = (v - 0.5) * contrast + 0.5 + brightness;
                ushort value = (ushort)Math.Round(Math.Max(0, Math.Min(1, v)) * 65535);
                ramp[i] = ramp[RampSize + i] = ramp[RampSize * 2 + i] = value;
            }
            return ramp;
        }

        public static ushort[] ReadRamp(string gdiName)
        {
            var ramp = new ushort[RampSize * 3];
            var dc = CreateDCW("DISPLAY", gdiName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            try { return GetDeviceGammaRamp(dc, ramp) ? ramp : null; }
            finally { DeleteDC(dc); }
        }

        private static bool SetRamp(string gdiName, ushort[] ramp)
        {
            var dc = CreateDCW("DISPLAY", gdiName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(dc, ramp); }
            finally { DeleteDC(dc); }
        }

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDCW(string driver, string device, string output, IntPtr init);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr dc, [In] ushort[] ramp);
        [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    }
}
