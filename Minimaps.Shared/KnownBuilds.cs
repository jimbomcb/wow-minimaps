namespace Minimaps.Shared;

/// <summary>
/// A list of builds and their associated specific change, used for deciding how to parse specific build schemas.
/// 
/// </summary>
public static class KnownBuilds
{
    // TODO:
    // - Find WdtFileId introduction
    // - WWF weather json surfaced to the browser at all?

    public static readonly BuildVersion LastDBC = new(7, 0, 3, 21287); 
    public static readonly BuildVersion SwitchMPQToCASC = new(6, 0, 1, 18125);

    /// <summary>
    /// After 9.0.1 the associated WDT is referenced by the map table
    /// </summary>
    public static readonly BuildVersion MapAddWdtFileId = new(9, 0, 1, 33978);
}
