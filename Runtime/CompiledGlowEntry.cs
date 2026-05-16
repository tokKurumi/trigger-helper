using Sharp.Shared.Types;
using TriggerHelper.Config;

namespace TriggerHelper.Runtime;

internal sealed record CompiledGlowEntry
(
    GlowMode Mode,
    int MinGlowRange,
    int MaxGlowRange,
    float MinSize,
    float MaxSize,
    Color32 Color,
    Vector ColorVector
);
