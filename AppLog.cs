using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace UsbIpClient;

/**
 * App log: daily files in %LOCALAPPDATA%\UsbIp\logs (usbip-YYYY-MM-DD.log, kept for 14 days).
 * "Open logs" also exports recent usbipd-win (sharing service) events from the Windows Event Log
 * next to them, so one folder holds everything someone needs to look at.
 */
public static class AppLog
{
    private const int KeepDays = 14;
    private static readonly object Gate = new();
    private static bool _pruned;

    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UsbIp", "logs");

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? error = null) => Write("WARN ", message, error);
    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    public static void Startup(string mode)
    {
        var version = (System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(AppLog).Assembly)
            ?.InformationalVersion ?? "?").Split('+')[0];
        Info($"---- USB/IP {version} started ({mode}) on {Environment.MachineName}, " +
             $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, " +
             $"usbip-win2 {UsbIpCli.Version ?? "not installed"}, usbipd-win {(Prerequisites.Server.IsInstalled ? "installed" : "not installed")}");
    }

    private static void Write(string level, string message, Exception? error)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ').Append(level).Append(' ')
            .Append(message.ReplaceLineEndings(" | "));
        if (error != null) line.AppendLine().Append("    ").Append(error.ToString().ReplaceLineEndings(Environment.NewLine + "    "));
        line.AppendLine();
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                if (!_pruned) { _pruned = true; Prune(); }
                File.AppendAllText(Path.Combine(Dir, $"usbip-{DateTime.Now:yyyy-MM-dd}.log"), line.ToString());
            }
        }
        catch
        {
            // Logging must never break the app (e.g. disk full, folder locked).
        }
        Debug.Write(line.ToString());
    }

    private static void Prune()
    {
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var f in new DirectoryInfo(Dir).GetFiles("usbip-*.log").Where(f => f.LastWriteTime < cutoff))
        {
            try { f.Delete(); } catch { /* in use */ }
        }
    }

    /** Writes usbipd-events.txt and opens the logs folder in Explorer with today's log selected. */
    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            ExportUsbipdEvents(Path.Combine(Dir, "usbipd-events.txt"));
        }
        catch (Exception e)
        {
            Warn("couldn't export usbipd events", e);
        }
        var today = Path.Combine(Dir, $"usbip-{DateTime.Now:yyyy-MM-dd}.log");
        var args = File.Exists(today) ? $"/select,\"{today}\"" : $"\"{Dir}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    /** The sharing service (usbipd-win) logs to Event Viewer → Windows Logs → Application, source "usbipd". */
    private static void ExportUsbipdEvents(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"usbipd-win events from the Windows Application log (newest first), exported {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Live view: Event Viewer → Windows Logs → Application, filter by source \"usbipd\".");
        sb.AppendLine();
        var query = new EventLogQuery("Application", PathType.LogName, "*[System[Provider[@Name='usbipd']]]")
        {
            ReverseDirection = true,
        };
        var count = 0;
        try
        {
            using var reader = new EventLogReader(query);
            for (var ev = reader.ReadEvent(); ev != null && count < 500; ev = reader.ReadEvent(), count++)
            {
                using (ev)
                {
                    string text;
                    try { text = ev.FormatDescription() ?? string.Join(" ", ev.Properties.Select(p => p.Value)); }
                    catch { text = string.Join(" ", ev.Properties.Select(p => p.Value)); }
                    sb.Append(ev.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss")).Append(' ')
                      .Append((ev.LevelDisplayName ?? ev.Level?.ToString() ?? "").PadRight(12))
                      .AppendLine(text.ReplaceLineEndings(Environment.NewLine + "    "));
                }
            }
        }
        catch (EventLogException e)
        {
            sb.AppendLine($"Couldn't read the event log: {e.Message}");
        }
        if (count == 0) sb.AppendLine("(no usbipd events; usbipd-win may not be installed)");
        File.WriteAllText(path, sb.ToString());
    }
}
