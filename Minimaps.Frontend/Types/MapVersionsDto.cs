using Minimaps.Shared;
using Minimaps.Shared.Types;
using System.Text.Json.Serialization;

namespace Minimaps.Frontend.Types;

public readonly record struct MapVersionsDto(Dictionary<BuildVersion, VersionEntryDto> Versions);

public class VersionEntryDto
{
    /// <summary>Layer hashes indexed by LayerType enum value. Null if layer absent for this build.</summary>
    [JsonPropertyName("l")]
    public string?[] Layers { get; init; } = new string?[LayerTypeExtensions.Count];

    /// <summary>CDN-missing tile hashes per layer. Omitted entirely if no layers have cdn_missing.</summary>
    [JsonPropertyName("m")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]?[]? CdnMissing { get; init; }

    /// <summary>Product names this build was seen on.</summary>
    [JsonPropertyName("p")]
    public string[] Products { get; init; } = [];
}
