using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace UsbIpClient;

/** A USB device plugged into this PC, as reported by `usbipd state`. */
public sealed record LocalUsbDevice(
    string BusId, string Description, string InstanceId, ushort VendorId, ushort ProductId,
    bool Shared, string? ClientAddress)
{
    public string? ProductName { get; init; }
    public IReadOnlyList<string> Kinds { get; init; } = Array.Empty<string>();
}

/** Wraps usbipd-win (the server side). Sharing changes need elevation; reading state doesn't. */
public static class UsbIpd
{
    public static string ExePath => Prerequisites.Server.ExePath;

    private static readonly Regex VidPid = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

    public static async Task<List<LocalUsbDevice>> StateAsync()
    {
        var psi = new ProcessStartInfo(ExePath, "state")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var json = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException((await p.StandardError.ReadToEndAsync()).Trim());

        var list = new List<LocalUsbDevice>();
        using var doc = JsonDocument.Parse(json);
        foreach (var d in doc.RootElement.GetProperty("Devices").EnumerateArray())
        {
            var busId = d.GetProperty("BusId").GetString();
            if (busId == null) continue; // persisted but not currently plugged in
            var instance = d.GetProperty("InstanceId").GetString() ?? "";
            // Devices we imported from another server live on usbip-win2's virtual controller; never re-share those.
            if (Devices.IsBehindService(instance, "usbip2_ude")) continue;
            var m = VidPid.Match(instance);
            list.Add(new LocalUsbDevice(
                busId,
                d.GetProperty("Description").GetString() ?? "USB device",
                instance,
                m.Success ? Convert.ToUInt16(m.Groups[1].Value, 16) : (ushort)0,
                m.Success ? Convert.ToUInt16(m.Groups[2].Value, 16) : (ushort)0,
                d.GetProperty("PersistedGuid").ValueKind == JsonValueKind.String,
                d.GetProperty("ClientIPAddress").GetString())
            {
                ProductName = Devices.BusReportedName(instance),
                Kinds = Devices.FunctionKinds(instance),
            });
        }
        return list;
    }

    /** Shares or stops sharing a device (bind/unbind). Shows a UAC prompt. Returns null on success. */
    public static async Task<string?> SetSharedAsync(string busId, bool share)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { share ? "bind" : "unbind", "--busid", busId },
        };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : $"usbipd {(share ? "bind" : "unbind")} failed (code {p.ExitCode}).";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "Cancelled at the Windows permission prompt.";
        }
    }
}

/** Device tree helpers (CfgMgr32). */
public static class Devices
{
    private const uint CM_DRP_SERVICE = 0x05;
    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x01;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint regType, char[]? buffer, ref uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Child(out uint child, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

    private const uint CM_DRP_CLASS = 0x08;

    /** DEVPKEY_Device_BusReportedDeviceDesc: the product string the device reports over USB. */
    private static DEVPROPKEY BusReportedDeviceDesc = new()
    {
        fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 4,
    };

    /** The device's own product name (USB iProduct string), e.g. "USB Receiver", "QEMU USB Tablet". */
    public static string? BusReportedName(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) return null;
            uint size = 0;
            CM_Get_DevNode_PropertyW(node, ref BusReportedDeviceDesc, out _, null, ref size, 0);
            if (size == 0) return null;
            var buf = new byte[size];
            if (CM_Get_DevNode_PropertyW(node, ref BusReportedDeviceDesc, out _, buf, ref size, 0) != 0) return null;
            var s = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
            return s.Length > 0 ? s : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /** What the device does, from the setup classes of the drivers bound below it: "Keyboard, Mouse". */
    public static IReadOnlyList<string> FunctionKinds(string instanceId)
    {
        var kinds = new List<string>();
        try
        {
            if (CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) return kinds;
            var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(uint n, int depth)
            {
                if (depth > 6) return;
                if (CM_Get_Child(out var c, n, 0) != 0) return;
                do
                {
                    var cls = ClassOf(c);
                    if (cls != null) classes.Add(cls);
                    Walk(c, depth + 1);
                } while (CM_Get_Sibling(out c, c, 0) == 0);
            }
            var own = ClassOf(node);
            if (own != null) classes.Add(own);
            Walk(node, 0);

            void Add(string cls, string label) { if (classes.Contains(cls) && !kinds.Contains(label)) kinds.Add(label); }
            Add("Keyboard", "Keyboard");
            Add("Mouse", "Mouse");
            Add("AudioEndpoint", "Audio");
            Add("MEDIA", "Audio");
            Add("Camera", "Camera");
            Add("Image", "Camera");
            Add("DiskDrive", "Storage");
            Add("Ports", "Serial port");
            Add("Modem", "Modem");
            Add("Net", "Network adapter");
            Add("Bluetooth", "Bluetooth");
            Add("Printer", "Printer");
            Add("SmartCardReader", "Smart card reader");
            Add("Biometric", "Fingerprint reader");
            if (kinds.Count == 0 && classes.Contains("HIDClass")) kinds.Add("HID device");
        }
        catch (DllNotFoundException)
        {
        }
        return kinds;
    }

    private static string? ClassOf(uint node)
    {
        uint len = 0;
        CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_CLASS, out _, null, ref len, 0);
        if (len == 0) return null;
        var buf = new char[len / 2 + 1];
        if (CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_CLASS, out _, buf, ref len, 0) != 0) return null;
        return new string(buf).TrimEnd('\0');
    }

    /** True if the device or any ancestor in the PnP tree is driven by the given service. */
    public static bool IsBehindService(string instanceId, string service)
    {
        try
        {
            if (CM_Locate_DevNodeW(out var node, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != 0) return false;
            for (var depth = 0; depth < 16; depth++)
            {
                if (string.Equals(ServiceOf(node), service, StringComparison.OrdinalIgnoreCase)) return true;
                if (CM_Get_Parent(out var parent, node, 0) != 0) return false;
                node = parent;
            }
        }
        catch (DllNotFoundException)
        {
        }
        return false;
    }

    private static string? ServiceOf(uint node)
    {
        uint len = 0;
        CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_SERVICE, out _, null, ref len, 0);
        if (len == 0) return null;
        var buf = new char[len / 2 + 1];
        if (CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_SERVICE, out _, buf, ref len, 0) != 0) return null;
        return new string(buf).TrimEnd('\0');
    }
}
