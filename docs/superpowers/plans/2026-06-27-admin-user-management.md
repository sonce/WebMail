# 管理员后台用户管理 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为管理员后台补齐销售员管理、供应商管理（账号增/查/重置密码/启停用）与买家管理增强（软删除 + 筛选搜索）。

**Architecture:** 销售员与供应商管理共享一个 `UserAdminService`，两个独立的 `[AdminOnly]` Razor Pages 各自固定一个角色调用该服务；买家页扩展现有 PageModel；登录流程校验 `IsActive`；导航改为管理后台下拉菜单。

**Tech Stack:** ASP.NET Core Razor Pages、EF Core（SQLite，`EnsureCreatedAsync` 无迁移）、`IPasswordHasher<AppUser>`、xUnit + EF InMemory 测试。

## Global Constraints

- 数据库用 `EnsureCreatedAsync()`，无 EF 迁移：新增列不会自动加到已有库。新增 `AppUser.IsActive` 后，开发环境须删除 SQLite 库文件让其重建，或手动执行 `ALTER TABLE Users ADD COLUMN IsActive INTEGER NOT NULL DEFAULT 1;`。
- 用户名唯一性比较大小写不敏感，沿用 `UserName.ToLower()` 模式。
- 密码最小长度 = 6。
- 所有管理页授权策略 = `AdminOnly`。
- 写操作写入 `AuditLog`（`UserId` = 当前管理员，可空）。
- 测试沿用现有风格：`new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options`。
- 所有用户可见文案为中文。

---

### Task 1: AppUser.IsActive 字段 + UserAdminService

**Files:**
- Modify: `src/WebMail/Domain/Entities.cs`（`AppUser` 加 `IsActive`）
- Create: `src/WebMail/Services/UserAdminService.cs`
- Modify: `src/WebMail/Program.cs`（注册服务）
- Test: `tests/WebMail.Tests/UserAdminServiceTests.cs`

**Interfaces:**
- Produces:
  - `WebMail.Domain.AppUser.IsActive : bool`（默认 true）
  - `record UserAdminResult(bool Success, string Message)`
  - `record UserListItem(long Id, string UserName, string DisplayName, bool IsActive, int LinkedBuyerCount, DateTimeOffset CreatedAt)`
  - `UserAdminService.CreateAsync(UserRole role, string userName, string displayName, string password, long? actingAdminId) : Task<UserAdminResult>`
  - `UserAdminService.ResetPasswordAsync(long userId, string newPassword, long? actingAdminId) : Task<UserAdminResult>`
  - `UserAdminService.SetActiveAsync(long userId, bool isActive, long? actingAdminId) : Task<UserAdminResult>`
  - `UserAdminService.ListByRoleAsync(UserRole role) : Task<IReadOnlyList<UserListItem>>`
  - `const int UserAdminService.MinPasswordLength = 6`

- [ ] **Step 1: 给 AppUser 加 IsActive 字段**

在 `src/WebMail/Domain/Entities.cs` 的 `AppUser` 中，`CreatedAt` 之后加一行：

```csharp
public bool IsActive { get; set; } = true;
```

- [ ] **Step 2: 写失败测试**

