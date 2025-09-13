using CASCLib;
using Microsoft.Extensions.Logging;

namespace Minimaps.Generator;

internal class Generator(ILogger logger, string product, string cascRegion)
{
	internal async Task Generate()
	{
		logger.LogInformation($"Generating minimap data... product={product}, region={cascRegion}", product, cascRegion);

		// TODO: Both CASC and DBCD are not great in their async usage, as a result blocking is all over the place.
		// Probably just going to fix it myself? 

		var cascHandler = await GenerateHandler();
	}

	private async Task<CASCHandler> GenerateHandler()
	{
		CASCConfig.LoadFlags = LoadFlags.FileIndex;
		CASCLib.Logger.Init(); // Ideally feed to ILogger rather than the odd bespoke logger
		return CASCHandler.OpenOnlineStorage(product, cascRegion);
	}
}
