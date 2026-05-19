using Dapper;
using nexthire_api.Data;
using nexthire_api.DTOs;

namespace nexthire_api.Repositories;

public class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<UserRepository> _logger;

    public UserRepository(IDbConnectionFactory connectionFactory, ILogger<UserRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<IEnumerable<UserDto>> GetAllAsync()
    {
        // NOTE: Project DB uses `nh_users` per schema notes.
        const string sql = @"
            SELECT
                id AS Id,
                first_name AS FirstName,
                last_name AS LastName,
                email AS Email,
                phone AS Phone,
                created_at AS CreatedAt
            FROM nh_users
            ORDER BY created_at DESC;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryAsync<UserDto>(sql);
    }

    public async Task<UserDto?> GetByIdAsync(Guid id)
    {
        const string sql = @"
            SELECT
                id AS Id,
                first_name AS FirstName,
                last_name AS LastName,
                email AS Email,
                phone AS Phone,
                created_at AS CreatedAt
            FROM nh_users
            WHERE id = @id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<UserDto>(sql, new { id });
    }

    public async Task<UserDto> CreateAsync(CreateUserDto dto)
    {
        const string sql = @"
            INSERT INTO nh_users (first_name, last_name, email, phone, created_at)
            OUTPUT INSERTED.id AS Id,
                   INSERTED.first_name AS FirstName,
                   INSERTED.last_name AS LastName,
                   INSERTED.email AS Email,
                   INSERTED.phone AS Phone,
                   INSERTED.created_at AS CreatedAt
            VALUES (@FirstName, @LastName, @Email, @Phone, SYSUTCDATETIME());";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QuerySingleAsync<UserDto>(sql, dto);
    }

    public async Task<UserDto?> UpdateAsync(Guid id, UpdateUserDto dto)
    {
        const string sql = @"
            UPDATE nh_users
            SET
                first_name = COALESCE(@FirstName, first_name),
                last_name = COALESCE(@LastName, last_name),
                email = COALESCE(@Email, email),
                phone = COALESCE(@Phone, phone),
                updated_at = SYSUTCDATETIME()
            WHERE id = @Id;

            SELECT
                id AS Id,
                first_name AS FirstName,
                last_name AS LastName,
                email AS Email,
                phone AS Phone,
                created_at AS CreatedAt
            FROM nh_users
            WHERE id = @Id;";

        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<UserDto>(
            sql,
            new
            {
                Id = id,
                dto.FirstName,
                dto.LastName,
                dto.Email,
                dto.Phone
            });
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        const string sql = @"DELETE FROM nh_users WHERE id = @id;";
        using var connection = _connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(sql, new { id });
        return affected > 0;
    }
}

