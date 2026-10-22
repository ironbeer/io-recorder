using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IoRecorder.Core;

// More of the machine for environment.json, read once at the start from Windows' management data (WMI) and the registry:
// what tells later why two records differ — the disks behind the drives (kind, bus, RAID, health), the SMB connections to a
// NAS, what sits in every file request (antivirus, the search indexer, the prefetcher), the CPU, memory, GPU and power plan.
// Nothing that identifies people or networks (user names, IP addresses, Wi-Fi names, antivirus exclusions) is read. Each part
// that cannot be read (no administrator rights, a component missing) is noted in Problems and the rest is still collected.

public sealed class ComputerInfo
{
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    // A hypervisor runs (also on a physical PC with Hyper-V or virtualization-based security on); VirtualMachine is the guess
    // from the manufacturer and model that this PC itself is a virtual machine.
    public bool? HypervisorPresent { get; set; }
    public bool VirtualMachine { get; set; }
}
public sealed class CpuInfo
{
    public string? Name { get; set; }
    public int? Cores { get; set; }
    public int? LogicalProcessors { get; set; }
    public int? MaxClockMHz { get; set; }
    public int? L2CacheKiB { get; set; }
    public int? L3CacheKiB { get; set; }
}
public sealed class MemoryModule
{
    public long CapacityBytes { get; set; }
    public int? SpeedMHz { get; set; }
    public int? ConfiguredSpeedMHz { get; set; }
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
}
public sealed class GpuInfo
{
    public string? Name { get; set; }
    public string? DriverVersion { get; set; }
    public long? MemoryBytes { get; set; }
}
public sealed class PowerInfo
{
    public string? Plan { get; set; }
    // On AC power (null: unknown); a battery and its charge when the PC has one.
    public bool? OnAc { get; set; }
    public bool HasBattery { get; set; }
    public int? BatteryPercent { get; set; }
}
public sealed class PageFileInfo
{
    public string Path { get; set; } = "";
    public long? AllocatedMiB { get; set; }
    public long? CurrentUsageMiB { get; set; }
}
public sealed class DiskInfo
{
    public int Number { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    // SSD / HDD / SCM / 不明, and how it is attached: NVMe, SATA, SAS, USB, RAID (behind a RAID controller), Virtual, ...
    public string? MediaType { get; set; }
    public string? BusType { get; set; }
    public long? SizeBytes { get; set; }
    public string? Firmware { get; set; }
    public int? SpindleSpeedRpm { get; set; }
    public string? Health { get; set; }
    public string? PartitionStyle { get; set; }
    public bool? Boot { get; set; }
    public bool? System { get; set; }
    // Windows' own software RAID on the old "dynamic disk" (mirror, stripe, span) keeps its volumes in an LDM partition.
    public bool DynamicDisk { get; set; }
    public List<string> DriveLetters { get; set; } = [];
    // Read where the disk reports them (administrator; not every disk or virtual disk does).
    public int? TemperatureC { get; set; }
    public int? WearPercent { get; set; }
    public long? ReadErrors { get; set; }
    public long? WriteErrors { get; set; }
    public long? PowerOnHours { get; set; }
}
public sealed class ControllerInfo
{
    public string? Name { get; set; }
    public string? Manufacturer { get; set; }
    public string? DriverVersion { get; set; }
    public string? DriverDate { get; set; }
    // SCSIAdapter (RAID, SAS, NVMe, storage controllers) or HDC (SATA / IDE).
    public string? Class { get; set; }
}
public sealed class StorageSpaceInfo
{
    public string? Name { get; set; }
    public string? Pool { get; set; }
    // Simple / Mirror / Parity, and how it is laid out.
    public string? Resiliency { get; set; }
    public int? Columns { get; set; }
    public long? InterleaveBytes { get; set; }
    public int? DataCopies { get; set; }
    public int? Redundancy { get; set; }
    public long? SizeBytes { get; set; }
    public string? Health { get; set; }
}
public sealed class SmbConnectionInfo
{
    public string? Server { get; set; }
    public string? Share { get; set; }
    public string? Dialect { get; set; }
    public bool? Signed { get; set; }
    public bool? Encrypted { get; set; }
    public bool? ContinuouslyAvailable { get; set; }
    public int? OpenFiles { get; set; }
}
public sealed class SmbChannelInfo
{
    public string? Server { get; set; }
    public bool? Selected { get; set; }
    public long? ClientLinkSpeedBitsPerSecond { get; set; }
    public bool? ClientRss { get; set; }
    public bool? ClientRdma { get; set; }
}
public sealed class AntivirusInfo
{
    public string? Name { get; set; }
    public bool? Enabled { get; set; }
    public bool? UpToDate { get; set; }
    public uint? ProductState { get; set; }
}
public sealed class DefenderInfo
{
    public bool? AntivirusEnabled { get; set; }
    public bool? RealTimeProtection { get; set; }
    public bool? OnAccessProtection { get; set; }
    public bool? TamperProtected { get; set; }
    // Normal / Passive mode (another antivirus is the active one) / EDR Block Mode.
    public string? RunningMode { get; set; }
}
public sealed class ServiceInfo
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? State { get; set; }
    public string? StartMode { get; set; }
}

