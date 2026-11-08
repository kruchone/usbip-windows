using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace UsbIpClient;

public sealed class Settings
{
    /** Servers added by address ("host" or "host:port"), e.g. reached over a VPN where mDNS doesn't work. */
    public List<string> ManualServers { get; set; } = new();

    /** Devices to attach automatically whenever they're available: "{serverKey}|{busId}". */
    public HashSet<string> AutoAttach { get; set; } = new();

    public bool TrayHintShown { get; set; }

    /** Who may connect to shared devices: "LocalSubnet", "*", or a list like "10.0.20.0/24,100.64.0.0/10". */
    public string ShareAccess { get; set; } = "LocalSubnet";

    /** Last custom list typed by the user (kept when switching modes). */
    public string ShareAccessCustom { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UsbIp", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // corrupt file: start fresh
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // non-fatal
        }
    }
}

/** "Start with Windows" via the per-user Run key (no admin rights needed). */
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UsbIp";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
