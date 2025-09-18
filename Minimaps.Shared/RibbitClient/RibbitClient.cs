namespace RibbitClient;

/// <summary>
/// Response from the TACT/Ribbit server containing a sequence ID (incremented per change) and the response data
/// </summary>
public readonly record struct RibbitResponse<T>(uint SequenceId, T Data);

/// <summary>
/// Client for interfacing with the TACT/Ribbit Battle.net server, provides data about the available products 
/// and their versions contained in the TACT system
/// </summary>
public class RibbitClient(RibbitRegion region)
{
	private readonly string _server = region switch
	{
		RibbitRegion.US => "https://us.version.battle.net",
		RibbitRegion.EU => "https://eu.version.battle.net",
		_ => throw new NotImplementedException()
	};

	/// <summary>
	/// Get all available products on the TACT server
	/// </summary>
	/// <exception cref="SchemaException">Thrown if the response schema does not match the expected format.</exception>
	public async Task<RibbitResponse<List<Product>>> SummaryAsync()
	{
		return await MakeRequestAsync("/v2/summary", "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0", parts =>
		{
			var name = parts[0];
			var seqn = uint.Parse(parts[1]);
			var flags = parts.Length > 2 ? parts[2] : string.Empty;
			return new Product(name, seqn, flags);
		});
	}

	/// <summary>
	/// Get all available versions for a specific product from the TACT server
	/// </summary>
	/// <exception cref="SchemaException">Thrown if the response schema does not match the expected format.</exception>
	/// <exception cref="ProductNotFoundException">Thrown if the specified product does not exist.</exception>
	public async Task<RibbitResponse<List<Version>>> VersionsAsync(string product)
	{
		return await MakeRequestAsync($"/v2/products/{product}/versions", 
			"Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16", 
			parts =>
		{
			var region = parts[0];
			var buildConfig = parts[1];
			var cdnConfig = parts[2];
			var keyRing = parts[3];
			var buildId = uint.Parse(parts[4]);
			var versionsName = parts[5];
			var productConfig = parts.Length > 6 ? parts[6] : string.Empty;
			return new Version(region, buildConfig, cdnConfig, keyRing, buildId, versionsName, productConfig);
		}, product);
	}

	private async Task<RibbitResponse<List<T>>> MakeRequestAsync<T>(string endpoint, string expectedSchema, Func<string[], T> parseRow, string? productName = null)
	{
		using var httpClient = new HttpClient();

		var response = await httpClient.GetAsync(_server + endpoint);

		// product specific requests that signal 404 signals that the product doesn't exist.
		if (response.StatusCode == System.Net.HttpStatusCode.NotFound && productName != null)
			throw new ProductNotFoundException(productName);

		response.EnsureSuccessStatusCode();

		var content = await response.Content.ReadAsStringAsync();
		using var reader = new StringReader(content);

		var schemaLine = reader.ReadLine();
		if (schemaLine == null || schemaLine != expectedSchema)
			throw new SchemaException($"Expected schema line '{expectedSchema}' not found. Got: {schemaLine}");

		var sequenceLine = reader.ReadLine();
		if (sequenceLine == null || !sequenceLine.StartsWith("## seqn = "))
			throw new SchemaException($"Expected sequence line not found. Got: {sequenceLine}");

		var sequenceId = uint.Parse(sequenceLine[10..]);

		var results = new List<T>();
		string? line;
		while ((line = reader.ReadLine()) != null)
		{
			if (line.StartsWith("#"))
				throw new Exception("Unexpected # line");

			var parts = line.Split('|');
			if (parts.Length >= 1)
				results.Add(parseRow(parts));
		}

		return new RibbitResponse<List<T>>(sequenceId, results);
	}

}