创建 `tests/WebMail.Tests/UserAdminServiceTests.cs`：

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class UserAdminServiceTests
{
    [Fact]
    public async Task CreateAddsActiveUserWithHashedPasswordAndAudit()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());

        var result = await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", actingAdminId: 9);

        Assert.True(result.Success);
        var user = await db.Users.SingleAsync();
        Assert.Equal("alice", user.UserName);
        Assert.Equal(UserRole.Sales, user.Role);
        Assert.True(user.IsActive);
        Assert.NotEqual("secret1", user.PasswordHash);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsDuplicateUserNameCaseInsensitive()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);

        var result = await svc.CreateAsync(UserRole.Supplier, "ALICE", "Other", "secret1", null);

        Assert.False(result.Success);
        Assert.Equal(1, await db.Users.CountAsync());
    }

    [Fact]
    public async Task CreateRejectsShortPassword()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());

        var result = await svc.CreateAsync(UserRole.Sales, "bob", "Bob", "12345", null);

        Assert.False(result.Success);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsEmptyUserName()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());

        var result = await svc.CreateAsync(UserRole.Sales, "   ", "X", "secret1", null);

        Assert.False(result.Success);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task ResetPasswordChangesHash()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);
        var before = (await db.Users.SingleAsync()).PasswordHash;

        var result = await svc.ResetPasswordAsync((await db.Users.SingleAsync()).Id, "newpass1", null);

        Assert.True(result.Success);
        Assert.NotEqual(before, (await db.Users.SingleAsync()).PasswordHash);
    }

    [Fact]
    public async Task SetActiveTogglesFlag()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);
        var id = (await db.Users.SingleAsync()).Id;

        await svc.SetActiveAsync(id, false, null);

        Assert.False((await db.Users.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task ListByRoleCountsSalesBuyersExcludingDeleted()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Sales, "alice", "Alice", "secret1", null);
        var saleId = (await db.Users.SingleAsync()).Id;
        db.Buyers.Add(new Buyer { CardNo = "a", SaleId = saleId });
        db.Buyers.Add(new Buyer { CardNo = "b", SaleId = saleId, IsDeleted = true });
        await db.SaveChangesAsync();

        var list = await svc.ListByRoleAsync(UserRole.Sales);

        Assert.Equal(1, Assert.Single(list).LinkedBuyerCount);
    }

    [Fact]
    public async Task ListByRoleCountsSupplierAssignmentsExcludingDeletedBuyers()
    {
        await using var db = CreateDb();
        var svc = new UserAdminService(db, new PasswordHasher<AppUser>());
        await svc.CreateAsync(UserRole.Supplier, "sup", "Sup", "secret1", null);
        var supId = (await db.Users.SingleAsync()).Id;
        var b1 = new Buyer { CardNo = "a" };
        var b2 = new Buyer { CardNo = "b", IsDeleted = true };
        db.Buyers.AddRange(b1, b2);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = b1.Id, SupplierId = supId });
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = b2.Id, SupplierId = supId });
        await db.SaveChangesAsync();

        var list = await svc.ListByRoleAsync(UserRole.Supplier);

        Assert.Equal(1, Assert.Single(list).LinkedBuyerCount);
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter UserAdminServiceTests`
Expected: 编译失败 / FAIL —— `UserAdminService` 不存在。

- [ ] **Step 4: 实现 UserAdminService**

创建 `src/WebMail/Services/UserAdminService.cs`：

```csharp
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

    public async Task<UserAdminResult> ResetPasswordAsync(long userId, string newPassword, long? actingAdminId)
    {
        if ((newPassword ?? string.Empty).Length < MinPasswordLength)
        {
            return new(false, $"密码至少需要 {MinPasswordLength} 位。");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
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

    public async Task<UserAdminResult> SetActiveAsync(long userId, bool isActive, long? actingAdminId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
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
```

- [ ] **Step 5: 注册服务**

在 `src/WebMail/Program.cs` 的 `builder.Services.AddScoped<BuyerRuleService>();` 之后加一行：

```csharp
builder.Services.AddScoped<UserAdminService>();
```

（`Program.cs` 顶部已有 `using WebMail.Services;`，无需新增 using。）

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter UserAdminServiceTests`
Expected: PASS（8 个测试）。

- [ ] **Step 7: 提交**

```bash
git add src/WebMail/Domain/Entities.cs src/WebMail/Services/UserAdminService.cs src/WebMail/Program.cs tests/WebMail.Tests/UserAdminServiceTests.cs
git commit -m "feat: add IsActive field and UserAdminService"
```

---

### Task 2: 登录校验禁用账号

**Files:**
- Modify: `src/WebMail/Pages/Login.cshtml.cs`
- Test: `tests/WebMail.Tests/LoginModelTests.cs`（追加）

**Interfaces:**
- Consumes: `AppUser.IsActive`（Task 1）

- [ ] **Step 1: 写失败测试**

在 `tests/WebMail.Tests/LoginModelTests.cs` 的 `UpperCaseUserNameStillSignsIn` 测试之后、`SeedUser` 之前追加：

```csharp
    [Fact]
    public async Task DisabledUserCannotSignIn()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var disabled = await db.Users.SingleAsync();
        disabled.IsActive = false;
        await db.SaveChangesAsync();
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "pw",
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal("账号已被禁用", model.ErrorMessage);
        Assert.Null(auth.SignedInPrincipal);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter LoginModelTests`
Expected: `DisabledUserCannotSignIn` FAIL —— 禁用账号仍登录成功（`SignedInPrincipal` 非空）。

- [ ] **Step 3: 实现禁用校验**

在 `src/WebMail/Pages/Login.cshtml.cs` 的 `OnPostAsync` 中，密码校验失败的 `if` 块之后、构造 `claims` 之前，插入：

```csharp
        if (!user.IsActive)
        {
            ErrorMessage = "账号已被禁用";
            return Page();
        }
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter LoginModelTests`
Expected: PASS（含原有用例 + 新增 1 个）。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Pages/Login.cshtml.cs tests/WebMail.Tests/LoginModelTests.cs
git commit -m "feat: reject login for disabled accounts"
```

---

### Task 3: /Admin/Sales 销售员管理页

**Files:**
- Create: `src/WebMail/Pages/Admin/Sales.cshtml`
- Create: `src/WebMail/Pages/Admin/Sales.cshtml.cs`
- Create: `src/WebMail/Pages/Admin/_UserManagement.cshtml`（销售员/供应商两页共享的视图 partial）
- Test: `tests/WebMail.Tests/AdminSalesModelTests.cs`

**Interfaces:**
- Consumes: `UserAdminService`、`UserListItem`、`UserAdminResult`（Task 1）
- Produces: `WebMail.Pages.Admin.SalesModel` 含处理器 `OnGetAsync`、`OnPostCreateAsync`、`OnPostResetPasswordAsync(long id, string password)`、`OnPostSetActiveAsync(long id, bool isActive)`

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/AdminSalesModelTests.cs`：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Pages.Admin;
using Xunit;

namespace WebMail.Tests;

public sealed class AdminSalesModelTests
{
    [Fact]
    public async Task CreateAddsSalesUserAndListsIt()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.NewUserName = "alice";
        model.NewDisplayName = "Alice";
        model.NewPassword = "secret1";

        await model.OnPostCreateAsync();

        var user = await db.Users.SingleAsync();
        Assert.Equal(UserRole.Sales, user.Role);
        Assert.Contains(model.Users, u => u.UserName == "alice");
    }

    [Fact]
    public async Task GetLoadsOnlySalesUsers()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { UserName = "s", Role = UserRole.Sales, DisplayName = "s" });
        db.Users.Add(new AppUser { UserName = "p", Role = UserRole.Supplier, DisplayName = "p" });
        await db.SaveChangesAsync();
        var model = CreateModel(db);

        await model.OnGetAsync();

        Assert.Single(model.Users);
        Assert.Equal("s", model.Users[0].UserName);
    }

    [Fact]
    public async Task SetActiveDisablesUser()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { UserName = "s", Role = UserRole.Sales, DisplayName = "s" });
        await db.SaveChangesAsync();
        var id = (await db.Users.SingleAsync()).Id;
        var model = CreateModel(db);

        await model.OnPostSetActiveAsync(id, false);

        Assert.False((await db.Users.SingleAsync()).IsActive);
    }

    private static SalesModel CreateModel(WebMailDbContext db)
    {
        var model = new SalesModel(new UserAdminService(db, new PasswordHasher<AppUser>()));
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
        };
        model.PageContext = new PageContext { HttpContext = ctx };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter AdminSalesModelTests`
Expected: 编译失败 —— `SalesModel` 不存在。

- [ ] **Step 3: 实现 PageModel**

创建 `src/WebMail/Pages/Admin/Sales.cshtml.cs`：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class SalesModel : PageModel
{
    private readonly UserAdminService _users;

    public SalesModel(UserAdminService users) => _users = users;

    protected virtual UserRole Role => UserRole.Sales;
    public string RoleTitle => Role == UserRole.Supplier ? "供应商" : "销售员";

    public IReadOnlyList<UserListItem> Users { get; private set; } = Array.Empty<UserListItem>();
    public string? Message { get; private set; }

    [BindProperty] public string NewUserName { get; set; } = string.Empty;
    [BindProperty] public string NewDisplayName { get; set; } = string.Empty;
    [BindProperty] public string NewPassword { get; set; } = string.Empty;

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostCreateAsync()
    {
        Message = (await _users.CreateAsync(Role, NewUserName, NewDisplayName, NewPassword, AdminId())).Message;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(long id, string password)
    {
        Message = (await _users.ResetPasswordAsync(id, password, AdminId())).Message;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostSetActiveAsync(long id, bool isActive)
    {
        Message = (await _users.SetActiveAsync(id, isActive, AdminId())).Message;
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync() => Users = await _users.ListByRoleAsync(Role);

    private long? AdminId() =>
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
```

