using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace UsbIpClient;

/**
 * Per-user install with no admin rights. The first run of a downloaded exe copies it to
 * %LOCALAPPDATA%\Programs\UsbIp, adds a Start menu shortcut and an "Installed apps" entry, and relaunches from there.
 */
public static class SelfInstall
{
    public const string AppName = "USB/IP";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\UsbIp";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "UsbIp");

    public static string InstalledExe => Path.Combine(InstallDir, "UsbIp.exe");

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "USB-IP.lnk");

    public static bool IsRunningInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /** Copies this exe into place (replacing an older version) and registers it. */
    public static void Install()
    {
        Directory.CreateDirectory(InstallDir);
        // A running copy can't be overwritten, but it can be renamed out of the way.
        if (File.Exists(InstalledExe))
        {
            var old = InstalledExe + ".old";
            try { File.Delete(old); } catch { /* still in use from a previous update */ }
            try { File.Move(InstalledExe, old, overwrite: true); } catch { /* fall through to the copy */ }
        }
        File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
        CreateShortcut();

        using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var version = FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion ?? "";
        k.SetValue("DisplayName", AppName);
        k.SetValue("DisplayVersion", version.Split('+')[0]);
        k.SetValue("Publisher", "kruchone");
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
    }

    /** Removes the app (not the drivers, which have their own entries in Installed apps). */
    public static void Uninstall()
    {
        Startup.Enabled = false;
        try { File.Delete(ShortcutPath); } catch { }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        // The exe can't delete itself while running; remove the folder once this process has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static void CreateShortcut()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = InstalledExe;
        link.WorkingDirectory = InstallDir;
        link.Description = "Use and share USB devices over the network";
        link.Save();
    }
}
