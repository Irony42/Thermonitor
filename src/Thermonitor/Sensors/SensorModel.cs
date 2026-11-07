namespace Thermonitor.Sensors;

/// <summary>One temperature sensor, attached to its top-level device.</summary>
/// <param name="Id">Stable identifier, e.g. <c>/intelcpu/0/temperature/2</c>.</param>
/// <param name="Hw">Identifier of the top-level device (CPU, GPU, board, drive...).</param>
/// <param name="HwType">LibreHardwareMonitor hardware type of that device, e.g. <c>Cpu</c>.</param>
/// <param name="Sub">Name of the sub-device (e.g. the Super I/O chip), null when read from the device itself.</param>
/// <param name="Glitchy">Board sensors: prone to one-off junk readings, see <see cref="ReadingFilter"/>.</param>
internal sealed record SensorInfo(
    string Id,
    string Name,
    string Hw,
    string HwName,
    string HwType,
    string? Sub,
    int Index,
    bool Glitchy);

/// <summary>One read of every sensor. <c>Values[i]</c> belongs to <c>Sensors[i]</c>; NaN means no reading.</summary>
internal sealed record SensorFrame(IReadOnlyList<SensorInfo> Sensors, float[] Values, double ReadMs)
{
    /// <summary>Changes when a sensor appears or disappears.</summary>
    public string Signature { get; } = string.Join('|', Sensors.Select(s => s.Id));
}

/// <summary>Where readings come from: the real hardware, or a fake one in tests.</summary>
internal interface ISensorSource : IDisposable
{
    string Version { get; }

    /// <summary>Whether the kernel driver the source needs is installed (null when unknown).</summary>
    bool? DriverInstalled { get; }

    void Open();

    SensorFrame Read();
}
