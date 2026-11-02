using System.Diagnostics;
using System.Management;

namespace RobloxMultiLauncher.Services;

public static class RobloxProcesses
{
    public const string ProcessName = "RobloxPlayerBeta";

    public static HashSet<int> GetPids()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            using (p)
            {
                try { set.Add(p.Id); }
                catch (InvalidOperationException) { }
                catch (ArgumentException) { }
            }
        }
        return set;
    }

    /// <summary>Command lines via WMI (used to recognise the background "--launch-to-tray" process). Can be slow.</summary>
    public static Dictionary<int, string?> GetCommandLines()
    {
        var map = new Dictionary<int, string?>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'RobloxPlayerBeta.exe'");
            foreach (ManagementObject o in searcher.Get())
            {
                map[Convert.ToInt32(o["ProcessId"])] = o["CommandLine"] as string;
                o.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not read Roblox command lines (WMI): {ex.Message}");
        }
        return map;
    }

    public static bool IsTrayProcess(string? commandLine) =>
        commandLine?.Contains("launch-to-tray", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
}
