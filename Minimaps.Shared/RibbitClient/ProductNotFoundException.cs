namespace Minimaps.Shared.RibbitClient;

public class ProductNotFoundException(string product) : Exception($"Product '{product}' was not found on the Ribbit server.")
{
    public string Product { get; } = product;
}