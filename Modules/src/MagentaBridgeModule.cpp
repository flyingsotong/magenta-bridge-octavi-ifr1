// Magenta Bridge module for MSFS 2024.
//
// Receives a gauge event name over the CommBus and fires it as calculator code -
// "(>H:AS430_DirectTo_Push)" - which is the mechanism the modules that already work in this
// sim use (execute_calculator_code). The names come from the GNS530/430's own JavaScript:
// DirectTo_Push, MENU_Push, CLR_Push, ENT_Push, OBS_Push, MSG_Push, FPL_Push, VNAV_Push,
// PROC_Push, CDI_Push, RightLargeKnob_Right and friends, prefixed AS530_ or AS430_.
//
// Entry points return nothing and the full runtime export set is linked in: the simulator
// compiles a module happily and then never schedules it unless both hold.
//
// Channel: "magenta.bridge", payload: a bare event name.

#include <MSFS/MSFS.h>
#include <MSFS/MSFS_CommBus.h>

extern "C" {
    __attribute__((import_module("env"), import_name("execute_calculator_code")))
    int execute_calculator_code(const char* code, double* fvalue, int* ivalue, const char** svalue);
}

namespace
{
    constexpr const char* kChannel = "magenta.bridge";
    constexpr unsigned int kMaxName = 64;
    constexpr unsigned int kMaxCode = 128;

    void run(const char* code)
    {
        execute_calculator_code(code, nullptr, nullptr, nullptr);
    }

    void callNamedEvent(const char* name)
    {
        char code[kMaxCode];
        unsigned int i = 0;

        for (const char* p = "(>H:"; *p != '\0' && i < kMaxCode - 3; ++p) code[i++] = *p;
        for (unsigned int k = 0; name[k] != '\0' && i < kMaxCode - 2; ++k) code[i++] = name[k];
        code[i++] = ')';
        code[i] = '\0';

        run(code);
    }

    void onCommand(const char* buffer, unsigned int size, void*)
    {
        if (buffer == nullptr || size == 0) return;

        char name[kMaxName];
        unsigned int n = 0;
        while (n < size && n < kMaxName - 1 && buffer[n] != '\0')
        {
            name[n] = buffer[n];
            ++n;
        }
        name[n] = '\0';
        if (n == 0) return;

        callNamedEvent(name);
    }
}

extern "C" MSFS_CALLBACK void module_init()
{
    fsCommBusRegister(kChannel, onCommand);
}

extern "C" MSFS_CALLBACK void module_deinit()
{
    fsCommBusUnregister(kChannel, onCommand);
}
