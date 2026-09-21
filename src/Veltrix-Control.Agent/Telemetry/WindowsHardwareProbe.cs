using System.Diagnostics;
using System.Runtime.InteropServices;
using VeltrixControl.Contracts;

namespace VeltrixControl.Agent.Telemetry;

public sealed class WindowsHardwareProbe
{
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;

    public static HardwareInventory ReadInventory()
    {
        var memory = ReadMemory();
        return new HardwareInventory(
            RuntimeInformation.OSDescription,
            Environment.OSVersion.VersionString,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            checked((long)memory.TotalPhys),
            ReadDisks(),
            typeof(WindowsHardwareProbe).Assembly.GetName().Version?.ToString(3) ?? "0.10.8",
            MacAddress: ReadPrimaryMac());
    }

    private static string? ReadPrimaryMac()
    {
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var bytes = adapter.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 6) continue;
                return string.Join(":", bytes.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // A driver can refuse inspection; Wake-on-LAN is simply unavailable.
        }
        return null;
    }

    public TelemetrySnapshot ReadTelemetry()
    {
        var memory = ReadMemory();
        return new TelemetrySnapshot(
            DateTimeOffset.UtcNow,
            ReadCpuPercent(),
            checked((long)(memory.TotalPhys - memory.AvailPhys)),
            checked((long)memory.TotalPhys),
            Environment.TickCount64 / 1000,
            ReadDisks());
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);
        var idleDelta = idleValue - _previousIdle;
        var totalDelta = kernelValue - _previousKernel + userValue - _previousUser;
        _previousIdle = idleValue;
        _previousKernel = kernelValue;
        _previousUser = userValue;
        return totalDelta == 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static List<DiskInventory> ReadDisks()
    {
        var disks = new List<DiskInventory>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            try
            {
                disks.Add(new DiskInventory(drive.Name, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // A removable drive can disappear between IsReady and inspection.
            }
        }
        return disks;
    }

    private static MemoryStatus ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new InvalidOperationException("Windows memory telemetry is unavailable.");
        return status;
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.HighDateTime << 32) | value.LowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint LowDateTime; public uint HighDateTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);
}