- [ ] **Step 4: 实现共享 partial 与瘦身视图**

创建共享 partial `src/WebMail/Pages/Admin/_UserManagement.cshtml`（类型化为基类 `SalesModel`，因 `SuppliersModel : SalesModel`，两页都能传入）：

```cshtml
@model WebMail.Pages.Admin.SalesModel

<h1 class="display-6">@(Model.RoleTitle)管理</h1>

@if (!string.IsNullOrEmpty(Model.Message))
{
    <div class="alert alert-info" role="alert">@Model.Message</div>
}

<div class="card mb-4">
    <div class="card-body">
        <h2 class="h5">新建@(Model.RoleTitle)</h2>
        <form method="post" asp-page-handler="Create" class="row g-2">
            <div class="col-auto">
                <input class="form-control" asp-for="NewUserName" placeholder="用户名" />
            </div>
            <div class="col-auto">
                <input class="form-control" asp-for="NewDisplayName" placeholder="显示名" />
            </div>
            <div class="col-auto">
                <input class="form-control" type="password" asp-for="NewPassword" placeholder="初始密码（≥6位）" />
            </div>
            <div class="col-auto">
                <button type="submit" class="btn btn-primary">创建</button>
            </div>
        </form>
    </div>
</div>

@if (Model.Users.Count == 0)
{
    <p>暂无@(Model.RoleTitle)。</p>
}
else
{
    <table class="table table-striped">
        <thead>
            <tr>
                <th>用户名</th>
                <th>显示名</th>
                <th>状态</th>
                <th>关联买家数</th>
                <th>创建时间</th>
                <th>操作</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var u in Model.Users)
            {
                <tr>
                    <td>@u.UserName</td>
                    <td>@u.DisplayName</td>
                    <td>@(u.IsActive ? "启用" : "禁用")</td>
                    <td>@u.LinkedBuyerCount</td>
                    <td>@u.CreatedAt</td>
                    <td>
                        <div class="d-flex gap-2">
                            <form method="post" asp-page-handler="ResetPassword" class="d-flex gap-1">
                                <input type="hidden" name="id" value="@u.Id" />
                                <input class="form-control form-control-sm" type="password" name="password" placeholder="新密码" />
                                <button type="submit" class="btn btn-sm btn-outline-secondary">重置密码</button>
                            </form>
                            <form method="post" asp-page-handler="SetActive">
                                <input type="hidden" name="id" value="@u.Id" />
                                <input type="hidden" name="isActive" value="@((!u.IsActive).ToString().ToLower())" />
                                <button type="submit" class="btn btn-sm @(u.IsActive ? "btn-outline-danger" : "btn-outline-success")">
                                    @(u.IsActive ? "禁用" : "启用")
                                </button>
                            </form>
                        </div>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}
```

