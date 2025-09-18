namespace Minimaps.Generator;

internal class GeneratorConfig
{
	public string Product { get; set; } = "wow";
	public string CascRegion { get; set; } = "us";
	public string CachePath { get; set; } = "\\\\mercury\\Cache"; // TODO: New default
	public int Parallelism { get; set; } = Environment.ProcessorCount;
	public bool UseOnline { get; set; } = false;
	public string FilterId { get; set; } = "*";
	public List<string> AdditionalCDNs { get; set; } = [];
}
