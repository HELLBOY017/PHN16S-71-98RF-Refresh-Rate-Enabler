using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;

// NVIDIA's custom-timing API rejects this panel's 60/75 Hz modes even though
// they are within the panel's EDID limits. Stage the same valid Type VII modes
// as an override on NVIDIA's monitor instance instead. The override builder
// keeps the original 240 Hz preferred timing in the first DisplayID section and
// chains the added rates after it, preserving the NVIDIA GPU-only switch mode list.
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

        string[] nvidiaPaths = monitor.RegistryPaths
            .Where(path => string.Equals(Program.AdapterLabel(path), "NVIDIA", StringComparison.Ordinal))
            .ToArray();
        if (nvidiaPaths.Length == 0)
        {
            ProbeLog.Line("No NVIDIA monitor registry instance was found. No EDID override was written.");
            return 1;
        }

        var pending = new List<(string Path, byte[] Block0, byte[] Block1, byte[][] Extras)>();
        try
        {
            foreach (string path in nvidiaPaths)
            {
                using RegistryKey parameters = OpenParameters(path, writable: false);
                if (parameters.GetSubKeyNames().Contains("EDID_OVERRIDE", StringComparer.OrdinalIgnoreCase))
                {
                    ProbeLog.Line("An EDID_OVERRIDE already exists on NVIDIA instance " + path + ". Refusing to replace an unknown override; remove it first.");
                    return 1;
                }

                if (parameters.GetValue("EDID") is not byte[] originalEdid || originalEdid.Length < 256)
                {
                    ProbeLog.Line("NVIDIA EDID at " + path + " is missing or shorter than 256 bytes.");
                    return 1;
                }

                PanelEdid.BuildOverride(originalEdid, descriptors,
                    out byte[] block0, out byte[] block1, out byte[][] extras);
                pending.Add((path, block0, block1, extras));
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Line("Could not build the NVIDIA EDID override. Nothing was written.");
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
                ProbeLog.Line("Staged NVIDIA EDID override at " + item.Path + ".");
            }
        }
        catch (Exception ex)
        {
            foreach (string path in writtenPaths)
            {
                try { DeleteOverride(path); }
                catch (Exception rollbackError) { ProbeLog.Line("Rollback failed at " + path + ": " + rollbackError.Message); }
            }
            ProbeLog.Line("Writing the NVIDIA EDID override failed; staged keys were rolled back where possible.");
            ProbeLog.Line(ex.ToString());
            return 1;
        }

        ProbeLog.Line("Staged " + string.Join(", ", rates) + " Hz on the NVIDIA EDID, keeping the original 240 Hz preferred timing.");
        ProbeLog.Line("A display-driver restart is required to load the NVIDIA EDID override.");
        DialogResult restart = MessageBox.Show(
            "The NVIDIA EDID override is staged. Restart the display driver now to load the new modes? The screen may go black briefly; press F8 within 15 seconds to recover the native 240 Hz timing.",
            "Restart NVIDIA display driver",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (restart != MessageBoxResult.OK)
        {
            ProbeLog.Line("Driver restart skipped. Click Restart driver later to load the staged NVIDIA rates.");
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
