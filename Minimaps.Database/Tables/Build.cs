using Minimaps.Shared;
using NodaTime;

namespace Minimaps.Database.Tables;

#pragma warning disable IDE1006, CS8618

internal class Build
{
    /// <summary>
    /// Primary key: build version encoded into a sortable int64
    /// Bit-packed to: expansion(11) | major(10) | minor(10) | build(32)
    /// </summary>
    public BuildVersion id { get; set; }

    /// <summary>
    /// Full version string "11.0.7.58046" [expansion].[major].[minor].[build]
    /// </summary>
    public string version { get; set; }

    /// <summary>
    /// First time this build was seen by the scanner
    /// </summary>
    public Instant discovered { get; set; }
}

#pragma warning restore IDE1006, CS8618

