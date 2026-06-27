using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services;

public sealed record UserAdminResult(bool Success, string Message);

public sealed record UserListItem(
    long Id, string UserName, string DisplayName, bool IsActive, int LinkedBuyerCount, DateTimeOffset CreatedAt);

public sealed class UserAdminService
{
    public const int MinPasswordLength = 6;

    private readonly WebMailDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;

    public UserAdminService(WebMailDbContext db, IPasswordHasher<AppUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<UserAdminResult> CreateAsync(
        UserRole role, string userName, string displayName, string password, long? actingAdminId)
    {
        userName = (userName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(userName))
        {
            return new(false, "用户名不能为空。");
        }
        if ((password ?? string.Empty).Length < MinPasswordLength)
        {
            return new(false, $"密码至少需要 {MinPasswordLength} 位。");
        }

        var normalized = userName.ToLower();
        if (await _db.Users.AnyAsync(u => u.UserName.ToLower() == normalized))
        {
            return new(false, "用户名已存在。");
        }

        var user = new AppUser
        {
            UserName = userName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim(),
            Role = role,
            IsActive = true,
        };
        user.PasswordHash = _hasher.HashPassword(user, password!);
        _db.Users.Add(user);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminCreateUser",
            UserId = actingAdminId,
            Details = $"role={role};user={userName}"
        });
        await _db.SaveChangesAsync();
        return new(true, "已创建账号。");
    }

    public async Task<UserAdminResult> ResetPasswordAsync(long userId, string newPassword, long? actingAdminId, UserRole? expectedRole = null)
    {
        if ((newPassword ?? string.Empty).Length < MinPasswordLength)
        {
            return new(false, $"密码至少需要 {MinPasswordLength} 位。");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || (expectedRole is not null && user.Role != expectedRole))
        {
            return new(false, "账号不存在。");
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword!);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminResetPassword",
            UserId = actingAdminId,
            Details = $"user={userId}"
        });
        await _db.SaveChangesAsync();
        return new(true, "已重置密码。");
    }

    public async Task<UserAdminResult> SetActiveAsync(long userId, bool isActive, long? actingAdminId, UserRole? expectedRole = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || (expectedRole is not null && user.Role != expectedRole))
        {
            return new(false, "账号不存在。");
        }

        user.IsActive = isActive;
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminSetActive",
            UserId = actingAdminId,
            Details = $"user={userId};active={isActive}"
        });
        await _db.SaveChangesAsync();
        return new(true, isActive ? "已启用账号。" : "已禁用账号。");
    }

    public async Task<IReadOnlyList<UserListItem>> ListByRoleAsync(UserRole role)
    {
        var users = await _db.Users
            .Where(u => u.Role == role)
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync();

        var result = new List<UserListItem>(users.Count);
        foreach (var u in users)
        {
            int count = role == UserRole.Supplier
                ? await (from a in _db.BuyerSupplierAssignments
                         join b in _db.Buyers on a.BuyerId equals b.Id
                         where a.SupplierId == u.Id && !b.IsDeleted
                         select a.Id).CountAsync()
                : await _db.Buyers.CountAsync(b => b.SaleId == u.Id && !b.IsDeleted);

            result.Add(new UserListItem(u.Id, u.UserName, u.DisplayName, u.IsActive, count, u.CreatedAt));
        }
        return result;
    }
}
