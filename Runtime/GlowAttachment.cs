using Sharp.Shared.Units;
using TriggerHelper.Config;

namespace TriggerHelper.Runtime;

internal sealed record GlowAttachment
(
    GlowMode Mode,
    string Classname,
    EntityIndex SpawnedModelIndex,
    EntityIndex? LabelEntityIndex
);
