using Minimaps.Shared;
using Minimaps.Shared.Types;

namespace Minimaps.Frontend.Types;

public readonly record struct MapVersionsDto(Dictionary<BuildVersion, MapVersionEntryDto> Versions);
public readonly record struct MapVersionEntryDto(ContentHash CompositionHash, string[] Products);

public readonly record struct MapLayerEntryDto(ContentHash CompositionHash, bool Partial);
public readonly record struct MapLayersDto(Dictionary<string, Dictionary<BuildVersion, MapLayerEntryDto>> Layers);
