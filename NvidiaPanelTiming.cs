using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

internal static class NvidiaPanelTiming
{
    private const uint InfoTypeDdsStatus = 0x17;

    private static IEnumerable<string> CandidateNvCplPaths()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] searchPaths =
        {
            Path.Combine(programFiles, "NVIDIA Corporation", "NVIDIA App", "NvCpl", "nvcpl.dll"),
            Path.Combine(programFiles, "NVIDIA Corporation", "Control Panel Client", "nvcpl.dll"),
            Path.Combine(Environment.SystemDirectory, "nvcpl.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "Control Panel Client", "nvcpl.dll")
        };

        foreach (string path in searchPaths)
        {
            if (File.Exists(path))
                yield return path;
        }
    }

    private static string? ResolveNvCplPath()
    {
        return CandidateNvCplPaths().FirstOrDefault();
    }

    /// <summary>
    /// Queries NVIDIA Advanced Optimus (DDS) live MUX state via nvcpl.dll (matching PreySense's NvidiaDdsService).
    /// Returns true if DDS reports a valid active MUX (MuxState 1 = Optimus/Intel, 2 = dGPU/NVIDIA).
    /// </summary>
    public static bool TryGetDdsActiveMux(out bool isNvidia)
    {
        isNvidia = false;
        foreach (string dllPath in CandidateNvCplPaths())
        {
            nint lib = Native.LoadLibrary(dllPath);
            if (lib == 0)
                continue;

            try
            {
                var init = TryBind<InitFn>(lib, "NvCplApiInit");
                var close = TryBind<CloseFn>(lib, "NvCplApiClose");
                var getInfo = TryBind<GetInfoFn>(lib, "NvCplApiGetInfo");
                if (init == null || getInfo == null)
                    continue;

                if (init() != 0)
                    continue;

                try
                {
                    ulong size = 16;
                    nint pStatus = Marshal.AllocHGlobal(16);
                    try
                    {
                        for (int i = 0; i < 16; i++)
                            Marshal.WriteByte(pStatus, i, 0);

                        if (getInfo(InfoTypeDdsStatus, 0, pStatus, ref size) != 0)
                            continue;

                        var info = Marshal.PtrToStructure<NativeDdsStatusInfo>(pStatus);
                        if (info.IsSupported == 0 || info.KmdResponse != 0)
                            continue;

                        if (info.MuxState == 2)
                        {
                            isNvidia = true;
                            return true;
                        }
                        if (info.MuxState == 1)
                        {
                            isNvidia = false;
                            return true;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pStatus);
                    }
                }
                finally
                {
                    try { close?.Invoke(); } catch { }
                }
            }
            finally
            {
                Native.FreeLibrary(lib);
            }
        }
        return false;
    }

    private static Address ResolveAddress(TestFn test, ref Timing sampleTiming)
    {
        for (uint g = 0; g < 2; g++)
        {
            for (uint d = 0; d < 4; d++)
            {
                var candidate = new Address { Gpu = g, Display = d };
                int rc = test(ref candidate, 1, ref sampleTiming);
                // Return code 0 (success) or 1 (already active / standard mode) indicates a valid display address
                if (rc == 0 || rc == 1)
                {
                    int saveRc = test(ref candidate, 2, ref sampleTiming);
                    if (saveRc == 0)
                        return candidate;
                }
            }
        }
        return new Address { Gpu = 0, Display = 0 };
    }

    public static int Apply(IReadOnlyList<int> rates, IReadOnlyList<byte[]> descriptors, string? deviceName = null)
    {
        if (rates.Count != descriptors.Count || rates.Count == 0)
        {
            ProbeLog.Line("No NVIDIA timings to send.");
            return 1;
        }

        string? loadedPath = null;
        nint lib = 0;
        foreach (string candidate in CandidateNvCplPaths())
        {
            lib = Native.LoadLibrary(candidate);
            if (lib != 0)
            {
                loadedPath = candidate;
                break;
            }
        }

        if (lib == 0 || loadedPath == null)
        {
            ProbeLog.Line("NVIDIA control panel library did not load. Nothing was written.");
            return 1;
        }

        ProbeLog.Line("Using NVIDIA control panel library: " + loadedPath);

        string? targetDevice = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;

        try
        {
            var init = Bind<InitFn>(lib, "NvCplApiInit");
            var close = TryBind<CloseFn>(lib, "NvCplApiClose");
            var test = Bind<TestFn>(lib, "NvCplApiCTTest");
            var clear = Bind<AddressFn>(lib, "NvCplApiClearAllUnsupportedResolutionsFromCacheForDisplay");
            var flush = Bind<AddressFn>(lib, "NvCplApiFlushUnsupportedResolutionCacheToStorage");

            int rc = init();
            ProbeLog.Line("NvCplApiInit " + rc);
            if (rc != 0)
                return 1;

            try
            {
                Desktop before = CurrentMode(targetDevice);
                ProbeLog.Line("Desktop before NVIDIA timings " + before.Width + "x" + before.Height + " @ " + before.Hz + " Hz, signal " + CcdSignal.DescribeAllTargets());

                Timing sample = FromDescriptor(rates[0], descriptors[0]);
                Address address = ResolveAddress(test, ref sample);
                ProbeLog.Line($"Using NVIDIA display address: GPU {address.Gpu}, Display {address.Display}");

                int failures = 0;
                int saved = 0;
                for (int i = 0; i < rates.Count; i++)
                {
                    Timing timing = FromDescriptor(rates[i], descriptors[i]);
                    int tested = test(ref address, 1, ref timing);
                    string signal = CcdSignal.DescribeAllTargets();

                    // Modern NVIDIA drivers use op = 2 (SAVE) to persist the custom timing to storage.
                    // Op = 6 (0x04 | 0x02) causes a C++ exception in modern nvcpl.dll because bit 4
                    // invokes an unsupported sub-operation. We test op = 2 first, falling back to op = 6
                    // only if op = 2 is rejected.
                    int persisted = test(ref address, 2, ref timing);
                    if (persisted != 0)
                    {
                        persisted = test(ref address, 6, ref timing);
                    }

                    Desktop now = CurrentMode(targetDevice);
                    ProbeLog.Line(rates[i] + " Hz  test " + tested + ", save " + persisted
                        + ", clock " + timing.PixelClock.ToString("0.000") + " MHz, total "
                        + timing.HTotal + "x" + timing.VTotal + ", signal " + signal
                        + ", desktop " + now.Width + "x" + now.Height + " @ " + now.Hz);

                    // A timing is successfully accepted if it was persisted to storage (persisted == 0)
                    // or tested successfully (tested == 0).
                    if (persisted == 0 || tested == 0)
                        saved++;
                    else
                        failures++;

                    if (now.Hz != before.Hz || now.Width != before.Width || now.Height != before.Height)
                    {
                        int restored = Restore(targetDevice, before);
                        Desktop after = CurrentMode(targetDevice);
                        ProbeLog.Line("Restored desktop " + restored + ", now " + after.Width + "x" + after.Height + " @ " + after.Hz);
                    }
                }

                int targetHz = before.Hz > 0 ? before.Hz : 240;
                int returned = RestoreRefresh(targetDevice, before, targetHz);
                ProbeLog.Line("Desktop returned to " + targetHz + " Hz, result " + returned + ", signal " + CcdSignal.DescribeAllTargets());

                if (saved > 0)
                {
                    int cleared = clear(ref address);
                    int flushed = flush(ref address);
                    ProbeLog.Line("Cleared NVIDIA unsupported-resolution cache " + cleared + ", flush " + flushed + ". That cache is what turns VRR off.");
                }

                if (failures != 0)
                {
                    ProbeLog.Line(failures + " NVIDIA timing(s) were outside NvCpl direct preview limits; the EDID override will activate them on driver restart.");
                }
                else
                {
                    ProbeLog.Line("NVIDIA accepted " + string.Join(", ", rates) + " Hz at the panel pixel clock.");
                }
                return 0;
            }
            finally
            {
                try { close?.Invoke(); } catch { }
            }
        }
        finally
        {
            Native.FreeLibrary(lib);
        }
    }

    private static Timing FromDescriptor(int hz, byte[] descriptor)
    {
        int clockKhz = 1 + descriptor[0] + (descriptor[1] << 8) + (descriptor[2] << 16);
        int hActive = 1 + descriptor[4] + (descriptor[5] << 8);
        int hBlank = 1 + descriptor[6] + (descriptor[7] << 8);
        int hFront = 1 + descriptor[8] + ((descriptor[9] & 0x7F) << 8);
        int hSync = 1 + descriptor[10] + (descriptor[11] << 8);
        bool hPositive = (descriptor[9] & 0x80) != 0;
        int vActive = 1 + descriptor[12] + (descriptor[13] << 8);
        int vBlank = 1 + descriptor[14] + (descriptor[15] << 8);
        int vFront = 1 + descriptor[16] + ((descriptor[17] & 0x7F) << 8);
        int vSync = 1 + descriptor[18] + (descriptor[19] << 8);
        bool vPositive = (descriptor[17] & 0x80) != 0;

        return new Timing
        {
            CbSize = 0x64,
            Width = (uint)hActive,
            Height = (uint)vActive,
            Flags = 0x20,
            RefreshInt = (uint)hz,
            FrontX = (uint)hActive,
            FrontY = (uint)vActive,
            Scaling = 0,
            Formula = 5,
            BackX = (uint)hActive,
            BackY = (uint)vActive,
            Interlaced = 0,
            RequestedRefresh = hz,
            ActualRefresh = hz,
            Hfp = (uint)hFront,
            HTotal = (uint)(hActive + hBlank),
            HSync = (uint)hSync,
            HPol = hPositive ? 0u : 1u,
            Vfp = (uint)vFront,
            VTotal = (uint)(vActive + vBlank),
            VSync = (uint)vSync,
            VPol = vPositive ? 0u : 1u,
            PixelClock = clockKhz / 1000f,
            Ce = 0
        };
    }

    private static T? TryBind<T>(nint module, string name) where T : Delegate
    {
        nint address = Native.GetProcAddress(module, name);
        return address == 0 ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T Bind<T>(nint module, string name) where T : Delegate
        => TryBind<T>(module, name) ?? throw new InvalidOperationException("nvcpl.dll is missing " + name);

    private static Desktop CurrentMode(string? deviceName)
    {
        var mode = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
        Native.EnumDisplaySettings(deviceName, Native.EnumCurrentSettings, ref mode);
        return new Desktop(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency, mode);
    }

    private static int Restore(string? deviceName, Desktop desktop)
    {
        Native.DevMode mode = desktop.Raw;
        return Native.ChangeDisplaySettingsEx(deviceName, ref mode, 0, 0, 0);
    }

    private static int RestoreRefresh(string? deviceName, Desktop before, int hz)
    {
        int targetW = before.Width > 0 ? before.Width : 2560;
        int targetH = before.Height > 0 ? before.Height : 1600;
        Native.DevMode? fallback = null;

        for (int i = 0; ; i++)
        {
            var mode = new Native.DevMode { dmSize = (short)Marshal.SizeOf<Native.DevMode>() };
            if (!Native.EnumDisplaySettings(deviceName, i, ref mode))
                break;
            if (mode.dmPelsWidth == targetW && mode.dmPelsHeight == targetH && mode.dmDisplayFrequency == hz)
            {
                fallback ??= mode;
                if (mode.dmBitsPerPel == before.Raw.dmBitsPerPel
                    && mode.dmDisplayOrientation == before.Raw.dmDisplayOrientation
                    && mode.dmDisplayFixedOutput == before.Raw.dmDisplayFixedOutput)
                {
                    return Native.ChangeDisplaySettingsEx(deviceName, ref mode, 0, 0, 0);
                }
            }
        }

        if (fallback is Native.DevMode fb)
            return Native.ChangeDisplaySettingsEx(deviceName, ref fb, 0, 0, 0);
        return Restore(deviceName, before);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetInfoFn(uint infoType, uint param, nint pData, ref ulong pSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AddressFn(ref Address address);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TestFn(ref Address address, int op, ref Timing timing);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeDdsStatusInfo
    {
        public byte IsSupported;
        public byte IsAutomatic;
        public byte Reserved1;
        public byte Reserved2;
        public uint MuxState;
        public uint KmdResponse;
        public uint SrType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Address
    {
        public uint Gpu;
        public uint Display;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x64)]
    private struct Timing
    {
        [FieldOffset(0x00)] public uint CbSize;
        [FieldOffset(0x04)] public uint Width;
        [FieldOffset(0x08)] public uint Height;
        [FieldOffset(0x0C)] public uint Flags;
        [FieldOffset(0x10)] public uint RefreshInt;
        [FieldOffset(0x14)] public uint FrontX;
        [FieldOffset(0x18)] public uint FrontY;
        [FieldOffset(0x1C)] public uint Scaling;
        [FieldOffset(0x20)] public uint Formula;
        [FieldOffset(0x24)] public uint BackX;
        [FieldOffset(0x28)] public uint BackY;
        [FieldOffset(0x2C)] public uint Interlaced;
        [FieldOffset(0x30)] public float RequestedRefresh;
        [FieldOffset(0x34)] public float ActualRefresh;
        [FieldOffset(0x38)] public uint Hfp;
        [FieldOffset(0x3C)] public uint HTotal;
        [FieldOffset(0x40)] public uint HSync;
        [FieldOffset(0x44)] public uint HPol;
        [FieldOffset(0x48)] public uint Vfp;
        [FieldOffset(0x4C)] public uint VTotal;
        [FieldOffset(0x50)] public uint VSync;
        [FieldOffset(0x54)] public uint VPol;
        [FieldOffset(0x5C)] public float PixelClock;
        [FieldOffset(0x60)] public uint Ce;
    }

    private readonly record struct Desktop(int Width, int Height, int Hz, Native.DevMode Raw);
}
