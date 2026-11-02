using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using NightLoop.Contracts.Api;
using NightLoop.LocalData;

namespace NightLoop.Client;

/// <summary>
/// Who this install is: a random install id (install.id, made once) and a hardware fingerprint. The fingerprint
/// is a SHA-256 of the machine GUID, the system drive's serial and the CPU id; the raw values never leave the PC.
/// The server only uses it for "new device" emails, so it doesn't have to be tamper-proof.
/// </summary>
public sealed class DeviceIdentity
{
    public DeviceIdentity(Guid installId, string fingerprint, string label)
    {
        InstallId = installId;
        Fingerprint = fingerprint;
        Label = label;
    }

    public Guid InstallId { get; }
    public string Fingerprint { get; }
    public string Label { get; }

    public DeviceInfo ToDeviceInfo() => new(InstallId, Fingerprint, Label);

    /// <summary>Reads (or creates) install.id and computes the fingerprint for this PC.</summary>
    public static DeviceIdentity ForThisPc(string? installIdPath = null)
    {
        var installId = LoadOrCreateInstallId(installIdPath ?? AppPaths.InstallId);
        var raw = string.Join("|", MachineGuid(), SystemDriveSerial(), Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "", Environment.MachineName);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return new DeviceIdentity(installId, fingerprint, MakeLabel());
    }

    internal static Guid LoadOrCreateInstallId(string path)
    {
        try
        {
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing) && existing != Guid.Empty)
                return existing;
        }
        catch (IOException)
        {
            // Unreadable: make a new one below.
        }
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, id.ToString());
        return id;
    }

    /// <summary>e.g. "Windows 11 · DESKTOP-7Q2". At most 64 characters.</summary>
    internal static string MakeLabel()
    {
        var os = Environment.OSVersion.Version is { Major: 10, Build: >= 22000 } ? "Windows 11"
            : OperatingSystem.IsWindows() ? "Windows 10" : RuntimeInformation.OSDescription;
        var label = $"{os} · {Environment.MachineName}";
        return label.Length > AccountRules.DeviceLabelMaxLength ? label[..AccountRules.DeviceLabelMaxLength] : label;
    }

    private static string MachineGuid()
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return "";
        }
    }

    private static string SystemDriveSerial()
    {
        if (!OperatingSystem.IsWindows()) return "";
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return GetVolumeInformationW(root, null, 0, out var serial, out _, out _, null, 0) ? serial.ToString("X8") : "";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer, int nFileSystemNameSize);
}
