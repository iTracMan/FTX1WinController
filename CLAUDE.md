# FTX1WinController

Standalone Windows control app for the Yaesu FTX-1 transceiver. WPF/.NET, talks to
the radio directly over USB serial (`System.IO.Ports.SerialPort`) — no network,
no bridge, no dependency on any other machine being on.

## How this fits with the other two FTX-1 apps

There are three related codebases. This one is the newest and simplest in
architecture, but currently the least complete functionally:

1. **FTX1Controller** (macOS, native Swift/SwiftUI) — the mature, hardware-proven
   app. Talks to the radio directly over two virtual COM ports (CAT-1 enhanced
   for frequency/mode, CAT-2 standard for PTT, 38400 baud). Lives on the Mac at
   `~/FTX1Controller`. **This is the canonical reference for every CAT command,
   encoding, and hardware-confirmed quirk.** When in doubt about protocol
   behavior, the Swift source (and its 127-test regression suite) is authoritative
   over the Yaesu manual, which is wrong or silent on several points (see below).
2. **FTX1ControllerWin** (Windows, WPF) — a bridge client. Does not talk to the
   radio at all; sends text commands (`GET FREQ`, `SET FREQ <hz>`, ...) over TCP
   port 5150 to `FTX1Bridge`, a headless daemon running as a LaunchAgent on the
   Mac that owns the actual serial connection. Lives on the Mac, synced to this
   machine over a mapped network drive. **This app's UI (`MainWindow.xaml`, `Controls/`,
   `Converters/`, `Models/`, `Settings/`, `ViewModels/`) was copied wholesale into
   this repo as the starting point** — same look/behavior, but it currently still
   talks to a bridge, not the radio.
3. **FTX1WinController** (this repo) — same UI as #2, but the goal is to replace
   `Bridge/BridgeClient.cs` and the bridge-calling code in
   `ViewModels/MainViewModel.cs` with direct serial CAT I/O, porting the protocol
   logic from the Mac app (#1) instead of going through #2's bridge.

The Mac app's source is cloned **read-only** at `..\FTX1Controller-MacRef` (a sibling
directory to this repo) for reference — never edit or commit anything there.

## Current status

- Project scaffolded via `dotnet new wpf` (net8.0-windows), building cleanly.
- Full UI/ViewModel/Model layer copied from the Mac's `FTX1ControllerWin` bridge-client
  repo (synced via mapped network drive) and namespace-renamed to `FTX1WinController`.
  The app still assumes a bridge
  connection (`Bridge/BridgeClient.cs`, `ViewModels/MainViewModel.cs`) — that
  code is untouched so far.
- **The CAT protocol layer has been ported** into `src/FTX1WinController/Cat/`:
  `CatCommands` (+`.Dsp.cs`/`.Func.cs`/`.Vfo.cs` partials) is a faithful 1:1
  translation of the Mac app's `CATProtocol(V2/V3/V4).swift` — every command,
  encoding, and documented quirk, including the four below. `CatConnection`
  ports the Mac's half-duplex FIFO command queue (1.2s timeout, `;`-framing,
  40ms fire-and-forget pacing) against a new `ISerialTransport` abstraction
  (not `System.IO.Ports.SerialPort` directly, so this layer needs no real
  hardware to build or test). `RadioController` is the stateful facade
  (ported from `RadioController(V2/V3/V4).swift`) exposing async
  Set/Refresh methods and published state, including the trickier
  hardware-confirmed behaviors: the QMB-recall race guard, keyer auto-off on
  mode change, the 4-state clarifier cycle, and squelch/RF-gain display
  swap by mode. It deliberately does **not** run its own poll loop —
  `MainViewModel`'s existing `DispatcherTimer` loop will drive it once wired up.
  A new xUnit project (`tests/FTX1WinController.Tests`, 46 tests) covers the
  quirks and the connection's queueing/timeout/framing behavior with a fake
  transport. `dotnet build` and `dotnet test` both pass clean.
