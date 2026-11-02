using System.Threading;

namespace RobloxMultiLauncher.Services;

/// <summary>
/// Creates the two named kernel objects the Roblox client uses as its "already running" signal, inside
/// THIS process, before any client starts. It never opens, injects into, or modifies a Roblox process.
/// If a Roblox process (including the background tray process) already owns the objects, the claim fails
/// and we report that honestly instead of pretending multi-instance works.
/// The event object is experimental: whether claiming it matters on current builds is untested.
/// </summary>
public sealed class SingletonGuard : IDisposable
{
    private const string MutexName = "ROBLOX_singletonMutex";
    private const string EventName = "ROBLOX_singletonEvent";

    private Mutex? _mutex;
    private EventWaitHandle? _event;

    public bool MutexHeld { get; private set; }
    public bool EventHeld { get; private set; }
    public bool IsClaimed => MutexHeld && EventHeld;

    public bool TryClaim()
    {
        Release();
        try
        {
            _mutex = new Mutex(true, MutexName, out bool created);
            MutexHeld = created;
            if (!created) { _mutex.Dispose(); _mutex = null; }
        }
        catch (Exception ex) { Logger.Warn($"Could not create {MutexName}: {ex.Message}"); }

        try
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out bool created);
            EventHeld = created;
            if (!created) { _event.Dispose(); _event = null; }
        }
        catch (Exception ex) { Logger.Warn($"Could not create {EventName}: {ex.Message}"); }

        Logger.Info($"Singleton claim: mutex={(MutexHeld ? "claimed" : "owned elsewhere")}, event={(EventHeld ? "claimed" : "owned elsewhere")}");
        return IsClaimed;
    }

    public void Release()
    {
        _mutex?.Dispose(); _mutex = null; MutexHeld = false;
        _event?.Dispose(); _event = null; EventHeld = false;
    }

    public void Dispose() => Release();
}
