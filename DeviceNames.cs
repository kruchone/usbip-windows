using System;
using System.Collections.Generic;
using System.Linq;

namespace UsbIpClient;

/** Picks the most informative name for a USB device from everything we know about it. */
public static class DeviceNames
{
    /** Driver/class names that say nothing about the actual device. */
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB Input Device", "USB Composite Device", "USB Device", "HID-compliant device", "USB Mass Storage Device",
        "Unknown USB Device", "USB Serial Device", "WinUsb Device", "USB Receiver",
    };

    public static bool IsGeneric(string? s) => string.IsNullOrWhiteSpace(s) || Generic.Contains(s.Trim());

    /**
     * Name: custom label, else the device's own product string, else the USB ID database, else a fallback.
     * Generic product strings (e.g. "USB Receiver") get the vendor prepended: "Logitech USB Receiver".
     */
    public static string Best(ushort vid, ushort pid, string? custom, params string?[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        var vendor = UsbIds.ShortVendor(vid);
        var dbProduct = UsbIds.Product(vid, pid);
        var specific = candidates.FirstOrDefault(c => !IsGeneric(c));
        if (specific != null) return specific.Trim();
        if (dbProduct != null) return vendor != null && !dbProduct.Contains(vendor, StringComparison.OrdinalIgnoreCase) ? $"{vendor} {dbProduct}" : dbProduct;
        var generic = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        if (generic != null) return vendor != null ? $"{vendor} {generic.Trim()}" : generic.Trim();
        return vendor != null ? $"{vendor} device" : $"USB device {vid:x4}:{pid:x4}";
    }

    /** "Logitech · Keyboard, Mouse · 046d:c52b" */
    public static string Detail(ushort vid, ushort pid, IEnumerable<string>? kinds, string? extra = null)
    {
        var parts = new List<string>();
        var vendor = UsbIds.ShortVendor(vid);
        if (vendor != null) parts.Add(vendor);
        var k = kinds?.ToList();
        if (k is { Count: > 0 }) parts.Add(string.Join(", ", k));
        parts.Add($"{vid:x4}:{pid:x4}");
        if (extra != null) parts.Add(extra);
        return string.Join(" · ", parts);
    }

    /** Kinds of a remote device from its USB interface classes (no drivers to ask on this side). */
    public static IReadOnlyList<string> KindsFromInterfaces(RemoteDevice d)
    {
        var kinds = new List<string>();
        void Add(string s) { if (!kinds.Contains(s)) kinds.Add(s); }
        foreach (var i in d.Interfaces)
        {
            switch (i.Class)
            {
                case 0x03 when i.SubClass == 1 && i.Protocol == 1: Add("Keyboard"); break;
                case 0x03 when i.SubClass == 1 && i.Protocol == 2: Add("Mouse"); break;
                case 0x01: Add("Audio"); break;
                case 0x02 or 0x0A: Add("Serial port"); break;
                case 0x07: Add("Printer"); break;
                case 0x08: Add("Storage"); break;
                case 0x0B: Add("Smart card reader"); break;
                case 0x0E: Add("Camera"); break;
                case 0xE0: Add("Wireless"); break;
            }
        }
        if (kinds.Count == 0 && d.Interfaces.Any(i => i.Class == 0x03)) kinds.Add("HID device");
        return kinds;
    }
}
