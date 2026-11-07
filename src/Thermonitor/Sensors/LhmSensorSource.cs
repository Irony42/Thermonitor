using System.Diagnostics;
using System.Reflection;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Thermonitor.Platform;

namespace Thermonitor.Sensors;

/// <summary>Reads temperatures through LibreHardwareMonitor (needs admin rights and the PawnIO driver for CPU and board).</summary>
internal sealed class LhmSensorSource : ISensorSource
{
    private Computer? _computer;
    private int _reads;

    public string Version { get; } = typeof(Computer).Assembly.GetName().Version?.ToString(3) ?? "?";

    public bool? DriverInstalled { get; } = DetectPawnIo();

    public void Open()
    {
        var sw = Stopwatch.StartNew();
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = true,
            IsPsuEnabled = true,
            IsBatteryEnabled = true,
            IsNetworkEnabled = false,
        };
        _computer.Open();
        Log.Write($"LibreHardwareMonitor {Version} opened in {sw.ElapsedMilliseconds} ms, pawnio={DriverInstalled}");
    }

    public SensorFrame Read()
    {
        var hardware = (_computer ?? throw new InvalidOperationException("Open() first")).Hardware;
        var failed = new HashSet<IHardware>();
        var total = Stopwatch.StartNew();

        // The CPU is by far the slowest device (one driver round-trip per core and register, ~275 ms
        // on an i7-13700KF), so it is read on a second thread while the other devices are read here.
        var cpuWatch = new Stopwatch();
        var cpu = Task.Run(() =>
        {
            cpuWatch.Start();
            foreach (var hw in hardware)
                if (hw.HardwareType == HardwareType.Cpu) TryUpdate(hw, failed);
            cpuWatch.Stop();
        });
        double othersMs;
        try
        {
            foreach (var hw in hardware)
                if (hw.HardwareType != HardwareType.Cpu) TryUpdate(hw, failed);
            othersMs = total.Elapsed.TotalMilliseconds;
        }
        finally
        {
            // Never leave the CPU read running: the next poll would update the CPU from two threads.
            cpu.GetAwaiter().GetResult();
        }
        var readMs = total.Elapsed.TotalMilliseconds;

        if (++_reads == 5)
            Log.Write($"read times: cpu {cpuWatch.Elapsed.TotalMilliseconds:0} ms, other devices {othersMs:0} ms, total {readMs:0} ms");

        var infos = new List<SensorInfo>(64);
        var values = new List<float>(64);
        foreach (var hw in hardware)
        {
            var stale = failed.Contains(hw);   // its sensors still hold the previous values
            var sensors = new List<ISensor>();
            Collect(hw, sensors);
            foreach (var s in sensors)
            {
                infos.Add(Describe(s));
                values.Add(stale ? float.NaN : s.Value ?? float.NaN);
            }
        }
        return new SensorFrame(infos, values.ToArray(), readMs);
    }

    // One misbehaving device (unplugged PSU, flaky USB controller...) must not blank all the others.
    private void TryUpdate(IHardware hw, HashSet<IHardware> failed)
    {
        try
        {
            UpdateDeep(hw);
        }
        catch (Exception ex)
        {
            lock (failed) failed.Add(hw);
            lock (_reported)
                if (_reported.Add(hw.Identifier.ToString()))
                    Log.Write($"{hw.Name} cannot be read (reported once): {ex}");
        }
    }

    private readonly HashSet<string> _reported = [];

    private static void UpdateDeep(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateDeep(sub);
    }

    private static void Collect(IHardware hw, List<ISensor> into)
    {
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Temperature && ReadingFilter.IsTemperatureReading(s.Name))
                into.Add(s);
        foreach (var sub in hw.SubHardware) Collect(sub, into);
    }

    private static SensorInfo Describe(ISensor s)
    {
        var top = s.Hardware;
        while (top.Parent is not null) top = top.Parent;
        return new SensorInfo(
            s.Identifier.ToString(),
            s.Name,
            top.Identifier.ToString(),
            top.Name,
            top.HardwareType.ToString(),
            ReferenceEquals(s.Hardware, top) ? null : s.Hardware.Name,
            s.Index,
            Glitchy: top.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController);
    }

    // LibreHardwareMonitor >= 0.9.5 reads CPU and board sensors through the PawnIO driver.
    private static bool? DetectPawnIo()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"))
                if (key is not null) return true;

            var type = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.PawnIo.PawnIo");
            var prop = type?.GetProperty("IsInstalled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return prop?.GetValue(null) is bool installed && installed;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try { _computer?.Close(); }
        catch (Exception ex) { Log.Write("close: " + ex.Message); }
    }
}
