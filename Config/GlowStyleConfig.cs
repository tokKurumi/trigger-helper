using System.Text.Json.Serialization;

namespace TriggerHelper.Config;

internal sealed class GlowStyleConfig
{
    [JsonPropertyName("color")]
    public GlowColorConfig Color { get; init; } = new();
}
