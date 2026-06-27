using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services.Auth;

public static class IdentitySeeder
{
    // 缺失同名用户时播种一个默认管理员；已存在则跳过（不重置密码）。
    public static async Task EnsureAdminSeededAsync(
        WebMailDbContext db, IPasswordHasher<AppUser> hasher, string userName, string password)
    {
        if (await db.Users.AnyAsync(u => u.UserName == userName))
        {
            return;
        }

        var admin = new AppUser
        {
            UserName = userName,
            Role = UserRole.Administrator,
            DisplayName = "管理员",
        };
        admin.PasswordHash = hasher.HashPassword(admin, password);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }
}
