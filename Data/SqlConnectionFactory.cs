using System.Data;
using System.Data.SqlClient;

namespace nexthire_api.Data;

public class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("NextHireDb")
            ?? throw new InvalidOperationException("Connection string 'NextHireDb' not found");
    }

    public IDbConnection CreateConnection()
    {
        return new SqlConnection(_connectionString);
    }
}

