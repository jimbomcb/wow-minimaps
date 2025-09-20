using Npgsql;
using System.Data;

namespace Minimaps.Web.API;

public class DapperContext(IConfiguration configuration)
{
    public IDbConnection Connection { get; private set; } 
        = new NpgsqlConnection(configuration.GetConnectionString("minimaps-database") ?? throw new Exception("No minimaps-database connection string"));
}