然后创建瘦身的页面 `src/WebMail/Pages/Admin/Sales.cshtml`：

```cshtml
@page
@model WebMail.Pages.Admin.SalesModel
@{
    ViewData["Title"] = $"{Model.RoleTitle}管理";
}

<partial name="_UserManagement" model="Model" />
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter AdminSalesModelTests`
Expected: PASS（3 个测试）。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Pages/Admin/Sales.cshtml src/WebMail/Pages/Admin/Sales.cshtml.cs src/WebMail/Pages/Admin/_UserManagement.cshtml tests/WebMail.Tests/AdminSalesModelTests.cs
git commit -m "feat: add admin sales management page"
```

---

### Task 4: /Admin/Suppliers 供应商管理页

**Files:**
- Create: `src/WebMail/Pages/Admin/Suppliers.cshtml`
- Create: `src/WebMail/Pages/Admin/Suppliers.cshtml.cs`
- Test: `tests/WebMail.Tests/AdminSuppliersModelTests.cs`

**Interfaces:**
- Consumes: `WebMail.Pages.Admin.SalesModel`（Task 3，作为基类）、`UserAdminService`
- Produces: `WebMail.Pages.Admin.SuppliersModel : SalesModel`，`Role` 覆盖为 `UserRole.Supplier`

说明：供应商管理与销售员管理逻辑完全相同，仅角色不同。`SuppliersModel` 继承 `SalesModel` 并覆盖 `Role`，避免重复处理器代码。视图为独立文件（Razor Page 需各自的 `@page`/`@model`）。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/AdminSuppliersModelTests.cs`：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Pages.Admin;
using Xunit;

