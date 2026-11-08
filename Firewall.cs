using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace UsbIpClient;

/**
 * Controls who may connect to the USB/IP server (usbipd-win) by editing the inbound firewall rule its installer
 * creates (named "usbipd", TCP 3240, LocalSubnet only by default). Reading works unelevated; changes run in an
 * elevated copy of this exe (`--set-firewall`), which validates the address list again.
 */
public static class Firewall
{
    public const string RuleName = "usbipd";
    public const string LocalSubnet = "LocalSubnet";
    public const string Any = "*";

    /** The rule's current RemoteAddresses (e.g. "LocalSubnet", "*", "10.0.20.0/255.255.255.0"), or null if not found. */
    public static string? GetRemoteAddresses()
    {
        try
        {
            foreach (var rule in Rules())
                if (string.Equals((string)rule.Name, RuleName, StringComparison.OrdinalIgnoreCase))
                    return (string)rule.RemoteAddresses;
        }
        catch
        {
            // COM unavailable
        }
        return null;
    }

    /** Validates and normalizes user input like "10.0.20.0/24, 100.64.0.0/10, 192.168.1.5". */
    public static bool TryNormalize(string input, out string normalized, out string? error)
    {
        normalized = "";
        error = null;
        var parts = input.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Enter at least one address or network.";
            return false;
        }
        var ok = new List<string>();
        foreach (var raw in parts)
        {
            var p = raw.Trim();
            if (p == Any || p.Equals("any", StringComparison.OrdinalIgnoreCase)) { normalized = Any; return true; }
            if (p.Equals(LocalSubnet, StringComparison.OrdinalIgnoreCase)) { ok.Add(LocalSubnet); continue; }
            if (IsAddress(p) || IsCidr(p) || IsRange(p)) { ok.Add(p); continue; }
            error = $"\"{p}\" isn't an IP address, network (e.g. 10.0.20.0/24) or range (e.g. 10.0.20.10-10.0.20.50).";
            return false;
        }
        normalized = string.Join(",", ok.Distinct(StringComparer.OrdinalIgnoreCase));
        return true;
    }

    /** Applies the address list (shows a UAC prompt). Returns null on success. */
    public static async Task<string?> SetRemoteAddressesAsync(string addresses)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList = { "--set-firewall", addresses },
        };
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode switch
            {
                0 => null,
                3 => "The usbipd firewall rule wasn't found. Reinstall usbipd-win.",
                _ => $"Couldn't update the firewall rule (code {p.ExitCode}).",
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "Cancelled at the Windows permission prompt.";
        }
    }

    /** Elevated entry point. */
    public static int ApplyElevated(string addresses)
    {
        if (!TryNormalize(addresses, out var normalized, out _)) return 2;
        var found = false;
        foreach (var rule in Rules())
        {
            if (!string.Equals((string)rule.Name, RuleName, StringComparison.OrdinalIgnoreCase)) continue;
            rule.RemoteAddresses = normalized;
            found = true;
        }
        return found ? 0 : 3;
    }

    /** Human-readable form of a RemoteAddresses value. */
    public static string Describe(string? remote) => remote switch
    {
        null => "unknown (firewall rule not found)",
        Any => "anyone who can reach this PC",
        _ => string.Join(", ", remote.Split(',').Select(a =>
            a.Equals(LocalSubnet, StringComparison.OrdinalIgnoreCase) ? "this PC's network" : ToCidr(a))),
    };

    /** Whether the rule matches the addresses we last applied (order/format-insensitive). */
    public static bool Matches(string? current, string desired)
    {
        if (current == null) return false;
        static HashSet<string> Set(string s) => s.Split(',').Select(a => ToCidr(a.Trim()).ToLowerInvariant()).ToHashSet();
        return Set(current).SetEquals(Set(desired));
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<dynamic> Rules()
    {
        var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new InvalidOperationException("Windows Firewall API unavailable");
        dynamic policy = Activator.CreateInstance(type)!;
        foreach (dynamic rule in policy.Rules) yield return rule;
    }

    private static bool IsAddress(string s) => IPAddress.TryParse(s, out _);

    private static bool IsCidr(string s)
    {
        var i = s.IndexOf('/');
        if (i <= 0 || !IPAddress.TryParse(s[..i], out var ip)) return false;
        var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var suffix = s[(i + 1)..];
        return (int.TryParse(suffix, out var bits) && bits >= 0 && bits <= max) || IPAddress.TryParse(suffix, out _);
    }

    private static bool IsRange(string s)
    {
        var i = s.IndexOf('-');
        return i > 0 && IPAddress.TryParse(s[..i], out _) && IPAddress.TryParse(s[(i + 1)..], out _);
    }

    /** The firewall API reports networks as "a.b.c.d/255.255.255.0"; show them as CIDR. */
    private static string ToCidr(string a)
    {
        var i = a.IndexOf('/');
        var suffix = i > 0 ? a[(i + 1)..] : "";
        // Only dotted masks need converting; IPAddress.TryParse would also accept a prefix length like "16".
        if (!suffix.Contains('.') || !IPAddress.TryParse(suffix, out var mask)) return a;
        var bits = mask.GetAddressBytes().Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
        return $"{a[..i]}/{bits}";
    }
}
