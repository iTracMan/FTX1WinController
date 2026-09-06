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

## Current status

- Project scaffolded via `dotnet new wpf` (net8.0-windows), building cleanly.
- Full UI/ViewModel/Model layer copied from `M:\FTX1ControllerWin` and
  namespace-renamed to `FTX1WinController`. This gives a working visual starting
  point but the app currently still assumes a bridge connection
  (`Bridge/BridgeClient.cs`) — that's the next thing to replace, not something
  that already works standalone.
- The FTX-1's USB-C cable is now physically connected to this Windows laptop
  (moved from the Mac). Its USB audio interface already shows up in Windows'
  audio device list. CAT/serial connectivity has not been built or tested yet.

## Planned next steps (in order)

1. **Port the CAT protocol layer** from the Mac app's Swift source to C#: every
   command, its encoding, and the hardware-confirmed quirks below. This is a
   translation task and doesn't require the radio to be connected.
2. **Wire it into real serial I/O** via `System.IO.Ports.SerialPort` against the
   two virtual COM ports the FTX-1 exposes, replacing `BridgeClient` calls in
   `MainViewModel.cs`. This step needs the physical radio and hands-on testing
   by the user — Claude Code cannot see or interact with the hardware directly.
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