- **Wired into real serial I/O.** `WindowsSerialTransport` implements
  `ISerialTransport` over `System.IO.Ports.SerialPort` (8N1, no flow control,
  matching the Mac's confirmed settings; subscribes to the port's own
  `DataReceived` in its constructor, before `Open()` is ever called, so no
  byte can arrive before something's listening). `MainViewModel.cs` no
  longer references `BridgeClient` at all — it's deleted (`Bridge/` folder
  removed), along with `BridgeHost`/`BridgePort` (AppSettings and the
  connection popup's Host/Port fields). `SelectedCat1Port`/`SelectedCat2Port`
  now hold real Windows COM port names (e.g. `COM3`), populated by
  `RefreshPortsCommand` from `SerialPort.GetPortNames()` — no more asking a
  bridge for its port list. `ConnectAsync` builds `WindowsSerialTransport`
  instances and hands them to `RadioController.ConnectAsync`; a busy COM
  port surfaces as `UnauthorizedAccessException`, caught specifically to
  show the same "port busy" dialog the bridge version had (reworded — a Mac
  app can no longer hold a Windows COM port in this architecture).
  `IntSettingViewModel`/`ToggleSettingViewModel`/`ChoiceSettingViewModel<T>`
  (FUNC-grid sliders/toggles/choices) were re-pointed from generic
  `"GET <name>"/"SET <name> <value>"` bridge strings to typed delegates
  straight into `RadioController`'s Set/Refresh method pairs — the interface
  they implement was renamed `IBridgeSetting` → `ISettingBinding`
  (`BridgeName` → `Id`) since there's no bridge left to name anything after.
- **Live Monitor / RECORD-to-file / PLAY are now real, local Windows audio**
  — `src/FTX1WinController/Audio/`: `LiveAudioService` captures from the
  FTX-1's USB audio input device (NAudio's MME `WaveInEvent`, chosen over
  WASAPI for simplicity — this doesn't need WASAPI's lower latency) and
  either plays it live to this machine's *default* output device (MONITOR,
  formerly mislabeled "MAC SPEAKER") or writes it to a WAV file (RECORD's
  local half), independently or both at once, sharing one capture stream.
  `RecordingLibrary` persists each recording as a `.wav` + sidecar `.json`
  (frequency/mode/duration) under `%AppData%\FTX1WinController\Recordings`,
  listed/played back by `RecordingPlayer` for PLAY. The input device is
  looked up **by name**, not a cached index (indices aren't stable across
  USB reconnects) — picked once in Settings (`AvailableAudioInputDevices`/
  `SelectedAudioInputDevice`, persisted in `AppSettings.AudioInputDeviceName`).
  RECORD still separately drives the real SD-card CAT command
  (`LM1`/`SdRecording`) exactly as before — that's the radio's own storage,
  this app has no way to browse it over USB, and it's unrelated to the local
  WAV files PLAY lists (the two just happen to start/stop together).
- **Fixed from the first hardware test** (2026-09-06, build 13:29): the
  stale "BRIDGE (MAC)" label in the connection popup; the Mode grid used a
  `WrapPanel` (no fixed shape, uneven button sizes on shorter rows) —
  replaced with a `UniformGrid Rows="4" Columns="5"` plus
  `ModeButtonViewModel.Blank()` placeholders at the 3 positions the real
  radio's own mode-button layout leaves empty; and several bespoke
  `RelayCommand`s (`AntTuneCommand`, `DLevelUpCommand`/`DownCommand`,
  `RfPowerUpCommand`/`DownCommand`, `ZeroInCommand`, `MessageArmCommand`,
  `RecordCommand`, `RefreshRecordingsCommand`, `StopPlaybackCommand`,
  `ToggleAudioMonitorCommand`, `SetSubFrequencyCommand`) were missing from
  `RaiseCanExecuteChanged()`. That said: WPF's `CommandManager` requery is
  actually global (every `RelayCommand.CanExecuteChanged` delegates to the
  same static `CommandManager.RequerySuggested` event), so any one of the
  already-listed commands firing should have re-evaluated all of them too —
  this fix is correct hygiene but **probably isn't** why ANT TUNE specifically
  didn't respond. Still unresolved — see "Open question" below.