namespace WebMail.Tests;

public sealed class AdminSuppliersModelTests
{
    [Fact]
    public async Task CreateAddsSupplierUser()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.NewUserName = "sup";
        model.NewDisplayName = "Sup";
        model.NewPassword = "secret1";

        await model.OnPostCreateAsync();

        Assert.Equal(UserRole.Supplier, (await db.Users.SingleAsync()).Role);
    }

    [Fact]
    public async Task GetLoadsOnlySupplierUsers()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { UserName = "s", Role = UserRole.Sales, DisplayName = "s" });
        db.Users.Add(new AppUser { UserName = "p", Role = UserRole.Supplier, DisplayName = "p" });
        await db.SaveChangesAsync();
        var model = CreateModel(db);

        await model.OnGetAsync();

        Assert.Single(model.Users);
        Assert.Equal("p", model.Users[0].UserName);
    }

    private static SuppliersModel CreateModel(WebMailDbContext db)
    {
        var model = new SuppliersModel(new UserAdminService(db, new PasswordHasher<AppUser>()));
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "1")], "test"))
        };
        model.PageContext = new PageContext { HttpContext = ctx };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter AdminSuppliersModelTests`
Expected: 编译失败 —— `SuppliersModel` 不存在。

- [ ] **Step 3: 实现 PageModel**

创建 `src/WebMail/Pages/Admin/Suppliers.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class SuppliersModel : SalesModel
{
    public SuppliersModel(UserAdminService users) : base(users)
    {
    }

    protected override UserRole Role => UserRole.Supplier;
}
```

- [ ] **Step 4: 实现视图**

创建 `src/WebMail/Pages/Admin/Suppliers.cshtml`（渲染 Task 3 创建的共享 partial）：

```cshtml
@page
@model WebMail.Pages.Admin.SuppliersModel
@{
    ViewData["Title"] = $"{Model.RoleTitle}管理";
}

<partial name="_UserManagement" model="Model" />
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter AdminSuppliersModelTests`
Expected: PASS（2 个测试）。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Pages/Admin/Suppliers.cshtml src/WebMail/Pages/Admin/Suppliers.cshtml.cs tests/WebMail.Tests/AdminSuppliersModelTests.cs
git commit -m "feat: add admin suppliers management page"
```

