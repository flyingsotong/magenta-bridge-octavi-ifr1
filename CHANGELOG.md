# Changelog

All notable changes to Magenta Bridge for Octavi IFR-1. This project has no vendor code and no
MobiFlight dependency; the entries below are about what the panel can actually do.

## 0.2.3 - 2026-11-12

Written for someone who has never used a flight simulator before.

- **The version is now the full number** in the header and in Settings - \0.2.3\, not \0.2\.
- **The add-on is called what it is:** *the GPS add-on*. The card is headed *ONE-TIME SETUP - THE
  GPS BUTTONS* and says, in plain words, that the GNS530/430 has buttons only something inside
  the simulator can press, that it is 15 KB and already inside the app, and that nothing is
  downloaded.
- **After installing, it says what to do next**: quit MSFS completely rather than return to the
  menu, start it again, then press FMS1 and Direct To. Removing it later is deleting that folder,
  which the Diagnostics line explains on hover.
- **The welcome card is a three-line start-up list**: plug in the panel, do the one-time add-on
  step, test the panel before flying.
- Diagnostics says \GPS add-on: installed and ready\ or, in warning colour, that the GPS buttons
  will do nothing, with the install link underneath.
- README: the section is now *The GPS add-on (the WASM module)* and opens with what a WASM
  module is in one paragraph, without assuming the reader knows.
## 0.2.2 - 2026-11-12

- The FMS module's status is now **always visible**, in Diagnostics next to the resolved
  SimConnect client: `FMS module: installed 0.1.0 at D:\...\Community\magenta-bridge-module`, or
  a warning saying FMS1/FMS2 are inert, with a one-click **Install the FMS module** link beneath.
  Previously the only indication was the first-run card, which disappears once the module is
  installed - so there was no way to confirm it from inside the app.
- The status is cached for three seconds; it is read on every window refresh and would otherwise
  hit the Community folder dozens of times a second.
- Self-test: 49 checks, up from 47.
## 0.2.1 - 2026-11-12

- After installing the FMS module the app now says to **restart MSFS**. It previously said
  nothing, and since the simulator reads the Community folder only at startup, FMS1 and FMS2
  stayed inert until the next launch - which read as the app being broken.
- README: the same step is spelled out in *Run it* and in *The FMS module*.
## 0.2.0 — 2026-11-11

**FMS1 and FMS2 work.** The four GNS knobs and the CDI/OBS/MSG/FPL/VNAV/PROC/ENT/MENU/CLR/Direct
To buttons now drive the gauge, with no MobiFlight anywhere.

- Adds a 15 KB WASM module that the app installs for you. It receives an event name over the
  MSFS CommBus and fires the gauge's own `(>H:...)` event. Everything else in the app works
  whether or not the module is installed.
- The module is embedded in the executable: a card in the window offers **Install the module**
  when it is missing, and `--module status | install | remove` does the same from a console.
- **Range zoom** now follows the manual (§5.3.4): hold the toggle button (`<->`) and turn a
  rotary knob. Buttons can be *held*, not just latched, which is what that gesture needs.
- **SimConnect client discovery** sweeps fixed drives for `Xbox`/`XboxGames` instead of
  hard-coding `D:\XboxGames` and `C:\XboxGames`, so a moved or drive-letter-changed install is
  still found. The previous v0.1.0 download could fail to find the client at all on a modern
  install.
- **Config version 8.** Regenerating across a version bump now keeps theme, start-with-Windows,
  always-on-top, the pinned device and logging instead of resetting them.
- Self-test grows from 36 to 47 checks: every FMS binding routes through the module, no
  MobiFlight name survives anywhere, the module is embedded, a version bump keeps your
  preferences, and the hold-to-range gesture behaves.

### Notes for anyone building a module for this simulator

Two things cost most of an evening and are worth writing down:

1. `module_init` and `module_deinit` must return **void** (`()->()`). Exported as `bool` the
   module compiles, and the simulator then never schedules it.
2. The module must export the runtime set the loader expects (`__wasm_call_ctors`,
   `__wasm_call_patched_ctors`, `__indirect_function_table`, `malloc`, `free`, `mallinfo`,
   `mchunkit_begin`, `mchunkit_next`, `get_pages_state`, `mark_decommit_pages`), which means
   linking the WASI reactor CRT and libc, not a bare `--no-entry` stub.

DevMode → Debug → **WASM Debug**, Module Section, reports READY or FAILED per module; that
window is what turned the problem from guesswork into a fix.

## 0.1.0 — 2026-10-20

First public release.

- COM1/COM2 tuning and standby swaps, NAV1/NAV2 tuning with accumulating standby frequency
  (the vendor plug-in re-sends the value it just read, so its NAV knobs cannot move).
- HDG, BARO, CRS1/CRS2, AP target altitude, AP master and six AP buttons.
- Squawk, IDENT, and transponder mode OFF/SBY/TST/ON/ALT, which the vendor client does not
  implement.
- FMS CDI via `TOGGLE_GPS_DRIVES_NAV1`, encoder acceleration, a guided panel self-test,
  dark and light themes, always-on-top, start with Windows, a live radio stack, and the name
  of the loaded aircraft.
- MIT, no telemetry, no third-party package references.
