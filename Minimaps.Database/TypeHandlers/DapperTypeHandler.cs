using Dapper;
using Minimaps.Shared;
using System.Data;

namespace Minimaps.Database.TypeHandlers;

public class DapperTypeHandler : SqlMapper.TypeHandler<BuildVersion>
{
    private static bool _isRegistered = false;
    public static void RegisterTypeHandlers()
    {
        if (_isRegistered) return;
        SqlMapper.AddTypeHandler(new DapperTypeHandler());
        SqlMapper.AddTypeHandler(new DapperBuildVersionArrayTypeHandler());
        SqlMapper.AddTypeHandler(new DapperBuildVersionListTypeHandler());
        _isRegistered = true;
    }

    public override void SetValue(IDbDataParameter parameter, BuildVersion value)
    {
        parameter.Value = value.EncodedValue;
        parameter.DbType = DbType.Int64;
    }

    public override BuildVersion Parse(object value)
    {
        return value switch
        {
            long longValue => new BuildVersion(longValue),
            int intValue => new BuildVersion(intValue),
            // long > buildversion given we always store it as int64 in the DB
            string stringValue when long.TryParse(stringValue, out var parsed) => new BuildVersion(parsed),
            _ => throw new InvalidCastException($"Cannot convert {value?.GetType()} to BuildVersion")
        };
    }
}

public class DapperBuildVersionArrayTypeHandler : SqlMapper.TypeHandler<BuildVersion[]>
{
    public override void SetValue(IDbDataParameter parameter, BuildVersion[] value)
    {
        var longArray = value.Select(v => v.EncodedValue).ToArray();
        parameter.Value = longArray;
    }

    public override BuildVersion[] Parse(object value)
    {
        return value switch
        {
            long[] longArray => [.. longArray.Select(v => new BuildVersion(v))],
            int[] intArray => [.. intArray.Select(v => new BuildVersion(v))],
            _ => throw new InvalidCastException($"Cannot convert {value?.GetType()} to BuildVersion[]")
        };
    }
}

public class DapperBuildVersionListTypeHandler : SqlMapper.TypeHandler<List<BuildVersion>>
{
    public override void SetValue(IDbDataParameter parameter, List<BuildVersion> value)
    {
        var longArray = value.Select(v => v.EncodedValue).ToArray();
        parameter.Value = longArray;
    }

    public override List<BuildVersion> Parse(object value)
    {
        return value switch
        {
            long[] longArray => [.. longArray.Select(v => new BuildVersion(v))],
            int[] intArray => [.. intArray.Select(v => new BuildVersion(v))],
            _ => throw new InvalidCastException($"Cannot convert {value?.GetType()} to List<BuildVersion>")
        };
    }
}