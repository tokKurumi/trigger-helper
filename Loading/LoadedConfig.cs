using TriggerHelper.Runtime;

namespace TriggerHelper.Loading;

internal readonly record struct LoadedConfig(CompiledGlowConfig Config, string Source);