- **Fixed from the second hardware test** (2026-09-06, build 21:24): turning
  Live Monitor OFF (right after ON worked fine) crashed the whole process
  with **no trace anywhere** — not a WPF exception dialog, not a Windows
  Application-log/.NET Runtime fault entry, nothing — which pointed at either
  a native-level failure in NAudio's MME calls into the FTX-1's USB Audio
  Class driver, or an exception on one of NAudio's own background threads
  that nothing in the app was set up to catch. Added `CrashLogger` (plain
  text under `%AppData%\FTX1WinController\crash.log`) wired up in
  `App.xaml.cs` via `DispatcherUnhandledException`/
  `AppDomain.UnhandledException`/`TaskScheduler.UnobservedTaskException` —
  previously there was no exception handling anywhere in the app, so any
  future crash like this would otherwise still vanish without a trace.
  `LiveAudioService.StopMonitoring`/`StopCaptureIfIdle` now update the
  service's own state *before* calling into NAudio's `Stop()`/`Dispose()`,
  and catch+log exceptions from that teardown instead of letting NAudio's
  thread take the whole process down. Re-tested afterwards — 13 Monitor
  on/off cycles total (some scripted, some by hand) plus several rounds of
  RECORD/PLAY — all hardware-confirmed working, crash not reproduced again,
  nothing landed in `crash.log`. This is hardening of the most likely
  failure surface, not a confirmed root-cause fix — if it recurs,
  `crash.log`'s contents (or continued emptiness) will say whether it's
  catchable or still native-level.
- **Hardware-tested across five rounds so far**, all 2026-09-06/07. The FTX-1's
  USB-C cable is physically connected to this Windows laptop; `dotnet
  build`/`dotnet test` (47 tests) both pass. Round 1 (build 13:29) found the
  issues fixed above the Monitor-crash entry, plus the ANT TUNE no-response
  report. Round 2 (build 21:24) found and fixed the Monitor crash, and
  hardware-confirmed the audio features. Round 3 fixed and hardware-confirmed
  ANT TUNE — see "Resolved" below. Round 4 (build 2026-09-07 10:14)
  hardware-confirmed the Tuner ON/OFF toggle (`SetAntennaTunerAsync`, the
  same `AC` command as ANT TUNE but P3='0'/'1' — its correctness had already
  implicitly confirmed the AC command path was fine, which is what pointed
  at the P3 value specifically for the ANT TUNE bug), re-confirmed
  MONITOR/RECORD/PLAY still working, confirmed the Mode grid's `UniformGrid`
  sizing fix (all buttons uniform, no more short-row stretching), and
  confirmed Mode selection (`SetModeAsync`, `MD` command) and Band selection
  (`SetBandAsync`, `BS` command) both actually change the radio, not just
  the UI. Round 5 hardware-confirmed Presets, MESSAGES, and RECORD-to-file,
  found three bugs (Playing-on-Mac label, MONITOR/PLAY overlap, MONITOR
  surviving app shutdown), and after the fixes below, hardware-confirmed
  all three fixed too.

## Resolved: ANT TUNE now hardware-confirmed working

Was suspect 1 of the two live suspects from round 1: `MainViewModel.AntTuneAsync`
was sending `_radio.SetAntennaTunerAsync('2')` for "start tuning", guessed from
the manual's "on/off/start" field ordering for the `AC` command — never
independently confirmed. Checking the Mac app's canonical source
(`RadioControllerV3.startAntennaTuning()`) showed the real value is **`P3='3'`**;
`'2'` isn't a valid value for that field, so the radio silently ignored it (no
`LastError`, because the command reached the radio fine — it just did nothing
with it). Fixed by adding `RadioController.StartAntennaTuningAsync()` (P3='3',
doesn't touch `TunerOn` — matching the Mac's split between `setTunerOn` and
`startAntennaTuning`, since starting a tuning cycle isn't the same state as the
Tuner on/off toggle) and pointing `AntTuneAsync` at it instead. User confirmed
on hardware (2026-09-06) that ANT TUNE now does something on the radio.

