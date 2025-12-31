using System.Text.Json.Serialization;

namespace Minimaps.Shared;

/// <summary>
/// Responsible for converting WoW's [patch].[build] (i.e. 11.0.7.58046) between
/// its individual components and a single sortable BIGINT for use in PgSQL.
/// Bit-packed to: reserved(1) | expansion(11) | major(10) | minor(10) | build(32)
/// Max:           reserved      2047          | 1023      | 1023      | int32.max
/// </summary>
[JsonConverter(typeof(BuildVersionConverter))]
public readonly struct BuildVersion : IComparable<BuildVersion>, IEquatable<BuildVersion>
{
    private readonly long _value;

    // Bit masks and shifts for packing/unpacking
    private const long BuildMask = 0xFFFFFFFF; // 32 bits
    private const long MinorMask = 0x3FF;      // 10 bits
    private const long MajorMask = 0x3FF;      // 10 bits
    private const long ExpansionMask = 0x7FF;  // 11 bits

    private const int BuildShift = 0;
    private const int MinorShift = 32;
    private const int MajorShift = 42;
    private const int ExpansionShift = 52;

    public static explicit operator long(BuildVersion version) => version._value;
    public static explicit operator string(BuildVersion version) => version.ToString();
    public static explicit operator BuildVersion(long value) => new(value);

    public BuildVersion(long value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "BuildVersion encoded value must be non-negative");
        _value = value;
    }

    public BuildVersion(int expansion, int major, int minor, int build)
    {
        if (expansion < 0 || expansion > ExpansionMask)
            throw new ArgumentOutOfRangeException(nameof(expansion), $"Expansion must be between 0 and {ExpansionMask}");
        if (major < 0 || major > MajorMask)
            throw new ArgumentOutOfRangeException(nameof(major), $"Major must be between 0 and {MajorMask}");
        if (minor < 0 || minor > MinorMask)
            throw new ArgumentOutOfRangeException(nameof(minor), $"Minor must be between 0 and {MinorMask}");
        if (build < 0 || (long)build > BuildMask)
            throw new ArgumentOutOfRangeException(nameof(build), $"Build must be between 0 and {BuildMask}");

        _value = ((long)expansion << ExpansionShift) |
                 ((long)major << MajorShift) |
                 ((long)minor << MinorShift) |
                 ((long)build << BuildShift);
    }

    public int Expansion => (int)((_value >> ExpansionShift) & ExpansionMask);
    public int Major => (int)((_value >> MajorShift) & MajorMask);
    public int Minor => (int)((_value >> MinorShift) & MinorMask);
    public int Build => (int)((_value >> BuildShift) & BuildMask);
    public long EncodedValue => _value;

    public static BuildVersion Parse(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Version string cannot be null or empty", nameof(version));

        var parts = version.Split('.');
        if (parts.Length != 4)
            throw new FormatException($"Version string '{version}' must be in format 'expansion.major.minor.build'");

        if (!int.TryParse(parts[0], out var expansion) ||
            !int.TryParse(parts[1], out var major) ||
            !int.TryParse(parts[2], out var minor) ||
            !int.TryParse(parts[3], out var build))
        {
            throw new FormatException($"Invalid version string '{version}'");
        }

        return new(expansion, major, minor, build);
    }

    public static bool TryParse(string version, out BuildVersion result)
    {
        try
        {
            result = Parse(version);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public override string ToString() => $"{Expansion}.{Major}.{Minor}.{Build}";

    public int CompareTo(BuildVersion other) => _value.CompareTo(other._value);
    public bool Equals(BuildVersion other) => _value == other._value;
    public override bool Equals(object? obj) => obj is BuildVersion other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();

    public static bool operator ==(BuildVersion left, BuildVersion right) => left.Equals(right);
    public static bool operator !=(BuildVersion left, BuildVersion right) => !left.Equals(right);
    public static bool operator <(BuildVersion left, BuildVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(BuildVersion left, BuildVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(BuildVersion left, BuildVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(BuildVersion left, BuildVersion right) => left.CompareTo(right) >= 0;
}
