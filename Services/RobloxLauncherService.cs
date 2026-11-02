using System.Diagnostics;

namespace RobloxMultiLauncher.Services;

public sealed record LaunchResult(bool Success, int? Pid, string Message, bool ClosedOtherInstance = false);

/// <summary>
/// Starts an official Roblox client for one profile and then VERIFIES the outcome by observing real processes
/// and windows. "Success" is only returned when a new Roblox window appeared and survived a stability check.
/// </summary>
public sealed class RobloxLauncherService
{
    private readonly RobloxAuthService _auth;
    private readonly SingletonGuard _guard;

    public RobloxLauncherService(RobloxAuthService auth, SingletonGuard guard)
    {
        _auth = auth;
        _guard = guard;
    }

    public async Task<LaunchResult> LaunchAsync(
        Models.AppSettings settings, string cookie, long placeId, string? jobId,
        IReadOnlyCollection<int> trackedPids, IProgress<string> progress, CancellationToken ct)
    {
        var exe = RobloxLocator.Resolve(settings.RobloxPath);
        if (exe == null)
            return Fail("RobloxPlayerBeta.exe was not found. Set the Roblox path in Settings (usually %LOCALAPPDATA%\\Roblox\\Versions\\version-…).");

        progress.Report("Checking for existing Roblox processes…");
        var before = RobloxProcesses.GetPids();
        var outside = before.Where(p => !trackedPids.Contains(p)).ToList();

        if (!settings.AttemptMultiInstance)
        {
            if (before.Count > 0)
                return Fail("Multi-instance is switched off in Settings and a Roblox client is already running. Close it or enable the multi-instance attempt.");
        }
        else
        {
            if (outside.Count > 0)
            {
                var cmds = await Task.Run(RobloxProcesses.GetCommandLines, ct);
                bool tray = outside.Any(p => cmds.TryGetValue(p, out var c) && RobloxProcesses.IsTrayProcess(c));
                return Fail(tray
                    ? $"A Roblox background (tray) process is running (PID {string.Join(", ", outside)}). It already owns the single-instance objects, so a second client cannot be started reliably. Use Diagnostics → \"Close Roblox processes not started by this launcher\", and disable Roblox's start-with-Windows option."
                    : $"Roblox is already running outside this launcher (PID {string.Join(", ", outside)}). It owns the single-instance objects. Close it, then launch from here.");
            }
            if (!_guard.IsClaimed && !_guard.TryClaim())
                return Fail("Could not claim the Roblox single-instance objects (another process owns them). Multi-instance is not possible right now.");
        }

        progress.Report("Requesting authentication ticket…");
        string ticket = await _auth.GetAuthTicketAsync(cookie, ct);

        long tracker = Random.Shared.NextInt64(100_000_000_000, 999_999_999_999);
        long launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string url = string.IsNullOrWhiteSpace(jobId)
            ? $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId={tracker}&placeId={placeId}&isPlayTogetherGame=false"
            : $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGameJob&browserTrackerId={tracker}&placeId={placeId}&gameId={Uri.EscapeDataString(jobId.Trim())}&isPlayTogetherGame=false";
        string args = $"--app -t {ticket} -j \"{url}\" -b {tracker} --launchtime={launchTime} --rloc en_us --gloc en_us";

        progress.Report("Starting Roblox…");
        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = args,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            return Fail($"Windows could not start Roblox: {ex.Message}");
        }

        progress.Report("Waiting for the Roblox window…");
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(settings.LaunchTimeoutSeconds, 15, 300));
        int? newPid = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var fresh = RobloxProcesses.GetPids().Except(before).ToList();
            newPid = fresh.FirstOrDefault(p => WindowManager.FindMainWindow(p) != IntPtr.Zero);
            if (newPid is 0) newPid = null;
            if (newPid != null) break;
            if (fresh.Count == 0 && started != null && started.HasExited)
                return Fail($"Roblox exited immediately (exit code {started.ExitCode}) without opening a window. It may have refused to start a second instance, or the launch arguments are no longer accepted by this Roblox version.");
        }
        if (newPid == null)
            return Fail($"No new Roblox window appeared within {settings.LaunchTimeoutSeconds}s. Roblox may be blocked, still updating, or rejected the launch.");

        progress.Report("Verifying the client stays open…");
        await Task.Delay(8000, ct);
        if (!RobloxProcesses.IsAlive(newPid.Value))
            return Fail("Roblox opened a window but closed within a few seconds. This is typically single-instance enforcement, anti-cheat, or a failed join. Check Diagnostics and try launching with no other client running.");

        var lost = trackedPids.Where(p => !RobloxProcesses.IsAlive(p)).ToList();
        if (lost.Count > 0)
        {
            Logger.Warn($"Existing instance(s) {string.Join(", ", lost)} closed during this launch.");
            return new LaunchResult(true, newPid, "The new client is running, but Roblox closed an existing client during launch. Multi-instance is NOT working on this Roblox build.", true);
        }

        Logger.Info($"Launch verified: PID {newPid}");
        return new LaunchResult(true, newPid, "Running");
    }

    private static LaunchResult Fail(string message)
    {
        Logger.Warn("Launch failed: " + message);
        return new LaunchResult(false, null, message);
    }
}
