using Npgsql;
using System.Data;

namespace Minimaps.Web.API;

public class DapperContext
{
    private readonly string _connectionString;

    public DapperContext(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("minimaps-database") ?? throw new Exception("No minimaps-database connection string");
    }

    public IDbConnection CreateConnection() => new NpgsqlConnection(_connectionString);
}