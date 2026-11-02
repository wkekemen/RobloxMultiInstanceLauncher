# Roblox Multi-Instance Launcher (C# / WPF)

A standalone Windows launcher that manages several Roblox accounts and tries to run several official Roblox
clients side by side, **and tells you honestly whether that worked**.

> **Status:** builds successfully with the .NET SDK. Multi-instance behavior still
> require testing with Roblox on the target PC; see the checklist and known limits below.

## Download the source code

[Download all source code as a ZIP](https://github.com/wkekemen/RobloxMultiInstanceLauncher/archive/refs/heads/main.zip).
This is the project source, not a ready-to-run installer; see [Build](#build-visual-studio-2022) for instructions.

## What the app does

* **Accounts:** sign in through an embedded WebView2 page on roblox.com (the app never sees your password).
  Only the `.ROBLOSECURITY` session is kept, encrypted with Windows DPAPI (current user) in
  `%APPDATA%\RobloxMultiLauncher\profiles.json`. Never stored or logged in plaintext. Note that other programs
  running as the same Windows user can ask DPAPI to decrypt it too.
* **Launching:** requests a one-time auth ticket from Roblox, then starts the installed `RobloxPlayerBeta.exe`
  with the standard launch arguments. No Roblox files are modified; nothing is injected.
* **Honest verification:** a launch counts as successful only if a new Roblox window appears and stays alive for
  ~8 s. If an existing client dies during a launch, you get an explicit "multi-instance did NOT work" warning.
  The dashboard shows "VERIFIED" only while two launcher-started clients are actually alive together
  (and remembers it per Roblox version folder); otherwise it says "NOT VERIFIED".
* **Blockers:** detects Roblox processes started outside the launcher (including the tray process) and refuses
  to launch with an explanation; the launcher closes outside Roblox processes on startup, while Diagnostics can
  close them on demand with confirmation. Closing a running client disconnects its game.
* **Windows:** lists Roblox processes/windows with PID, memory, uptime and owner profile; Focus / Minimise / Close
  (graceful first, force-kill only after confirmation). Each successful launch arranges launcher-started windows
  into a compact grid on the selected monitor, with a configurable number of sessions per row. Live activity previews have a separately configurable 1–12 screens-per-row layout; they are view-only and hide when
  clipped by the activity scroll viewport. Tab in focuses the normal Roblox window for mouse and keyboard interaction.
* **Personalization:** change the background/panel/card/accent colors and choose a custom application badge image
  from Settings.
* **Macros:** explicitly start a global keyboard-and-mouse recording, pause/resume it, replay it, and save/load
  recordings from the Macros tab. Playback includes recorded key presses and mouse clicks with their timing and
  screen positions. Choose any positive playback count, or enter 0 to loop until stopped. Global
  Record/Play/Pause/Stop shortcuts can be reassigned there; defaults are
  Ctrl+Alt+F5/F6/F7/F8.
  Recordings are saved under `%APPDATA%\RobloxMultiLauncher\Macros`. Recording displays a warning first because
  all keyboard input and clicks are captured while it is active.
* **Resilience:** global exception handlers, rolling log (`launcher.log`), corrupt-JSON backup, timeouts.

## Build (Visual Studio 2022)

1. Install the **.NET 8 SDK** and Visual Studio 2022 with the ".NET desktop development" workload.
2. Install the Microsoft Edge **WebView2 Runtime** if missing (preinstalled on Windows 11).
3. Open `RobloxMultiInstanceLauncher.csproj` (File → Open → Project/Solution). NuGet packages restore automatically.
4. Press **F5** (Debug) or choose Build → Configuration **Release**.

Command line / single-file executable:

```
dotnet publish -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin\Release\net8.0-windows\win-x64\publish\RobloxMultiInstanceLauncher.exe` (needs the .NET 8 Desktop
Runtime; use `--self-contained true` to bundle it).

## Project layout

```
App.xaml(.cs)            theme, global crash handling, single-launcher guard
MainWindow.xaml(.cs)     dashboard / settings / diagnostics UI
Models/                  StoredProfile, AppSettings, MacroRecording
Services/                SecretProtector (DPAPI), ProfileStore, MacroService, RobloxLocator, RobloxProcesses (+WMI),
                         SingletonGuard, RobloxAuthService, RobloxLauncherService, WindowManager, Logger
Interop/                   user32 P/Invoke and DWM previews
ViewModels/              MainViewModel, ProfileViewModel, InstanceViewModel, commands
Views/LoginWindow        WebView2 sign-in
```

## Test checklist (do this before trusting anything)

1. Quit every Roblox window, **also the tray icon / background `RobloxPlayerBeta` in Task Manager**, and disable
   Roblox's start-with-Windows option. Start this launcher; the status bar should say the objects are claimed.
2. Add two different accounts. Enter a Place ID. Launch account A, wait for it to load in.
3. Launch account B. Expected outcomes:
   * both stay open > 20 s → dashboard says VERIFIED (multi-instance works on this build);
   * A closes or B exits → you get a failure/warning message (multi-instance is blocked on this build);
   * B never shows a window → timeout message.
4. Try Focus / Minimise / Close on each window row. In Live activity, use Tab in and confirm the normal Roblox
   window comes to the foreground for mouse and keyboard interaction.
5. Reboot with Roblox's tray autostart on and confirm the launcher reports the blocker instead of failing silently.
6. Confirm `profiles.json` contains only an encrypted `ProtectedCookie` string.

## Known limits / things most likely to need adjusting

* Roblox may change launch arguments, the auth-ticket endpoint, or singleton checks at any time.
* Microsoft Store (UWP) Roblox is not supported; use the standard installer version.
* Private-server link codes are not parsed; use a Place ID and optional server (job) ID.
* Whether claiming `ROBLOX_singletonEvent` has any effect is unknown (flagged experimental in the code).
