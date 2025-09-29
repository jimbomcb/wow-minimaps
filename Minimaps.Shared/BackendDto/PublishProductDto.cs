namespace Minimaps.Shared.BackendDto;

public readonly record struct ProductVersionDto(string Product, string Region, string BuildConfig, string CDNConfig, string KeyRing, uint BuildId, string VersionsName, string ProductConfig);
