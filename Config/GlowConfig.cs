using System.Text.Json.Serialization;

namespace TriggerHelper.Config;

internal sealed class GlowConfig
{
    [JsonPropertyName("entities")]
    public List<GlowEntityConfig> Entities { get; init; } = [];

    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; init; } = [];

    [JsonPropertyName("style")]
    public GlowStyleConfig Style { get; init; } = new();
}
