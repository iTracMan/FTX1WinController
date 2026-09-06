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
- **Not yet wired**: Live Monitor / RECORD-to-file / PLAY / the recordings
  list. These were always Mac-local-USB-audio features with no CAT command
  behind them at all (unlike SD-card recording via `LM1`/`SdRecording`,
  which *is* real CAT and fully wired) — they're stubbed to inert
  no-ops/a clear `LastError` message in `MainViewModel.cs` until the
  Windows-audio step below happens. `AntennaTuner`'s momentary "start
  tuning" pulse (`AC` command's P3=2) and the plain Tuner on/off toggle
  (P3=0/1) are wired per the Yaesu manual's own "on/off/start" field
  ordering, but not yet independently hardware-confirmed.
- **Hardware-untested.** The FTX-1's USB-C cable is physically connected to
  this Windows laptop. `dotnet build`/`dotnet test` (47 tests) both pass, but
  no real COM port has been opened yet — that verification is the user's to do.

## Planned next steps (in order)

1. ~~Port the CAT protocol layer from the Mac app's Swift source to C#~~ — done.
2. ~~Wire it into real serial I/O~~ — done, see "Current status" above.
   **What's left here**: hardware verification. Connect to the real FTX-1
   (pick CAT-1/CAT-2 COM ports + baud in the connection popup), confirm
   frequency/mode/PTT/meters and the FUNC-grid controls actually work
   against the radio, and fix whatever the Mac→Windows port got wrong (the
   Antenna Tuner P3=2 "start" guess above is the most likely candidate).
3. Build Live Monitor / RECORD using Windows audio APIs (NAudio or Core Audio),
   now that the radio's USB audio interface is confirmed visible to Windows.
   FTX1ControllerWin has no equivalent (its bridge only carries text, not audio),
   so this is new work, not a port.

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
