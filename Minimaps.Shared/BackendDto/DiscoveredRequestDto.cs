namespace Minimaps.Shared.BackendDto;

public readonly record struct DiscoveredRequestDto(List<DiscoveredBuildDto> Entries);
public readonly record struct DiscoveredBuildDto(string Product, BuildVersion Version, List<string> Regions, string BuildConfig, string CDNConfig, string ProductConfig);