---

### Task 5: /Admin/Buyers 增强（软删除 + 筛选搜索）

**Files:**
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml.cs`
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml`
- Test: `tests/WebMail.Tests/AdminBuyersModelTests.cs`（追加）

**Interfaces:**
- Produces: `BuyersModel` 新增 `OnPostDeleteAsync(long id)`、筛选属性 `StatusFilter`、`EmailFilter`、`CardNo`

- [ ] **Step 1: 写失败测试**

在 `tests/WebMail.Tests/AdminBuyersModelTests.cs` 的 `CreateModel` 之前追加三个测试：

```csharp
    [Fact]
    public async Task DeleteSoftDeletesBuyerWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", BuyerStatus = BuyerStatus.Approved };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostDeleteAsync(buyer.Id);

        Assert.True((await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).IsDeleted);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task GetFiltersByBuyerStatus()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "p", BuyerStatus = BuyerStatus.PendingReview });
        db.Buyers.Add(new Buyer { CardNo = "a", BuyerStatus = BuyerStatus.Approved });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        model.StatusFilter = BuyerStatus.Approved;
        await model.OnGetAsync();

        Assert.Equal("a", Assert.Single(model.Buyers).CardNo);
    }

    [Fact]
    public async Task GetFiltersByCardNoSubstring()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "ABC123" });
        db.Buyers.Add(new Buyer { CardNo = "XYZ999" });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        model.CardNo = "ABC";
        await model.OnGetAsync();

        Assert.Equal("ABC123", Assert.Single(model.Buyers).CardNo);
    }
```

注意：现有 `OnGetAsync` 签名为 `async Task`，本任务改为 `async Task<IActionResult>` 不影响这些测试（测试不检查返回值）。`CreateModel` 已有，无需改动。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter AdminBuyersModelTests`
Expected: 编译失败 —— `OnPostDeleteAsync`、`StatusFilter`、`CardNo` 不存在。

- [ ] **Step 3: 修改 PageModel**

将 `src/WebMail/Pages/Admin/Buyers.cshtml.cs` 整个文件替换为：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;

    public BuyersModel(WebMailDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public BuyerStatus? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public EmailAuthorizationStatus? EmailFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? CardNo { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(long id) => await ReviewAsync(id, BuyerStatus.Approved);

    public async Task<IActionResult> OnPostRejectAsync(long id) => await ReviewAsync(id, BuyerStatus.Rejected);

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null)
        {
            buyer.IsDeleted = true;
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminDeleteBuyer",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={id}"
            });
            await _db.SaveChangesAsync();
            Message = "已删除买家。";
        }
        else
        {
            Message = "无法删除该买家。";
        }

        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ReviewAsync(long id, BuyerStatus decision)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null && buyer.BuyerStatus == BuyerStatus.PendingReview)
        {
            buyer.BuyerStatus = decision;
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminReview",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={id};decision={decision}"
            });
            await _db.SaveChangesAsync();
            Message = "已更新审核状态。";
        }
        else
        {
            Message = "无法审核该买家。";
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var query = _db.Buyers.Where(b => !b.IsDeleted);
        if (StatusFilter is not null)
        {
            query = query.Where(b => b.BuyerStatus == StatusFilter);
        }
        if (EmailFilter is not null)
        {
            query = query.Where(b => b.EmailStatus == EmailFilter);
        }
        if (!string.IsNullOrWhiteSpace(CardNo))
        {
            query = query.Where(b => b.CardNo.Contains(CardNo));
        }

        Buyers = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
    }
}
```

- [ ] **Step 4: 修改视图**

在 `src/WebMail/Pages/Admin/Buyers.cshtml` 中，`@if (!string.IsNullOrEmpty(Model.Message))` 块之后、`@if (Model.Buyers.Count == 0)` 之前，插入筛选表单：

