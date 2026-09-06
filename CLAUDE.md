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
   Mac that owns the actual serial connection. Lives at `M:\FTX1ControllerWin`
   (SMB share back to the Mac). **This app's UI (`MainWindow.xaml`, `Controls/`,
   `Converters/`, `Models/`, `Settings/`, `ViewModels/`) was copied wholesale into
   this repo as the starting point** — same look/behavior, but it currently still
   talks to a bridge, not the radio.
3. **FTX1WinController** (this repo) — same UI as #2, but the goal is to replace
   `Bridge/BridgeClient.cs` and the bridge-calling code in
   `ViewModels/MainViewModel.cs` with direct serial CAT I/O, porting the protocol
   logic from the Mac app (#1) instead of going through #2's bridge.

The Mac app's source is cloned **read-only** at `C:\Users\maswe\FTX1Controller-MacRef`
for reference — never edit or commit anything there.

## Current status

- Project scaffolded via `dotnet new wpf` (net8.0-windows), building cleanly.
- Full UI/ViewModel/Model layer copied from `M:\FTX1ControllerWin` and
  namespace-renamed to `FTX1WinController`. The app still assumes a bridge
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
- **Hardware-tested across three rounds so far**, all 2026-09-06. The FTX-1's
  USB-C cable is physically connected to this Windows laptop; `dotnet
  build`/`dotnet test` (47 tests) both pass. Round 1 (build 13:29) found the
  issues fixed above the Monitor-crash entry, plus the ANT TUNE no-response
  report. Round 2 (build 21:24) found and fixed the Monitor crash, and
  hardware-confirmed the audio features. Round 3 fixed and hardware-confirmed
  ANT TUNE — see "Resolved" below.

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

## Planned next steps (in order)

1. ~~Port the CAT protocol layer from the Mac app's Swift source to C#~~ — done.
2. ~~Wire it into real serial I/O~~ — done, see "Current status" above.
3. ~~Build Live Monitor / RECORD using Windows audio APIs~~ — done, see above.
4. **Hardware verification, round 2+**: work through remaining issues as the
   user finds them. MONITOR passthrough, RECORD-to-file/PLAY, and ANT TUNE
   are now hardware-confirmed (see "Current status" and "Resolved" above);
   still need every other FUNC-grid control, Scan/Split, presets, and the
   audio device picker covered against the real radio.

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

GitHub: https://github.com/iTracMan/FTX1WinController.git (private for now,
MIT license planned once ready for public release — code signing/SmartScreen
and install docs for non-technical users still unresolved).
