using Microsoft.Win32;
using System.Windows;

// NVIDIA's custom-timing API rejects this panel's 60/75 Hz modes even though
// they are within the panel's EDID limits. Stage the same valid Type VII modes
// as a monitor EDID override instead, retaining the native 240 Hz preferred mode.
// EDID_OVERRIDE is attached to the monitor's Enum\DISPLAY instance, not to a
// GPU-specific NVD0000 instance. The adapter-label heuristic therefore must not
// be used to find the EDID key for the NVIDIA path.
internal static class NvidiaPanelTiming
{
    public static int Apply(IReadOnlyList<int> rates, IReadOnlyList<byte[]> descriptors)
    {
        if (rates.Count != descriptors.Count || rates.Count == 0)
        {
            ProbeLog.Line("No NVIDIA timings to stage.");
            return 1;
        }

        MonitorInfo? monitor = MonitorLocator.FindInternal();
        if (monitor == null)
        {
            ProbeLog.Line("No active internal monitor was found. No NVIDIA EDID override was written.");
            return 1;
        }

        // These are the monitor's actual Enum\DISPLAY\<monitor>\<instance>
        // registry paths. They are the correct location for EDID_OVERRIDE even
        // when NVIDIA is the active adapter; they are not named NVD0000.
        string[] monitorPaths = monitor.RegistryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (monitorPaths.Length == 0)
        {
            ProbeLog.Line("No monitor EDID registry instances were found. No override was written.");
            return 1;
        }

        var pending = new List<(string Path, byte[] Block0, byte[] Block1, byte[][] Extras)>();
        try
        {
            foreach (string path in monitorPaths)
            {
                using RegistryKey parameters = OpenParameters(path, writable: false);
                if (parameters.GetSubKeyNames().Contains("EDID_OVERRIDE", StringComparer.OrdinalIgnoreCase))
                {
                    ProbeLog.Line("An EDID_OVERRIDE already exists at " + path + ". Refusing to replace an unknown override.");
                    return 1;
                }

                if (parameters.GetValue("EDID") is not byte[] originalEdid || originalEdid.Length < 256)
                {
                    ProbeLog.Line("Monitor EDID at " + path + " is missing or shorter than 256 bytes.");
                    return 1;
                }

                PanelEdid.BuildOverride(originalEdid, descriptors,
                    out byte[] block0, out byte[] block1, out byte[][] extras);
                pending.Add((path, block0, block1, extras));
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Line("Could not build the NVIDIA monitor EDID override. Nothing was written.");
            ProbeLog.Line(ex.ToString());
            return 1;
        }

        var writtenPaths = new List<string>();
        try
        {
            foreach (var item in pending)
            {
                WriteBlocks(item.Path, item.Block0, item.Block1, item.Extras);
                writtenPaths.Add(item.Path);
                ProbeLog.Line("Staged monitor EDID override at " + item.Path + ".");
            }
        }
        catch (Exception ex)
        {
            foreach (string path in writtenPaths)
            {
                try { DeleteOverride(path); }
                catch (Exception rollbackError) { ProbeLog.Line("Rollback failed at " + path + ": " + rollbackError.Message); }
            }
            ProbeLog.Line("Writing the NVIDIA monitor EDID override failed; staged keys were rolled back where possible.");
            ProbeLog.Line(ex.ToString());
            return 1;
        }

        ProbeLog.Line("Staged " + string.Join(", ", rates) + " Hz in the monitor EDID override; original 240 Hz preferred timing is preserved.");
        ProbeLog.Line("The override is monitor-scoped, not GPU-scoped. Restart the display driver to load the NVIDIA modes; press F8 within 15 seconds if the panel stays black.");
        DialogResult restart = MessageBox.Show(
            "The monitor EDID override is staged. Restart the display driver now to load the new modes? The screen may go black briefly; press F8 within 15 seconds to recover the native 240 Hz timing.",
            "Restart display driver",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (restart != MessageBoxResult.OK)
        {
            ProbeLog.Line("Driver restart skipped. Click Restart driver later to load the staged rates.");
            return 0;
        }

        string outcome = DriverRestart.Restart(monitor);
        ProbeLog.Line(outcome);
        return outcome.StartsWith("Restarted", StringComparison.Ordinal) ? 0 : 1;
    }

    private static RegistryKey OpenParameters(string path, bool writable)
    {
        const string prefix = @"\Registry\Machine\";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            path = path[prefix.Length..];
        return Registry.LocalMachine.OpenSubKey(path, writable)
            ?? throw new InvalidOperationException("Could not open " + path);
    }

    private static void WriteBlocks(string path, byte[] block0, byte[] block1, byte[][] extras)
    {
        using RegistryKey key = OpenParameters(path, writable: true);
        using RegistryKey over = key.CreateSubKey("EDID_OVERRIDE", writable: true)
            ?? throw new InvalidOperationException("Could not create EDID_OVERRIDE at " + path);
        over.SetValue("0", block0, RegistryValueKind.Binary);
        over.SetValue("1", block1, RegistryValueKind.Binary);
        for (int i = 0; i < extras.Length; i++)
            over.SetValue((i + 2).ToString(), extras[i], RegistryValueKind.Binary);
    }

    private static void DeleteOverride(string path)
    {
        using RegistryKey key = OpenParameters(path, writable: true);
        if (key.GetSubKeyNames().Contains("EDID_OVERRIDE", StringComparer.OrdinalIgnoreCase))
            key.DeleteSubKeyTree("EDID_OVERRIDE");
    }
}
