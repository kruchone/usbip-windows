using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace UsbIpClient;

/** A driver package the app depends on, pinned to a release and its SHA-256 (same values as winget's manifests). */
public sealed record Component(
    string Name,
    string Purpose,
    string ExePath,
    string UrlX64, string Sha256X64,
    string UrlArm64, string Sha256Arm64)
{
    public bool IsInstalled => File.Exists(ExePath);
    public bool IsMsi => UrlX64.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

    public (string url, string sha) ForThisMachine =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? (UrlArm64, Sha256Arm64) : (UrlX64, Sha256X64);
}

/**
 * Installs the two signed open-source drivers this app builds on:
 * usbip-win2 (client: virtual USB host controller) and usbipd-win (server: shares local devices).
 * Downloads happen unelevated; one elevated copy of this exe (`--run-installers`) then runs them all, so the
 * user sees a single UAC prompt. The elevated side re-checks every hash against the pinned table.
 */
public static class Prerequisites
{
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static readonly Component Client = new(
        "usbip-win2", "attach USB devices shared by other computers",
        Path.Combine(ProgramFiles, "USBip", "usbip.exe"),
        "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.8/USBip-0.9.7.8-x64.exe",
        "44451fe06f4186125c2a5ecd25b099c5560a61a60b1e56f5a0758e77a60afa44",
        "https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.7.8/USBip-0.9.7.8-arm64.exe",
        "0fda9d3fc19e31753f57fcf8e3401939c41e259964a960b101f848c89fc19f19");

    public static readonly Component Server = new(
        "usbipd-win", "share this PC's USB devices",
        Path.Combine(ProgramFiles, "usbipd-win", "usbipd.exe"),
        "https://github.com/dorssel/usbipd-win/releases/download/v5.3.0/usbipd-win_5.3.0_x64.msi",
        "1c984914aec944de19b64eff232421439629699f8138e3ddc29301175bc6d938",
        "https://github.com/dorssel/usbipd-win/releases/download/v5.3.0/usbipd-win_5.3.0_arm64.msi",
        "efd7c4eb99b144c1623e616064a7b262f83d0994b0d7fde16c95d4b07528b24d");

    public static IReadOnlyList<Component> All { get; } = new[] { Client, Server };

    /** Downloads and installs the given components; returns null on success or an error message. */
    public static async Task<string?> InstallAsync(IEnumerable<Component> components, IProgress<string> progress, CancellationToken ct = default)
    {
        var missing = components.Where(c => !c.IsInstalled).ToList();
        if (missing.Count == 0) return null;

        var dir = Path.Combine(Path.GetTempPath(), "UsbIpSetup");
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("UsbIpClient/1.0");
            foreach (var c in missing)
            {
                var (url, sha) = c.ForThisMachine;
                var file = Path.Combine(dir, Path.GetFileName(new Uri(url).LocalPath));
                if (!File.Exists(file) || !HashMatches(file, sha))
                {
                    progress.Report($"Downloading {c.Name}…");
                    try
                    {
                        await using var src = await http.GetStreamAsync(url, ct);
                        await using var dst = File.Create(file);
                        await src.CopyToAsync(dst, ct);
                    }
                    catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
                    {
                        return $"Couldn't download {c.Name}: {e.Message}";
                    }
                }
                if (!HashMatches(file, sha)) return $"The {c.Name} download didn't match its expected checksum.";
                files.Add(file);
            }
        }

        progress.Report(missing.Count == 1
            ? $"Installing {missing[0].Name}… approve the Windows prompt."
            : $"Installing {string.Join(" and ", missing.Select(c => c.Name))}… approve the Windows prompt.");
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("--run-installers");
        foreach (var f in files) psi.ArgumentList.Add(f);
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 && missing.Any(c => !c.IsInstalled))
                return $"Installation failed (code {p.ExitCode}).";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return "Installation was cancelled at the Windows permission prompt.";
        }

        var still = missing.Where(c => !c.IsInstalled).Select(c => c.Name).ToList();
        return still.Count == 0 ? null : $"{string.Join(", ", still)} didn't finish installing. A restart may be needed.";
    }

    /** Elevated entry point: runs each installer silently. Only files matching a pinned hash are executed. */
    public static int RunInstallers(IEnumerable<string> files)
    {
        var pinned = All.SelectMany(c => new[] { c.Sha256X64, c.Sha256Arm64 }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file) || !pinned.Contains(Sha256(file)))
            {
                AppLog.Error($"installer {file} missing or hash mismatch; not running it");
                return 2;
            }
            var psi = file.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec.exe") { ArgumentList = { "/i", file, "/qn", "/norestart" } }
                : new ProcessStartInfo(file) { ArgumentList = { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" } };
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            AppLog.Info($"installer {Path.GetFileName(file)} -> exit {p.ExitCode}");
            // 3010 = success, reboot required (MSI).
            if (p.ExitCode != 0 && p.ExitCode != 3010) result = p.ExitCode;
        }
        return result;
    }

    private static bool HashMatches(string file, string sha) =>
        string.Equals(Sha256(file), sha, StringComparison.OrdinalIgnoreCase);

    private static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s));
    }
}
