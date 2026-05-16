using System.Text.Json.Serialization;

namespace TriggerHelper.Config;

internal readonly record struct GlowColorConfig
{
    [JsonPropertyName("r")]
    public byte R { get; init; }

    [JsonPropertyName("g")]
    public byte G { get; init; } = 255;

    [JsonPropertyName("b")]
    public byte B { get; init; }

    [JsonPropertyName("a")]
    public byte A { get; init; } = 255;

    public GlowColorConfig()
    {
    }
}
