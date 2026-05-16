using System.Text.Json;
using System.Text.Json.Serialization;

namespace TriggerHelper.Config;

[JsonSourceGenerationOptions
(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Converters = [typeof(JsonStringEnumConverter<GlowMode>)]
)]
[JsonSerializable(typeof(GlowConfig))]
internal sealed partial class GlowJsonContext : JsonSerializerContext;