```cshtml
<form method="get" class="row g-2 mb-3">
    <div class="col-auto">
        <select class="form-select" asp-for="StatusFilter"
                asp-items="Html.GetEnumSelectList<WebMail.Domain.BuyerStatus>()">
            <option value="">全部买家状态</option>
        </select>
    </div>
    <div class="col-auto">
        <select class="form-select" asp-for="EmailFilter"
                asp-items="Html.GetEnumSelectList<WebMail.Domain.EmailAuthorizationStatus>()">
            <option value="">全部邮箱状态</option>
        </select>
    </div>
    <div class="col-auto">
        <input class="form-control" asp-for="CardNo" placeholder="卡密关键字" />
    </div>
    <div class="col-auto">
        <button type="submit" class="btn btn-outline-primary">筛选</button>
        <a class="btn btn-outline-secondary" asp-page="/Admin/Buyers">重置</a>
    </div>
</form>
```

然后在表格每行操作单元格 `<td>` 中，现有审核按钮 `</div>`（`d-flex gap-2` 的闭合）之后、`</td>` 之前，加入删除按钮：

```cshtml
                        <form method="post" asp-page-handler="Delete" class="mt-1">
                            <input type="hidden" name="id" value="@buyer.Id" />
                            <button type="submit" class="btn btn-sm btn-outline-danger">删除</button>
                        </form>
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter AdminBuyersModelTests`
Expected: PASS（原有 3 个 + 新增 3 个）。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Pages/Admin/Buyers.cshtml.cs src/WebMail/Pages/Admin/Buyers.cshtml tests/WebMail.Tests/AdminBuyersModelTests.cs
git commit -m "feat: admin buyer soft-delete and list filtering"
```

---

### Task 6: 导航下拉菜单

**Files:**
- Modify: `src/WebMail/Pages/Shared/_Layout.cshtml`

**Interfaces:**
- Consumes: 三个管理页路由 `/Admin/Sales`、`/Admin/Suppliers`、`/Admin/Buyers`

- [ ] **Step 1: 替换管理员导航项**

在 `src/WebMail/Pages/Shared/_Layout.cshtml` 中，将现有管理员区块：

```cshtml
                        @if (User.IsInRole("Administrator"))
                        {
                            <li class="nav-item"><a class="nav-link text-dark" asp-page="/Admin/Buyers">管理后台</a></li>
                        }
```

替换为下拉菜单：

```cshtml
                        @if (User.IsInRole("Administrator"))
                        {
                            <li class="nav-item dropdown">
                                <a class="nav-link text-dark dropdown-toggle" href="#" role="button"
                                   data-bs-toggle="dropdown" aria-expanded="false">管理后台</a>
                                <ul class="dropdown-menu">
                                    <li><a class="dropdown-item" asp-page="/Admin/Sales">销售员管理</a></li>
                                    <li><a class="dropdown-item" asp-page="/Admin/Suppliers">供应商管理</a></li>
                                    <li><a class="dropdown-item" asp-page="/Admin/Buyers">买家管理</a></li>
                                </ul>
                            </li>
                        }
```

（Bootstrap bundle 已在布局底部引入，下拉无需额外脚本。）

- [ ] **Step 2: 构建确认无误**

Run: `dotnet build src/WebMail`
Expected: 构建成功，无错误。

- [ ] **Step 3: 全量测试**

Run: `dotnet test`
Expected: 全部 PASS。

- [ ] **Step 4: 提交**

```bash
git add src/WebMail/Pages/Shared/_Layout.cshtml
git commit -m "feat: admin nav dropdown for sales/suppliers/buyers"
```

---

## 验证清单

- [ ] 删除开发库（或加 `IsActive` 列）后启动应用，管理员登录可见「管理后台」下拉三项。
- [ ] 新建销售员/供应商后可登录；禁用后登录被拒「账号已被禁用」；重置密码后用新密码可登录。
- [ ] 买家页删除按钮软删除买家；状态/卡密筛选生效。
- [ ] `dotnet test` 全绿。
