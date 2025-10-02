using Minimaps.Shared.Types;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

/// <summary>
/// junction between products and compositions, many-to-many
/// </summary>
internal class ProductComposition
{
    public Int64 product_id { get; set; }
    public ContentHash composition_hash { get; set; }
}

#pragma warning restore IDE1006, CS8618

