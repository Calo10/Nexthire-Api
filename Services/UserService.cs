using nexthire_api.DTOs;
using nexthire_api.Repositories;

namespace nexthire_api.Services;

public class UserService : IUserService
{
    private readonly IUserRepository _repo;

    public UserService(IUserRepository repo)
    {
        _repo = repo;
    }

    public async Task<IEnumerable<UserDto>> GetAllUsersAsync()
    {
        return await _repo.GetAllAsync();
    }

    public async Task<UserDto?> GetUserByIdAsync(Guid id)
    {
        return await _repo.GetByIdAsync(id);
    }

    public async Task<UserDto> CreateUserAsync(CreateUserDto createUserDto)
    {
        return await _repo.CreateAsync(createUserDto);
    }

    public async Task<UserDto?> UpdateUserAsync(Guid id, UpdateUserDto updateUserDto)
    {
        return await _repo.UpdateAsync(id, updateUserDto);
    }

    public async Task<bool> DeleteUserAsync(Guid id)
    {
        return await _repo.DeleteAsync(id);
    }
}

