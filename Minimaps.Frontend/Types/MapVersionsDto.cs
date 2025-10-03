using Minimaps.Shared;
using Minimaps.Shared.Types;

namespace Minimaps.Frontend.Types;

public readonly record struct MapVersionsDto(Dictionary<BuildVersion, ContentHash> Versions);