internal static class MachineDetails
{
    private const string Storage = @"root\Microsoft\Windows\Storage";
    public static void Collect(EnvironmentInfo info)
    {
        var problems = info.Problems;
        foreach (var o in Query(@"root\cimv2", "SELECT Manufacturer, Model, HypervisorPresent FROM Win32_ComputerSystem", problems, "PC"))
        {
            info.Computer = new() { Manufacturer = Text(o, "Manufacturer"), Model = Text(o, "Model"), HypervisorPresent = Value<bool>(o, "HypervisorPresent") };
            string both = $"{info.Computer.Manufacturer} {info.Computer.Model}";
            info.Computer.VirtualMachine = new[] { "Virtual", "VMware", "QEMU", "KVM", "VirtualBox", "Xen", "Parallels", "Bochs", "Amazon EC2", "Google Compute" }.Any(k => both.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var o in Query(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor", problems, "CPU").Take(1))
            info.Cpu = new() { Name = Text(o, "Name")?.Trim(), Cores = Int(o, "NumberOfCores"), LogicalProcessors = Int(o, "NumberOfLogicalProcessors"), MaxClockMHz = Int(o, "MaxClockSpeed"), L2CacheKiB = Int(o, "L2CacheSize"), L3CacheKiB = Int(o, "L3CacheSize") };
        foreach (var o in Query(@"root\cimv2", "SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory", problems, "メモリ"))
            info.MemoryModules.Add(new() { CapacityBytes = Long(o, "Capacity") ?? 0, SpeedMHz = Int(o, "Speed"), ConfiguredSpeedMHz = Int(o, "ConfiguredClockSpeed"), Manufacturer = Text(o, "Manufacturer")?.Trim(), PartNumber = Text(o, "PartNumber")?.Trim() });
        info.Gpus = Gpus(problems);
        info.Power = Power();
        info.BootTimeUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        foreach (var o in Query(@"root\cimv2", "SELECT Name, AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage", problems, "ページファイル"))
            info.PageFiles.Add(new() { Path = Text(o, "Name") ?? "", AllocatedMiB = Long(o, "AllocatedBaseSize"), CurrentUsageMiB = Long(o, "CurrentUsage") });
        Disks(info, problems);
        foreach (var o in Query(@"root\cimv2", "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver WHERE DeviceClass='SCSIADAPTER' OR DeviceClass='HDC'", problems, "ストレージのコントローラー"))
            info.StorageControllers.Add(new() { Name = Text(o, "DeviceName"), Manufacturer = Text(o, "Manufacturer"), DriverVersion = Text(o, "DriverVersion"), DriverDate = Date(Text(o, "DriverDate")), Class = Text(o, "DeviceClass") });
        var pools = Query(Storage, "SELECT ObjectId, FriendlyName, IsPrimordial FROM MSFT_StoragePool WHERE IsPrimordial = FALSE", problems, "記憶域プール");
        foreach (ManagementObject o in Query(Storage, "SELECT * FROM MSFT_VirtualDisk", problems, "記憶域スペース").OfType<ManagementObject>())
        {
            string? pool = null;
            try { pool = o.GetRelated("MSFT_StoragePool").Cast<ManagementBaseObject>().Select(p => Text(p, "FriendlyName")).FirstOrDefault(); } catch (Exception ex) when (Wmi(ex)) { }
            info.StorageSpaces.Add(new() { Name = Text(o, "FriendlyName"), Pool = pool, Resiliency = Text(o, "ResiliencySettingName"), Columns = Int(o, "NumberOfColumns"), InterleaveBytes = Long(o, "Interleave"),
                DataCopies = Int(o, "NumberOfDataCopies"), Redundancy = Int(o, "PhysicalDiskRedundancy"), SizeBytes = Long(o, "Size"), Health = Health(Int(o, "HealthStatus")) });
        }
        if (pools.Count > 0 && info.StorageSpaces.Count == 0) problems.Add("記憶域プールはありますが、仮想ディスクを読めませんでした。");
        Ntfs(info, problems);
        Smb(info, problems);
        try
        {
            using var network = Registry.CurrentUser.OpenSubKey("Network");
            foreach (var letter in network?.GetSubKeyNames() ?? [])
            {
                using var drive = network!.OpenSubKey(letter);
                if (drive?.GetValue("RemotePath") is string remote) info.MappedDrives[letter.ToUpperInvariant() + ":"] = remote;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { problems.Add("ネットワークドライブの割り当て: " + ex.Message); }
        foreach (var o in Query(@"root\cimv2", "SELECT Enabled, Active FROM Win32_OfflineFilesCache", problems, "オフラインファイル"))
        { info.OfflineFilesEnabled = Value<bool>(o, "Enabled"); info.OfflineFilesActive = Value<bool>(o, "Active"); }
        foreach (var o in Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct", problems, "ウイルス対策"))
        {
            uint? state = Value<uint>(o, "productState");
            info.Antivirus.Add(new() { Name = Text(o, "displayName"), ProductState = state, Enabled = state is { } s ? ((s >> 8) & 0x10) != 0 : null, UpToDate = state is { } t ? (t & 0xFF) == 0 : null });
        }
        foreach (var o in Query(@"root\Microsoft\Windows\Defender", "SELECT AntivirusEnabled, RealTimeProtectionEnabled, OnAccessProtectionEnabled, IsTamperProtected, AMRunningMode FROM MSFT_MpComputerStatus", problems, "Microsoft Defender"))
            info.Defender = new() { AntivirusEnabled = Value<bool>(o, "AntivirusEnabled"), RealTimeProtection = Value<bool>(o, "RealTimeProtectionEnabled"), OnAccessProtection = Value<bool>(o, "OnAccessProtectionEnabled"), TamperProtected = Value<bool>(o, "IsTamperProtected"), RunningMode = Text(o, "AMRunningMode") };
        foreach (var o in Query(@"root\cimv2", "SELECT Name, DisplayName, State, StartMode FROM Win32_Service WHERE Name='WSearch' OR Name='SysMain'", problems, "サービス"))
            info.Services.Add(new() { Name = Text(o, "Name") ?? "", DisplayName = Text(o, "DisplayName"), State = Text(o, "State"), StartMode = Text(o, "StartMode") });
    }

    // Volumes of the drives: cluster size, BitLocker and the disks they are on (after Disks, which knows the letters).
    public static void Volumes(EnvironmentInfo info)
    {
        var encryption = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume", info.Problems, "BitLocker"))
            if (Text(o, "DriveLetter") is { Length: > 0 } letter && Int(o, "ProtectionStatus") is { } status) encryption[letter] = status;
        foreach (var v in info.Volumes)
        {
            if (v.Root is not { Length: > 0 } root) continue;
            if (GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)) v.ClusterBytes = (long)sectorsPerCluster * bytesPerSector;
            if (root.Length >= 2 && root[1] == ':')
            {
                string letter = root[..2].ToUpperInvariant();
                if (encryption.TryGetValue(letter, out int protection)) v.BitLocker = protection switch { 0 => "オフ", 1 => "オン", _ => "不明" };
                v.DiskNumbers = info.Disks.Where(d => d.DriveLetters.Contains(letter, StringComparer.OrdinalIgnoreCase)).Select(d => d.Number).ToList();
            }
        }
    }

    private static void Disks(EnvironmentInfo info, List<string> problems)
    {
        var physical = Query(Storage, "SELECT * FROM MSFT_PhysicalDisk", problems, "物理ディスク").OfType<ManagementObject>().ToList();
        foreach (var o in Query(Storage, "SELECT Number, FriendlyName, Model, BusType, Size, PartitionStyle, IsBoot, IsSystem, HealthStatus, FirmwareVersion FROM MSFT_Disk", problems, "ディスク"))
        {
            if (Int(o, "Number") is not { } number) continue;
            var disk = new DiskInfo
            {
                Number = number, Name = Text(o, "FriendlyName"), Model = Text(o, "Model")?.Trim(), BusType = Bus(Int(o, "BusType")), SizeBytes = Long(o, "Size"), Firmware = Text(o, "FirmwareVersion")?.Trim(),
                PartitionStyle = Int(o, "PartitionStyle") switch { 1 => "MBR", 2 => "GPT", 3 => "RAW", _ => null }, Boot = Value<bool>(o, "IsBoot"), System = Value<bool>(o, "IsSystem"), Health = Health(Int(o, "HealthStatus"))
            };
            // The physical disk of the same number tells the media and its own health; a disk built from several (Storage
            // Spaces, a RAID controller's logical disk) has none of its own.
            if (physical.FirstOrDefault(p => Text(p, "DeviceId") == number.ToString()) is { } p)
            {
                disk.MediaType = Int(p, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "不明" };
                disk.SpindleSpeedRpm = Int(p, "SpindleSpeed") is { } rpm and > 0 and < 100000 ? rpm : null;
                disk.Health = Health(Int(p, "HealthStatus")) ?? disk.Health;
                try
                {
                    foreach (ManagementBaseObject r in p.GetRelated("MSFT_StorageReliabilityCounter"))
                    {
                        disk.TemperatureC = Int(r, "Temperature") is { } c and > 0 ? c : null; disk.WearPercent = Int(r, "Wear");
                        disk.ReadErrors = Long(r, "ReadErrorsTotal"); disk.WriteErrors = Long(r, "WriteErrorsTotal"); disk.PowerOnHours = Long(r, "PowerOnHours");
                    }
                }
                catch (Exception ex) when (Wmi(ex)) { }
            }
            info.Disks.Add(disk);
        }
        foreach (var o in Query(Storage, "SELECT DiskNumber, DriveLetter, GptType, MbrType FROM MSFT_Partition", problems, "パーティション"))
        {
            if (Int(o, "DiskNumber") is not { } number || info.Disks.FirstOrDefault(d => d.Number == number) is not { } disk) continue;
            if (o["DriveLetter"] is char letter && letter != '\0') disk.DriveLetters.Add(char.ToUpperInvariant(letter) + ":");
            string gpt = Text(o, "GptType") ?? "";
            if (Int(o, "MbrType") == 0x42 || gpt.Contains("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3", StringComparison.OrdinalIgnoreCase) || gpt.Contains("af9b60a0-1431-4f62-bc68-3311714a69ad", StringComparison.OrdinalIgnoreCase))
                disk.DynamicDisk = true;
        }
        info.Disks.Sort((a, b) => a.Number.CompareTo(b.Number));
    }

    private static void Ntfs(EnvironmentInfo info, List<string> problems)
    {
        try
        {
            using var fs = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            // 0/1 set by an administrator, 2/3 managed by the system (the low bit: the update is off).
            if (fs?.GetValue("NtfsDisableLastAccessUpdate") is int access)
                info.NtfsLastAccessUpdate = (access & 1) == 0 ? "する" + ((access & 0x80000000) != 0 ? "（システムが管理）" : "") : "しない" + ((access & 0x80000000) != 0 ? "（システムが管理）" : "");
            if (fs?.GetValue("NtfsDisable8dot3NameCreation") is int shortNames)
                info.NtfsShortNames = shortNames switch { 0 => "すべてのボリュームで作る", 1 => "作らない", 2 => "ボリュームごとの設定に従う", 3 => "システムドライブ以外では作らない", _ => shortNames.ToString() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { problems.Add("NTFSの設定: " + ex.Message); }
    }

    private static void Smb(EnvironmentInfo info, List<string> problems)
    {
        const string smb = @"root\Microsoft\Windows\SMB";
        foreach (var o in Query(smb, "SELECT * FROM MSFT_SmbConnection", problems, "SMBの接続"))
            info.SmbConnections.Add(new() { Server = Text(o, "ServerName"), Share = Text(o, "ShareName"), Dialect = Text(o, "Dialect"), Signed = Value<bool>(o, "Signed"), Encrypted = Value<bool>(o, "Encrypted"),
                ContinuouslyAvailable = Value<bool>(o, "ContinuouslyAvailable"), OpenFiles = Int(o, "NumOpens") });
        // Without the addresses at either end.
        foreach (var o in Query(smb, "SELECT * FROM MSFT_SmbMultichannelConnection", problems, "SMBのマルチチャネル"))
            info.SmbChannels.Add(new() { Server = Text(o, "ServerName"), Selected = Value<bool>(o, "Selected"), ClientLinkSpeedBitsPerSecond = Long(o, "ClientLinkSpeed"), ClientRss = Value<bool>(o, "ClientRSSCapable"), ClientRdma = Value<bool>(o, "ClientRdmaCapable") });
        foreach (var o in Query(smb, "SELECT * FROM MSFT_SmbClientConfiguration", problems, "SMBクライアントの設定").Take(1))
            foreach (var p in o.Properties)
                if (p.Value is { } value && p.Name is not ("PSComputerName")) info.SmbClient[p.Name] = value is Array array ? string.Join(", ", array.Cast<object>()) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    // Names and drivers from WMI; the memory from the display adapters' registry keys (WMI's value stops at 4 GiB).
    private static List<GpuInfo> Gpus(List<string> problems)
    {
        var memory = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var adapters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in adapters?.GetSubKeyNames() ?? [])
                try
                {
                    using var key = adapters!.OpenSubKey(name);
                    if (key?.GetValue("DriverDesc") is string desc && key.GetValue("HardwareInformation.qwMemorySize") is long size) memory[desc] = size;
                }
                catch (System.Security.SecurityException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return Query(@"root\cimv2", "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController", problems, "GPU")
            .Select(o => new GpuInfo { Name = Text(o, "Name"), DriverVersion = Text(o, "DriverVersion"), MemoryBytes = Text(o, "Name") is { } n && memory.TryGetValue(n, out long m) ? m : Long(o, "AdapterRAM") }).ToList();
    }

    private static PowerInfo Power()
    {
        var power = new PowerInfo();
        if (PowerGetActiveScheme(0, out nint scheme) == 0)
            try
            {
                var guid = Marshal.PtrToStructure<Guid>(scheme);
                uint size = 0;
                PowerReadFriendlyName(0, ref guid, 0, 0, null, ref size);
                var name = new byte[size];
                if (size > 0 && PowerReadFriendlyName(0, ref guid, 0, 0, name, ref size) == 0) power.Plan = System.Text.Encoding.Unicode.GetString(name).TrimEnd('\0');
            }
            finally { LocalFree(scheme); }
        if (GetSystemPowerStatus(out var status))
        {
            power.OnAc = status.AcLine switch { 0 => false, 1 => true, _ => null };
            power.HasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
            power.BatteryPercent = power.HasBattery && status.BatteryPercent <= 100 ? status.BatteryPercent : null;
        }
        return power;
    }

    private static List<ManagementBaseObject> Query(string scope, string query, List<string> problems, string label)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (Wmi(ex)) { problems.Add($"{label}: {ex.Message.Trim()}"); return []; }
    }
    private static bool Wmi(Exception ex) => ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException or InvalidCastException or ArgumentException;
    private static object? Read(ManagementBaseObject o, string name) { try { return o[name]; } catch (ManagementException) { return null; } }
    private static string? Text(ManagementBaseObject o, string name) => Read(o, name)?.ToString() is { Length: > 0 } s ? s : null;
    private static T? Value<T>(ManagementBaseObject o, string name) where T : struct => Read(o, name) is T v ? v : null;
    private static long? Long(ManagementBaseObject o, string name) => Read(o, name) switch { null => null, string s => long.TryParse(s, out long l) ? l : null, var v => Convert.ToInt64(v) };
    private static int? Int(ManagementBaseObject o, string name) => Long(o, name) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;
    // WMI datetime (20230115000000.******+***) → 2023-01-15.
    private static string? Date(string? wmi) => wmi is { Length: >= 8 } ? $"{wmi[..4]}-{wmi[4..6]}-{wmi[6..8]}" : null;
    private static string? Health(int? status) => status switch { 0 => "正常", 1 => "警告", 2 => "異常", null => null, _ => "不明" };
    private static string? Bus(int? type) => type switch
    {
        null => null, 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        14 => "Virtual", 15 => "File Backed Virtual", 16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", _ => "不明"
    };
    [StructLayout(LayoutKind.Sequential)] private struct SystemPowerStatus { public byte AcLine, BatteryFlag, BatteryPercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(nint root, ref Guid scheme, nint subgroup, nint setting, byte[]? buffer, ref uint size);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
}
