using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RezTray
{
    /// <summary>A display the NVIDIA driver is driving.</summary>
    internal sealed class NvDisplay
    {
        public string GdiName;      // e.g. \\.\DISPLAY1
        public IntPtr Handle;
        public uint DisplayId;
    }

    /// <summary>A resolution saved in the driver, as the driver stores it.</summary>
    internal sealed class NvCustomDisplay
    {
        public int Width, Height, RefreshHz;
        public byte[] Raw;          // the driver's own record, needed to delete it
    }

    /// <summary>A calculated signal for the monitor, ready to go into a custom display.</summary>
    internal sealed class NvTiming
    {
        public const int RefreshOffset = 36;   // NV_TIMING.etc.rr

        public byte[] Raw;
        public bool ReportedByMonitor;   // false means it is beyond what the monitor advertises
        public int Hz => BitConverter.ToUInt16(Raw, RefreshOffset);
        public int Width => BitConverter.ToUInt16(Raw, 0);
        public int Height => BitConverter.ToUInt16(Raw, 12);
    }

    /// <summary>
    /// Wrapper over nvapi64.dll, which ships with the driver. Functions are looked up by NVAPI
    /// interface id, and structures are read as raw bytes at the offsets in NVIDIA's nvapi.h.
    /// </summary>
    internal static class NvApi
    {
        private const int Ok = 0;

        private const int CustomDisplaySize = 144;                 // sizeof(NV_CUSTOM_DISPLAY)
        private const uint CustomDisplayVersion = CustomDisplaySize | (1u << 16);
        private const int CustomTimingOffset = 44;                 // NV_CUSTOM_DISPLAY.timing
        private const int TimingSize = 96;                         // sizeof(NV_TIMING)
        private const int TimingInputSize = 32;                    // sizeof(NV_TIMING_INPUT)
        private const uint TimingInputVersion = TimingInputSize | (1u << 16);
        private const int DvcInfoExSize = 20;                      // sizeof(NV_DISPLAY_DVC_INFO_EX)
        private const int DvcInfoExVersion = DvcInfoExSize | (1 << 16);
        private const uint FormatA8R8G8B8 = 21;                    // what the driver reports for saved modes

        // NV_TIMING_OVERRIDE values.
        private const uint TimingCurrent = 0, TimingAuto = 1, TimingEdid = 2, TimingCvtRb = 6;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumHandleFn(uint index, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DisplayNameFn(IntPtr handle, [Out] byte[] name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DisplayIdFn([In] byte[] name, out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumCustomFn(uint id, uint index, [In, Out] byte[] custom);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTimingFn(uint id, [In] byte[] input, [Out] byte[] timing);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TryCustomFn([In] uint[] ids, uint count, [In] byte[] custom);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteCustomFn([In] uint[] ids, uint count, [In] byte[] custom);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SaveCustomFn([In] uint[] ids, uint count, uint outputOnly, uint monitorOnly);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RevertCustomFn([In] uint[] ids, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DvcInfoExFn(IntPtr handle, uint outputId, [In, Out] int[] info);

        private static EnumHandleFn _enumHandle;
        private static DisplayNameFn _displayName;
        private static DisplayIdFn _displayId;
        private static EnumCustomFn _enumCustom;
        private static GetTimingFn _getTiming;
        private static TryCustomFn _tryCustom;
        private static DeleteCustomFn _deleteCustom;
        private static SaveCustomFn _saveCustom;
        private static RevertCustomFn _revertCustom;
        private static DvcInfoExFn _getDvc, _setDvc;

        public static bool Available { get; private set; }

        public static bool Initialize()
        {
            try
            {
                if (Load<InitializeFn>(0x0150E828)() != Ok) return false;
                _enumHandle = Load<EnumHandleFn>(0x9ABDD40D);
                _displayName = Load<DisplayNameFn>(0x22A78B05);
                _displayId = Load<DisplayIdFn>(0xAE457190);
                _enumCustom = Load<EnumCustomFn>(0xA2072D59);
                _getTiming = Load<GetTimingFn>(0x175167E9);
                _tryCustom = Load<TryCustomFn>(0x1F7DB630);
                _deleteCustom = Load<DeleteCustomFn>(0x552E5B9B);
                _saveCustom = Load<SaveCustomFn>(0x49882876);
                _revertCustom = Load<RevertCustomFn>(0xCBBD40F0);
                _getDvc = Load<DvcInfoExFn>(0x0E45002D);
                _setDvc = Load<DvcInfoExFn>(0x4A82C2B1);
                return Available = true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (BadImageFormatException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static T Load<T>(uint id)
        {
            var pointer = QueryInterface(id);
            if (pointer == IntPtr.Zero)
                throw new InvalidOperationException($"NVAPI function 0x{id:X8} is missing");
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        public static List<NvDisplay> GetDisplays()
        {
            var displays = new List<NvDisplay>();
            if (!Available) return displays;

            for (uint i = 0; i < 16 && _enumHandle(i, out var handle) == Ok; i++)
            {
                var name = new byte[64];
                if (_displayName(handle, name) != Ok) continue;
                if (_displayId(name, out var id) != Ok) continue;

                int end = Array.IndexOf(name, (byte)0);
                displays.Add(new NvDisplay
                {
                    GdiName = Encoding.ASCII.GetString(name, 0, end < 0 ? name.Length : end),
                    Handle = handle,
                    DisplayId = id,
                });
            }
            return displays;
        }

        public static List<NvCustomDisplay> GetCustomDisplays(uint displayId)
        {
            var result = new List<NvCustomDisplay>();
            for (uint i = 0; i < 64; i++)
            {
                var raw = new byte[CustomDisplaySize];
                BitConverter.GetBytes(CustomDisplayVersion).CopyTo(raw, 0);
                if (_enumCustom(displayId, i, raw) != Ok) break;   // normally "end of list"

                result.Add(new NvCustomDisplay
                {
                    Width = BitConverter.ToInt32(raw, 4),
                    Height = BitConverter.ToInt32(raw, 8),
                    RefreshHz = BitConverter.ToUInt16(raw, CustomTimingOffset + NvTiming.RefreshOffset),
                    Raw = raw,
                });
            }
            return result;
        }

        /// <summary>
        /// Works out the signal for a refresh rate. The size is always the panel's own, so
        /// smaller resolutions are scaled by the GPU instead of changing the monitor's mode.
        /// </summary>
        public static NvTiming CalculateTiming(uint displayId, int panelWidth, int panelHeight, int hz)
        {
            foreach (var type in new[] { TimingCurrent, TimingAuto, TimingEdid })
            {
                var timing = GetTiming(displayId, panelWidth, panelHeight, hz, type);
                if (timing != null && timing.Hz == hz && timing.Width == panelWidth && timing.Height == panelHeight)
                {
                    timing.ReportedByMonitor = true;
                    return timing;
                }
            }
            return GetTiming(displayId, panelWidth, panelHeight, hz, TimingCvtRb);
        }

        private static NvTiming GetTiming(uint displayId, int width, int height, int hz, uint type)
        {
            var input = new byte[TimingInputSize];
            BitConverter.GetBytes(TimingInputVersion).CopyTo(input, 0);
            BitConverter.GetBytes(width).CopyTo(input, 4);
            BitConverter.GetBytes(height).CopyTo(input, 8);
            BitConverter.GetBytes((float)hz).CopyTo(input, 12);
            BitConverter.GetBytes(type).CopyTo(input, 28);

            var raw = new byte[TimingSize];
            return _getTiming(displayId, input, raw) == Ok ? new NvTiming { Raw = raw } : null;
        }

        /// <summary>Shows a mode on the monitor without saving it. Follow with Save or Revert.</summary>
        public static int TryCustomDisplay(uint displayId, int width, int height, NvTiming timing) =>
            _tryCustom(new[] { displayId }, 1, BuildCustomDisplay(width, height, timing));

        /// <summary>Saves the mode being tried, for this monitor only.</summary>
        public static int SaveCustomDisplay(uint displayId) =>
            _saveCustom(new[] { displayId }, 1, 1, 1);

        public static int RevertCustomDisplay(uint displayId) =>
            _revertCustom(new[] { displayId }, 1);

        public static int DeleteCustomDisplay(uint displayId, byte[] rawCustomDisplay) =>
            _deleteCustom(new[] { displayId }, 1, rawCustomDisplay);

        private static byte[] BuildCustomDisplay(int width, int height, NvTiming timing)
        {
            var raw = new byte[CustomDisplaySize];
            BitConverter.GetBytes(CustomDisplayVersion).CopyTo(raw, 0);
            BitConverter.GetBytes(width).CopyTo(raw, 4);
            BitConverter.GetBytes(height).CopyTo(raw, 8);
            BitConverter.GetBytes(32).CopyTo(raw, 12);                 // depth
            BitConverter.GetBytes(FormatA8R8G8B8).CopyTo(raw, 16);
            BitConverter.GetBytes(1f).CopyTo(raw, 28);                 // srcPartition w, h: nvapi.h says
            BitConverter.GetBytes(1f).CopyTo(raw, 32);                 // use (0, 0, 1, 1)
            BitConverter.GetBytes(1f).CopyTo(raw, 36);                 // xRatio
            BitConverter.GetBytes(1f).CopyTo(raw, 40);                 // yRatio
            timing.Raw.CopyTo(raw, CustomTimingOffset);
            return raw;
        }

        /// <summary>Digital vibrance as shown in NVIDIA Control Panel: 0-100, 50 is off.</summary>
        public static int? GetVibrance(IntPtr handle)
        {
            var info = new int[DvcInfoExSize / 4];
            info[0] = DvcInfoExVersion;
            return _getDvc(handle, 0, info) == Ok ? info[1] : (int?)null;
        }

        public static bool SetVibrance(IntPtr handle, int level)
        {
            var info = new int[DvcInfoExSize / 4];
            info[0] = DvcInfoExVersion;
            info[1] = level;
            return _setDvc(handle, 0, info) == Ok;
        }

        public static string Describe(int status)
        {
            switch (status)
            {
                case -1: return "the driver reported a general error";
                case -6: return "the driver does not support this";
                case -104: return "the driver rejected this resolution";
                default: return $"NVIDIA error {status}";
            }
        }
    }
}
