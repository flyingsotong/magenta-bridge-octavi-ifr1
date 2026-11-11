namespace MagentaBridge.Core;

/// <summary>
/// The contract between the app and its WASM module. Two products share one module, so the
/// names live here rather than in either engine.
/// </summary>
public static class ModuleLink
{
    /// <summary>CommBus channel the module listens on. Must match kChannel in the module source.</summary>
    public const string Channel = "magenta.bridge";

    /// <summary>
    /// Prefix marking a binding as a gauge event. In config.json a name like
    /// "H:AS530_OBS_Push" means "ask the module to call the sim's own event AS530_OBS_Push",
    /// which is the only way to reach the GNS530's bezel buttons.
    /// </summary>
    public const string EventPrefix = "H:";

    public static bool IsModuleEvent(string name) =>
        name.StartsWith(EventPrefix, StringComparison.OrdinalIgnoreCase);

    public static string Strip(string name) =>
        IsModuleEvent(name) ? name.Substring(EventPrefix.Length) : name;
}
