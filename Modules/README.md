# The FMS module

`MagentaBridgeModule.wasm` is the piece that lets this app press the GNS530/430 bezel
buttons. It is not a dependency you install separately: it ships inside the executable as an
embedded resource, and the app writes it into the simulator's Community folder on request.

    Modules/
      MagentaBridgeModule.wasm   the built module, embedded in the exe
      src/MagentaBridgeModule.cpp  its source
      build-module.ps1             rebuilds the .wasm from that source

Rebuild after editing the source:

    .\Modules\build-module.ps1

Then rebuild the app, because the .wasm is embedded at compile time.

## Why it exists

The GNS530/430 knobs and softkeys are not sim events; they are the gauge's own events, and
only code running inside the simulator can fire them. The module receives an event name from
the app over the MSFS CommBus and runs `(>H:<name>)` through `execute_calculator_code`.

## What it needs from the sim

Two things that are easy to get wrong, both learned the hard way:

- `module_init` and `module_deinit` must return **void**. Exported as `bool` the module
  compiles and then is silently never scheduled.
- The module must export the runtime set (`__wasm_call_ctors`, `__wasm_call_patched_ctors`,
  `__indirect_function_table`, `malloc`, `free`, `mallinfo`, `mchunkit_begin`,
  `mchunkit_next`, `get_pages_state`, `mark_decommit_pages`), so it links the WASI reactor CRT
  and libc rather than a bare stub.

Diagnostics live in the simulator: DevMode -> Debug -> WASM Debug, Module Section, which says
READY or FAILED per module.
