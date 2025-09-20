namespace Minimaps.Shared.BackendDto;

public readonly record struct DiscoveredRequestDto(List<DiscoveredRequestDtoEntry> Entries);
public readonly record struct DiscoveredRequestDtoEntry(string Product, string Version);
