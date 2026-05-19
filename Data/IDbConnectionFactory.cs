using System.Data;

namespace nexthire_api.Data;

public interface IDbConnectionFactory
{
    IDbConnection CreateConnection();
}