- **Fixed from the fifth hardware test** (2026-09-07): Presets, MESSAGES
  record/playback, and RECORD-to-file all hardware-confirmed working with no
  issues. Three bugs found and fixed:
  1. The PLAY popup's "Playing on Mac" label (`MainWindow.xaml`, the PLAY
     LIST popover) — same leftover-bridge-label category as the earlier
     "BRIDGE (MAC)" fix — reworded to just "Playing".
  2. MONITOR and local-recording PLAY shared one capture stream into
     separate live-vs-file paths, but nothing stopped MONITOR while a
     recording played back, so both fed the speakers at once and overlapped
     audibly. `MainViewModel` now tracks `_monitorAutoPausedForPlayback`:
     `PlayRecordingAsync` pauses Monitor (if it was on) before playing and
     flags that it did so; the flag survives switching tracks mid-playback
     (`RecordingPlayer.Play` stops the previous track first, which fires
     the old track's "finished" callback *after* the new one has already
     started — `OnPlaybackFinished` checks `_player.IsPlaying` rather than
     assuming its own call means nothing is playing, so it only resumes
     Monitor once nothing is queued up); a manual MONITOR button press
     during playback clears the flag so it can't override the user's
     explicit choice once playback ends. If Monitor was already off when
     PLAY was pressed, nothing touches it — it stays off, exactly as
     specified.
  3. Live Monitor kept playing through the speakers after the app had
     fully closed. `Window.Closing` → `MainViewModel.Dispose()` →
     `LiveAudioService.StopMonitoring()` was already wired up, but NAudio's
     `WaveOutEvent.Stop()` only *signals* its background playback thread to
     wind down and close the native device handle — that happens on the
     thread's own time, not inside the `Stop()` call. On shutdown the
     process can exit and kill that thread before it gets there, leaving
     the USB Audio Class device open with the radio's audio still coming
     through. `StopMonitoring` now blocks (bounded to 500ms, so a wedged
     driver can't hang shutdown) on the `PlaybackStopped` event — which the
     thread raises right before it actually exits — before calling
     `Dispose()`, so the native handle is confirmed released before the
     method returns and the process is allowed to fully exit.
  All three hardware-confirmed fixed (2026-09-07): the label reads correctly,
  MONITOR auto-pauses/resumes cleanly around local playback with no overlap,
  and MONITOR no longer survives app shutdown.
- **Round 6** (2026-09-07): the rest of the FUNC-grid controls and Scan/Split
  all hardware-confirmed working, no issues found.
- **Round 7** (2026-09-07): the audio input device picker (Settings) also
  hardware-confirmed working — this was the last item on the checklist
  below, so every planned feature has now been verified against the real
  radio at least once.
- **S-meter tick placement recalibrated** (2026-09-07): the app's S-meter
  gauge (`Controls/RadioMeters.cs`, `RadioArcMeterView.SMeterTicks`) had S9
  positioned at fraction 0.64 along the sweep — eyeballed off the Mac app's
  own screenshot, not the FTX-1's real meter. User supplied a photo of the
  FTX-1's actual on-screen S-meter (a user-supplied reference photo) showing S9 landing at
  virtual center-sweep, not off to the right. Recalibrated to two evenly-
  spaced groups matching that photo: S1/3/5/7/9 evenly spaced from 0.08 to
  0.50 (center), then +20/+40/+60 evenly spaced from 0.50 out to 0.98; the
  white→blue arc color break (previously hardcoded to the old 0.64) now
  follows the same `SMeterCenterFraction` constant. Still just label
  *placement*, not a real raw-value calibration curve — there's still no
  way to feed the radio a known signal strength to measure the actual
  raw-value thresholds for each S-unit/dB-over-S9 step, unlike the power
  meter's real hardware-measured curve just above it in the same file.
  Hardware-confirmed (2026-09-07): user reports it's "as close as it ever
  needs to be" against the radio's own display.
- **Window made resizable via a wrapping Viewbox** (2026-09-07, ahead of
  public release — other people's monitors won't all fit the fixed
  1129x1019 window this app has used since scaffolding). `MainWindow.xaml`'s
  entire layout (previously inside a `ScrollViewer` with vertical scrolling
  and horizontal clipping) is now wrapped in a single `<Viewbox
  Stretch="Uniform">` in place of that `ScrollViewer`, and `ResizeMode`
  changed from `NoResize` to `CanResize`. This deliberately isn't a
  responsive redesign — every row is still Auto/fixed-width exactly as
  before, just uniformly scaled as a whole to fit whatever size the window
  becomes, keeping the current layout's shape and proportions intact rather
  than reflowing anything. `Height="1019" Width="1129"` on the `Window` are
  now just the layout's natural (1:1 scale) size, not a hard limit.
  Hardware-confirmed (2026-09-07): resized on an external monitor, moved
  back to the laptop's own display and resized there too — all functions
  still work and the overall look/proportions hold. Also confirmed with the
  laptop's Windows display scaling changed from 100% to 125%, no issues.
- **Window title now carries a version number** (2026-09-07): "G1INU v1 —
  build <timestamp>" (`MainWindow.xaml.cs`, `AppVersion` constant), where
  G1INU is the user's callsign. **Bump `AppVersion` by 1 every time a build
  goes out for the user to test** — plain hand-incremented integer, not
  tied to the .csproj/assembly version. The build timestamp stays alongside
  it rather than being replaced (per user request) — the version number is
  the human-readable "which release is this," the timestamp is still the
  at-a-glance stale-build check.
- **AM/AM-N added to the SQL-not-RF mode group** (2026-09-08): the radio's
  default [AF/RF/SQL]=AUTO menu behavior shows SQL instead of RF (on both
  the meter and the MAIN AF/RF/SQL knob) for AM/AM-N as well as the
  FM/D-FM/C4FM family already handled — `SquelchModeCodes` in both
  `RadioController.ModeShowsSquelchNotRf` and `MainViewModel`'s own copy
  (`ModeShowsSquelchNotRF`) were missing AM ('5') and AM-N ('D') entirely.
  Both sets updated in lockstep, same as the existing duplication between
  the two files. Hardware-confirmed (2026-09-08): AM mode now correctly
  shows SQL active.
- **MONITOR now respects SQL** (2026-09-08): the radio's own speaker was
  correctly silenced by squelch, but MONITOR (`LiveAudioService`) is a raw
  USB Audio Class passthrough with no built-in idea of squelch state, so it
  kept passing band noise to the PC speakers regardless. The FTX-1 exposes
  no CAT command for squelch open/closed state, so this is an
  approximation: `MainViewModel.RefreshMetersAsync` compares the
  already-polled `SMeterValue` (RF signal strength, `RM1`) against
  `Squelch` (the `SQ` threshold) and sets `LiveAudioService.MonitorMuted`
  accordingly — RECORD-to-file is deliberately unaffected, so recordings
  stay a complete capture regardless of squelch. Because SM (RF signal
  strength) and SQ aren't actually the same physical quantity — most Yaesu
  FM/AM squelch is audio-noise-derived, not RF-strength-derived — the raw
  comparison didn't line up with the radio's real crossover point on the
  first hardware test (MONITOR muted at SQL≈1, the radio's speaker didn't
  silence until SQL≈2). Added a user-tunable `MonitorSquelchTrim`
  (`AppSettings`, exposed in the connection Settings popup as "MONITOR
  SQUELCH TRIM," subtracted from `Squelch` before the comparison) so the
  gap can be corrected on the bench instead of guessed from source.
  Hardware-confirmed (2026-09-08) once the trim was adjusted: MONITOR now
  silences at the same SQL point as the radio's own speaker.

## Planned next steps (in order)

1. ~~Port the CAT protocol layer from the Mac app's Swift source to C#~~ — done.
2. ~~Wire it into real serial I/O~~ — done, see "Current status" above.
3. ~~Build Live Monitor / RECORD using Windows audio APIs~~ — done, see above.
4. ~~Hardware verification~~ — done, as of round 7 (2026-09-07). Every
   planned feature (MONITOR passthrough, RECORD-to-file/PLAY, ANT TUNE,
   Tuner ON/OFF, Mode grid sizing, Mode selection, Band selection, Presets,
   MESSAGES, the full FUNC-grid, Scan/Split, and the audio device picker) is
   hardware-confirmed working (see "Current status" and "Resolved" above).
   Future hardware issues the user finds should still be worked through as
   they come up, but there's no longer an open checklist of untested
   features.
5. ~~Publish a public release~~ — done, see "Repo / distribution" below.
   v1 is live as a self-contained single-file build with a working
   GitHub Release download; code signing/SmartScreen and the MIT license
   are the remaining open items.

## Hardware-confirmed CAT quirks (from the Mac app, NOT reliably in the manual)

- **Mic EQ Set command** uses inverted 0/1 encoding vs. the Answer's encoding.
- **TXW** is a toggle command, not momentary (manual wording implies momentary).
- **AMS** has no CAT command at all — front-panel only.
- **AGC** has a "sentinel" value in its CAT response needing special-case handling.

Don't re-derive these from the Yaesu manual — check the Mac app's Swift source
and its regression tests for the exact behavior.

## Working with hardware

The user tests every change against the real radio and reports results in both
directions (app→radio, radio→app). Don't claim a serial/CAT feature works until
it's been hardware-verified — building and running the app is not the same as
confirming radio behavior.

## Repo / distribution

GitHub: https://github.com/iTracMan/FTX1WinController.git — **repo is now
public**.

**Versioning: simple whole numbers only — v1, v2, v3, etc.** No semantic
versioning, no patch numbers (never "v1.0.1" or "v1.1"). Each GitHub Release
is the next whole number up from the last. This is a separate counter from
the in-window title's `AppVersion` build number (`MainWindow.xaml.cs`) — that
one bumps per test build handed to the user, independent of when a GitHub
Release actually gets cut.

**v1 released** (2026-09-07): built as self-contained/single-file
(`RuntimeIdentifier=win-x64`, `SelfContained=true`, `PublishSingleFile=true`
in `src/FTX1WinController/FTX1WinController.csproj`) via
`dotnet publish src/FTX1WinController/FTX1WinController.csproj -c Release`,
output at `src/FTX1WinController/bin/Release/net8.0-windows/win-x64/publish/`.
Not quite a true single file — WPF's native interop DLLs
(`D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`,
`vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`) can't be embedded by
`PublishSingleFile` and always land alongside the exe — so the release
artifact is that whole `publish/` folder zipped up (exe + those 5 DLLs +
`.pdb`, kept in deliberately for useful crash-log symbols), not the exe
alone. Fixed a regression this switch caused along the way: the window
title's build-timestamp fell back to "unknown build" under single-file
publish because `Assembly.GetExecutingAssembly().Location` returns `""` for
an embedded assembly — swapped to
`Process.GetCurrentProcess().MainModule.FileName` in `MainWindow.xaml.cs`.
No .NET Desktop Runtime install step needed for end users as a result.

**GitHub Release live, download confirmed working**:
https://github.com/iTracMan/FTX1WinController/releases/latest

Code signing/SmartScreen still unresolved — the unsigned exe will likely
trigger a SmartScreen warning on first run for end users; MIT license
still to be added to the repo.

### Changes merged to main since v1 (accumulating toward v2)

Keep this list updated as commits land — add an entry per merged
fix/feature, and clear the list back to empty right after cutting the v2
GitHub Release (moving its contents into the release notes instead).

- **AM/AM-N added to the SQL-not-RF mode group** (2026-09-08, commit
  `95ced2d`) — see "Current status" above for detail. Hardware-confirmed.
- **MONITOR now respects SQL** (2026-09-08) — see "Current status" above
  for detail. Hardware-confirmed, including the tuned `MonitorSquelchTrim`
  value.
- **Debug builds no longer self-contained/win-x64** (2026-09-08):
  `RuntimeIdentifier`/`SelfContained`/`PublishSingleFile` in
  `FTX1WinController.csproj` were in the top-level `<PropertyGroup>`, so
  they applied to every build, not just Release publish — Debug builds
  started landing in `bin\Debug\net8.0-windows\win-x64\` (RID-suffixed
  path) instead of the old `bin\Debug\net8.0-windows\`, and became
  self-contained (bundling the full .NET runtime) along the way. Moved
  those three properties into a `Condition="'$(Configuration)'=='Release'"`
  `<PropertyGroup>` so Debug goes back to the old plain, framework-dependent
  path/behavior, while `dotnet publish -c Release` still produces the same
  win-x64 self-contained single-file output v1 shipped with (verified:
  `bin\Release\net8.0-windows\win-x64\publish\` unchanged).
- **Redacted personal paths from the now-public repo** (2026-09-08): a full
  pass found no leaked credentials (current tree and full git history both
  checked — none), but `CLAUDE.md` and `Controls/RadioMeters.cs` referenced
  the developer's Windows username (`C:\Users\...\FTX1Controller-MacRef`)
  and mapped SMB drive letter (`M:\...`) in a few spots. Reworded to a
  relative sibling-directory path for the Mac reference repo (still
  navigable, no username) and generic descriptions for the SMB share and a
  one-off reference photo path.
