using System.Text.Json.Serialization;

namespace TriggerHelper.Config;

internal sealed class GlowEntityConfig
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("glow_type")]
    public GlowMode GlowType { get; init; } = GlowMode.Glow;

    [JsonPropertyName("min_glow_range")]
    public int MinGlowRange { get; init; }

    [JsonPropertyName("max_glow_range")]
    public int MaxGlowRange { get; init; }

    [JsonPropertyName("min_size")]
    public float MinSize { get; init; }

    [JsonPropertyName("max_size")]
    public float MaxSize { get; init; }
}
