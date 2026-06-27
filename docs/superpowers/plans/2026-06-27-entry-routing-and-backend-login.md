# 入口路由与后台登录 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让应用能进得去——买家带卡号进买家界面，后台人员经登录进各自后台；补齐首页路由、登录/登出/拒绝访问页与 DB 初始化＋默认管理员播种。

**Architecture:** 复用项目已配置的 Cookie 认证与 `AppUser` 实体。新增两个纯逻辑帮助类（角色落地路由 + 本地 URL 校验、管理员播种）便于单测；首页 `/` 改为重定向；新增 `/Login`、`/Logout`、`/AccessDenied` Razor Pages；`Program.cs` 启动时 `EnsureCreated` 并播种 admin。

**Tech Stack:** ASP.NET Core 8 Razor Pages、Cookie 认证、`Microsoft.AspNetCore.Identity.PasswordHasher<T>`（框架自带，PBKDF2）、EF Core (SQLite 运行 / InMemory 测试)、xUnit。

## Global Constraints

- 目标框架 `net8.0`，可空引用启用（`<Nullable>enable</Nullable>`）。
- 角色 Claim 值必须用 `UserRole` 枚举名 `Administrator`/`Sales`/`Supplier`，与 `Program.cs` 中 `RequireRole(...)` 完全一致。
- 不引入任何第三方 NuGet 包；密码哈希用框架自带 `PasswordHasher<AppUser>`。
- 不建立 EF 迁移；本期用 `Database.EnsureCreated()`。
- 登录失败统一文案「用户名或密码错误」，不区分原因。
- 登录一律 `IsPersistent = true`；Cookie `ExpireTimeSpan = 14 天`、`SlidingExpiration = true`。
- 默认管理员 `admin` / `Admin@123`，来自 `appsettings.json` 的 `Seed` 节点，仅缺失时播种、不重置。
- 测试沿用现有模式：`UseInMemoryDatabase(Guid.NewGuid().ToString("N"))`，手写假对象（项目无 Moq）。
- 仓库在 `master` 分支做特性开发，无远程。提交信息以 `Co-Authored-By: Claude <noreply@anthropic.com>` 结尾。

---

## File Structure

- `src/WebMail/Services/Auth/AuthRouting.cs` (新建) — 纯静态：角色→落地页、本地 URL 校验。
- `src/WebMail/Services/Auth/IdentitySeeder.cs` (新建) — 静态：缺失时播种默认管理员（幂等）。
- `src/WebMail/Pages/Index.cshtml` + `.cs` (改写) — 重定向逻辑。
- `src/WebMail/Pages/Login.cshtml` + `.cs` (新建) — 登录表单 + 校验 + SignIn。
- `src/WebMail/Pages/Logout.cshtml` + `.cs` (新建) — POST 登出。
- `src/WebMail/Pages/AccessDenied.cshtml` + `.cs` (新建) — 拒绝访问页。
- `src/WebMail/Pages/Shared/_Layout.cshtml` (改) — 导航按登录状态/角色显示。
- `src/WebMail/Program.cs` (改) — Cookie 选项、注册 hasher、EnsureCreated + 播种、移除旧警告。
- `src/WebMail/appsettings.json` (改) — 新增 `Seed` 节点。
- `tests/WebMail.Tests/WebMail.Tests.csproj` (改) — 增加 `FrameworkReference`。
- `tests/WebMail.Tests/TestAuth.cs` (新建) — 假 `IAuthenticationService` + HttpContext 帮助。
- `tests/WebMail.Tests/AuthRoutingTests.cs` (新建)
- `tests/WebMail.Tests/IdentitySeederTests.cs` (新建)
- `tests/WebMail.Tests/IndexModelTests.cs` (新建)
- `tests/WebMail.Tests/LoginModelTests.cs` (新建)
- `tests/WebMail.Tests/LogoutModelTests.cs` (新建)

---

### Task 1: 角色落地路由与本地 URL 校验帮助类

**Files:**
- Create: `src/WebMail/Services/Auth/AuthRouting.cs`
- Test: `tests/WebMail.Tests/AuthRoutingTests.cs`

**Interfaces:**
- Consumes: `WebMail.Domain.UserRole`
- Produces:
  - `static string WebMail.Services.Auth.AuthRouting.LandingPage(string? role)` — 返回角色后台首页路径；未知/空返回 `"/Login"`。
  - `static bool WebMail.Services.Auth.AuthRouting.IsLocalUrl(string? url)` — 仅站内相对地址为 true。

- [ ] **Step 1: Write the failing test**

Create `tests/WebMail.Tests/AuthRoutingTests.cs`:

```csharp
using WebMail.Services.Auth;
using Xunit;

namespace WebMail.Tests;

public sealed class AuthRoutingTests
{
    [Theory]
    [InlineData("Administrator", "/Admin/Buyers")]
    [InlineData("Sales", "/Sales/Buyers")]
    [InlineData("Supplier", "/Supplier/Buyers")]
    [InlineData(null, "/Login")]
    [InlineData("Bogus", "/Login")]
    public void LandingPageMapsRole(string? role, string expected)
        => Assert.Equal(expected, AuthRouting.LandingPage(role));

    [Theory]
    [InlineData("/Admin/Buyers", true)]
    [InlineData("/", true)]
    [InlineData("~/x", true)]
    [InlineData("//evil.com", false)]
    [InlineData("/\\evil.com", false)]
    [InlineData("https://evil.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalUrlGuardsOpenRedirect(string? url, bool expected)
        => Assert.Equal(expected, AuthRouting.IsLocalUrl(url));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WebMail.Tests --filter AuthRoutingTests`
Expected: 编译失败（`AuthRouting` 不存在）。

- [ ] **Step 3: Write minimal implementation**

Create `src/WebMail/Services/Auth/AuthRouting.cs`:

```csharp
using WebMail.Domain;

namespace WebMail.Services.Auth;

public static class AuthRouting
{
    // 角色 Claim 值 → 对应后台首页路径。未知或空回退到登录页。
    public static string LandingPage(string? role) => role switch
    {
        nameof(UserRole.Administrator) => "/Admin/Buyers",
        nameof(UserRole.Sales) => "/Sales/Buyers",
        nameof(UserRole.Supplier) => "/Supplier/Buyers",
        _ => "/Login",
    };

    // 仅站内相对地址返回 true（等价于 IUrlHelper.IsLocalUrl），用于阻断开放重定向。
    public static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        if (url[0] == '/')
        {
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }
        if (url.Length > 1 && url[0] == '~' && url[1] == '/')
        {
            return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/WebMail.Tests --filter AuthRoutingTests`
Expected: PASS（全部用例通过）。

- [ ] **Step 5: Commit**

```bash
git add src/WebMail/Services/Auth/AuthRouting.cs tests/WebMail.Tests/AuthRoutingTests.cs
git commit -m "feat: add role landing routing and local-url guard

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 默认管理员播种帮助类

**Files:**
- Create: `src/WebMail/Services/Auth/IdentitySeeder.cs`
- Modify: `tests/WebMail.Tests/WebMail.Tests.csproj`
- Test: `tests/WebMail.Tests/IdentitySeederTests.cs`

**Interfaces:**
- Consumes: `WebMail.Data.WebMailDbContext`、`WebMail.Domain.AppUser`、`WebMail.Domain.UserRole`、`Microsoft.AspNetCore.Identity.IPasswordHasher<AppUser>`
- Produces:
  - `static Task WebMail.Services.Auth.IdentitySeeder.EnsureAdminSeededAsync(WebMailDbContext db, IPasswordHasher<AppUser> hasher, string userName, string password)` — 若不存在同名用户则插入一个 Administrator；幂等。

- [ ] **Step 1: 给测试项目加 FrameworkReference**

测试项目是 `Microsoft.NET.Sdk`（非 Web），需显式引用 ASP.NET Core 共享框架，才能在测试中使用 `PasswordHasher<AppUser>`、`IAuthenticationService`、`ServiceCollection` 等类型。

Modify `tests/WebMail.Tests/WebMail.Tests.csproj`，在 `<ItemGroup>`（含 PackageReference 的那个）后新增一个 ItemGroup：

```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

Create `tests/WebMail.Tests/IdentitySeederTests.cs`:

```csharp
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.Auth;
using Xunit;

namespace WebMail.Tests;

public sealed class IdentitySeederTests
{
    [Fact]
    public async Task SeedsAdminWhenMissing()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();

        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "admin", "Admin@123");

        var user = await db.Users.SingleAsync();
        Assert.Equal("admin", user.UserName);
        Assert.Equal(UserRole.Administrator, user.Role);
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, user.PasswordHash, "Admin@123"));
    }

    [Fact]
    public async Task SeedIsIdempotentAndKeepsOriginalPassword()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();

        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "admin", "Admin@123");
        await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, "admin", "different");

        Assert.Equal(1, await db.Users.CountAsync());
        var user = await db.Users.SingleAsync();
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(user, user.PasswordHash, "Admin@123"));
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/WebMail.Tests --filter IdentitySeederTests`
Expected: 编译失败（`IdentitySeeder` 不存在）。

- [ ] **Step 4: Write minimal implementation**

Create `src/WebMail/Services/Auth/IdentitySeeder.cs`:

```csharp
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
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/WebMail.Tests --filter IdentitySeederTests`
Expected: PASS（2 个用例通过）。

- [ ] **Step 6: Commit**

```bash
git add src/WebMail/Services/Auth/IdentitySeeder.cs tests/WebMail.Tests/IdentitySeederTests.cs tests/WebMail.Tests/WebMail.Tests.csproj
git commit -m "feat: add idempotent default-admin seeder

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: 首页 `/` 改为重定向

**Files:**
- Modify: `src/WebMail/Pages/Index.cshtml.cs`（改写）
- Modify: `src/WebMail/Pages/Index.cshtml`（改写为空壳）
- Test: `tests/WebMail.Tests/IndexModelTests.cs`

**Interfaces:**
- Consumes: `AuthRouting.LandingPage` (Task 1)
- Produces: `IndexModel.OnGet(string? card, long? saleid)` 返回 `IActionResult`（`RedirectToPageResult`）。

- [ ] **Step 1: Write the failing test**

Create `tests/WebMail.Tests/IndexModelTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Pages;
using Xunit;

namespace WebMail.Tests;

public sealed class IndexModelTests
{
    [Fact]
    public void RedirectsToBuyerVerifyWhenCardPresent()
    {
        var model = new IndexModel { PageContext = Ctx(Anonymous()) };

        var result = Assert.IsType<RedirectToPageResult>(model.OnGet("CARD123", 7));

        Assert.Equal("/Buyer/Verify", result.PageName);
        Assert.Equal("CARD123", result.RouteValues!["card"]);
        Assert.Equal(7L, result.RouteValues!["saleid"]);
    }

    [Fact]
    public void RedirectsToLoginWhenNoCardAndAnonymous()
    {
        var model = new IndexModel { PageContext = Ctx(Anonymous()) };

        var result = Assert.IsType<RedirectToPageResult>(model.OnGet(null, null));

        Assert.Equal("/Login", result.PageName);
    }

    [Fact]
    public void RedirectsToRoleLandingWhenAuthenticated()
    {
        var model = new IndexModel { PageContext = Ctx(WithRole("Sales")) };

        var result = Assert.IsType<RedirectToPageResult>(model.OnGet(null, null));

        Assert.Equal("/Sales/Buyers", result.PageName);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
    private static ClaimsPrincipal WithRole(string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
    private static PageContext Ctx(ClaimsPrincipal user) =>
        new() { HttpContext = new DefaultHttpContext { User = user } };
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WebMail.Tests --filter IndexModelTests`
Expected: 编译失败（`OnGet` 签名不匹配 / 无参构造不存在）。

- [ ] **Step 3: Write minimal implementation**

Replace `src/WebMail/Pages/Index.cshtml.cs` 全文：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Services.Auth;

namespace WebMail.Pages;

public class IndexModel : PageModel
{
    // 带卡号 → 买家入口；否则已登录按角色落地、未登录去登录页。
    public IActionResult OnGet(string? card, long? saleid)
    {
        if (!string.IsNullOrWhiteSpace(card))
        {
            return RedirectToPage("/Buyer/Verify", new { card, saleid });
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage(AuthRouting.LandingPage(User.FindFirstValue(ClaimTypes.Role)));
        }

        return RedirectToPage("/Login");
    }
}
```

Replace `src/WebMail/Pages/Index.cshtml` 全文（重定向页不渲染内容）：

```cshtml
@page
@model IndexModel
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/WebMail.Tests --filter IndexModelTests`
Expected: PASS（3 个用例通过）。

- [ ] **Step 5: Commit**

```bash
git add src/WebMail/Pages/Index.cshtml src/WebMail/Pages/Index.cshtml.cs tests/WebMail.Tests/IndexModelTests.cs
git commit -m "feat: redirect home by card presence and auth state

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 登录页（含假认证服务测试夹具）

**Files:**
- Create: `tests/WebMail.Tests/TestAuth.cs`
- Create: `src/WebMail/Pages/Login.cshtml.cs`
- Create: `src/WebMail/Pages/Login.cshtml`
- Test: `tests/WebMail.Tests/LoginModelTests.cs`

**Interfaces:**
- Consumes: `WebMailDbContext`、`IPasswordHasher<AppUser>`、`AuthRouting.LandingPage`、`AuthRouting.IsLocalUrl` (Task 1)
- Produces:
  - `LoginModel(WebMailDbContext db, IPasswordHasher<AppUser> hasher)`；属性 `[BindProperty] UserName`、`[BindProperty] Password`、`[BindProperty(SupportsGet=true)] ReturnUrl`、`ErrorMessage`；`Task<IActionResult> OnPostAsync()`。
  - 测试夹具 `WebMail.Tests.FakeAuthenticationService`（记录 SignIn/SignOut）与 `WebMail.Tests.TestHttpContext.WithAuth(ClaimsPrincipal?)`。

- [ ] **Step 1: 写测试夹具**

Create `tests/WebMail.Tests/TestAuth.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WebMail.Tests;

// 记录式假认证服务：SignInAsync / SignOutAsync 扩展方法会从 RequestServices 解析它。
public sealed class FakeAuthenticationService : IAuthenticationService
{
    public ClaimsPrincipal? SignedInPrincipal { get; private set; }
    public AuthenticationProperties? SignInProperties { get; private set; }
    public bool SignedOut { get; private set; }

    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        => Task.FromResult(AuthenticateResult.NoResult());

    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        => Task.CompletedTask;

    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        => Task.CompletedTask;

    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
    {
        SignedInPrincipal = principal;
        SignInProperties = properties;
        return Task.CompletedTask;
    }

    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignedOut = true;
        return Task.CompletedTask;
    }
}

public static class TestHttpContext
{
    public static (DefaultHttpContext ctx, FakeAuthenticationService auth) WithAuth(ClaimsPrincipal? user = null)
    {
        var auth = new FakeAuthenticationService();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(auth);
        var ctx = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };
        return (ctx, auth);
    }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/WebMail.Tests/LoginModelTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages;
using Xunit;

namespace WebMail.Tests;

public sealed class LoginModelTests
{
    [Fact]
    public async Task ValidCredentialsSignInPersistentAndRedirectToRoleLanding()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "pw",
        };

        var result = await model.OnPostAsync();

        Assert.Equal("/Sales/Buyers", Assert.IsType<RedirectToPageResult>(result).PageName);
        Assert.NotNull(auth.SignedInPrincipal);
        Assert.True(auth.SignInProperties!.IsPersistent);
        Assert.Equal("Sales", auth.SignedInPrincipal!.FindFirstValue(ClaimTypes.Role));
    }

    [Fact]
    public async Task WrongPasswordShowsErrorAndDoesNotSignIn()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "wrong",
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal("用户名或密码错误", model.ErrorMessage);
        Assert.Null(auth.SignedInPrincipal);
    }

    [Fact]
    public async Task UnknownUserShowsErrorAndDoesNotSignIn()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "nobody",
            Password = "pw",
        };

        var result = await model.OnPostAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal("用户名或密码错误", model.ErrorMessage);
        Assert.Null(auth.SignedInPrincipal);
    }

    [Fact]
    public async Task LocalReturnUrlIsHonored()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, _) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "pw",
            ReturnUrl = "/Admin/Buyers",
        };

        var result = await model.OnPostAsync();

        Assert.Equal("/Admin/Buyers", Assert.IsType<RedirectResult>(result).Url);
    }

    [Fact]
    public async Task NonLocalReturnUrlIsIgnored()
    {
        await using var db = CreateDb();
        var hasher = new PasswordHasher<AppUser>();
        await SeedUser(db, hasher, "sue", "pw", UserRole.Sales);
        var (ctx, _) = TestHttpContext.WithAuth();
        var model = new LoginModel(db, hasher)
        {
            PageContext = new PageContext { HttpContext = ctx },
            UserName = "sue",
            Password = "pw",
            ReturnUrl = "https://evil.com",
        };

        var result = await model.OnPostAsync();

        Assert.Equal("/Sales/Buyers", Assert.IsType<RedirectToPageResult>(result).PageName);
    }

    private static async Task SeedUser(WebMailDbContext db, PasswordHasher<AppUser> hasher, string name, string pw, UserRole role)
    {
        var user = new AppUser { UserName = name, Role = role, DisplayName = name };
        user.PasswordHash = hasher.HashPassword(user, pw);
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/WebMail.Tests --filter LoginModelTests`
Expected: 编译失败（`LoginModel` 不存在）。

- [ ] **Step 4: Write minimal implementation**

Create `src/WebMail/Pages/Login.cshtml.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.Auth;

namespace WebMail.Pages;

public class LoginModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;

    public LoginModel(WebMailDbContext db, IPasswordHasher<AppUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [BindProperty] public string UserName { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName == UserName);
        if (user is null ||
            _hasher.VerifyHashedPassword(user, user.PasswordHash, Password) == PasswordVerificationResult.Failed)
        {
            ErrorMessage = "用户名或密码错误";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("DisplayName", user.DisplayName),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        if (AuthRouting.IsLocalUrl(ReturnUrl))
        {
            return Redirect(ReturnUrl!);
        }
        return RedirectToPage(AuthRouting.LandingPage(user.Role.ToString()));
    }
}
```

Create `src/WebMail/Pages/Login.cshtml`:

```cshtml
@page
@model LoginModel
@{
    ViewData["Title"] = "登录";
}

<div class="row justify-content-center">
    <div class="col-md-4">
        <h1 class="h3 mb-3">后台登录</h1>
        @if (Model.ErrorMessage is not null)
        {
            <div class="alert alert-danger" role="alert">@Model.ErrorMessage</div>
        }
        <form method="post">
            <input type="hidden" asp-for="ReturnUrl" />
            <div class="mb-3">
                <label class="form-label" asp-for="UserName">用户名</label>
                <input class="form-control" asp-for="UserName" autofocus />
            </div>
            <div class="mb-3">
                <label class="form-label" asp-for="Password">密码</label>
                <input class="form-control" type="password" asp-for="Password" />
            </div>
            <button type="submit" class="btn btn-primary w-100">登录</button>
        </form>
    </div>
</div>
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/WebMail.Tests --filter LoginModelTests`
Expected: PASS（5 个用例通过）。

- [ ] **Step 6: Commit**

```bash
git add tests/WebMail.Tests/TestAuth.cs tests/WebMail.Tests/LoginModelTests.cs src/WebMail/Pages/Login.cshtml src/WebMail/Pages/Login.cshtml.cs
git commit -m "feat: add backend login page with cookie sign-in

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 登出与拒绝访问页

**Files:**
- Create: `src/WebMail/Pages/Logout.cshtml.cs`
- Create: `src/WebMail/Pages/Logout.cshtml`
- Create: `src/WebMail/Pages/AccessDenied.cshtml.cs`
- Create: `src/WebMail/Pages/AccessDenied.cshtml`
- Test: `tests/WebMail.Tests/LogoutModelTests.cs`

**Interfaces:**
- Consumes: `TestHttpContext.WithAuth` (Task 4)
- Produces: `LogoutModel.OnPostAsync()` 登出并跳 `/Login`；`AccessDeniedModel`（无逻辑）。

- [ ] **Step 1: Write the failing test**

Create `tests/WebMail.Tests/LogoutModelTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Pages;
using Xunit;

namespace WebMail.Tests;

public sealed class LogoutModelTests
{
    [Fact]
    public async Task LogoutSignsOutAndRedirectsToLogin()
    {
        var (ctx, auth) = TestHttpContext.WithAuth();
        var model = new LogoutModel { PageContext = new PageContext { HttpContext = ctx } };

        var result = await model.OnPostAsync();

        Assert.Equal("/Login", Assert.IsType<RedirectToPageResult>(result).PageName);
        Assert.True(auth.SignedOut);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WebMail.Tests --filter LogoutModelTests`
Expected: 编译失败（`LogoutModel` 不存在）。

- [ ] **Step 3: Write minimal implementation**

Create `src/WebMail/Pages/Logout.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebMail.Pages;

public class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
```

Create `src/WebMail/Pages/Logout.cshtml`:

```cshtml
@page
@model LogoutModel
```

Create `src/WebMail/Pages/AccessDenied.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebMail.Pages;

public class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
```

Create `src/WebMail/Pages/AccessDenied.cshtml`:

```cshtml
@page
@model AccessDeniedModel
@{
    ViewData["Title"] = "无权访问";
}

<div class="text-center">
    <h1 class="h3">无权访问</h1>
    <p>当前账号没有访问该页面的权限。</p>
    <a asp-page="/Index">返回首页</a>
</div>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/WebMail.Tests --filter LogoutModelTests`
Expected: PASS（1 个用例通过）。

- [ ] **Step 5: Commit**

```bash
git add src/WebMail/Pages/Logout.cshtml src/WebMail/Pages/Logout.cshtml.cs src/WebMail/Pages/AccessDenied.cshtml src/WebMail/Pages/AccessDenied.cshtml.cs tests/WebMail.Tests/LogoutModelTests.cs
git commit -m "feat: add logout and access-denied pages

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: Program.cs 接线 + appsettings 播种配置

**Files:**
- Modify: `src/WebMail/Program.cs`
- Modify: `src/WebMail/appsettings.json`

**Interfaces:**
- Consumes: `IdentitySeeder.EnsureAdminSeededAsync` (Task 2)、`IPasswordHasher<AppUser>`
- Produces: 启动时建库并播种 admin；DI 中注册 `IPasswordHasher<AppUser>`；Cookie 选项含 14 天 + 滑动过期。

> 本任务为接线/集成，无单测；以 `dotnet build` 与手动运行验证。

- [ ] **Step 1: 加 appsettings 的 Seed 节点**

Modify `src/WebMail/appsettings.json`，在 `"ConnectionStrings"` 那一行之后新增一行：

```json
  "Seed": { "AdminUserName": "admin", "AdminPassword": "Admin@123" },
```

（放在 `"ConnectionStrings": { ... },` 与 `"MailSync": { ... }` 之间，保持 JSON 合法。）

- [ ] **Step 2: 注册 PasswordHasher 并扩展 Cookie 选项**

Modify `src/WebMail/Program.cs`：在顶部 using 区补充：

```csharp
using Microsoft.AspNetCore.Identity;
using WebMail.Domain;
using WebMail.Services.Auth;
```

把现有第 26 行的单行 `AddAuthentication(...).AddCookie(...)` 替换为：

```csharp
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});
```

- [ ] **Step 3: 用 EnsureCreated + 播种替换旧的开发警告块**

Modify `src/WebMail/Program.cs`：删除现有这段（`var app = builder.Build();` 之后的开发警告块）：

```csharp
if (app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("Database is not auto-initialized (migrations deferred). DB-backed pages and the mail sync tick will fail until migrations/EnsureCreated are added.");
}
```

替换为：

```csharp
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
    await db.Database.EnsureCreatedAsync();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
    var seedUserName = builder.Configuration["Seed:AdminUserName"] ?? "admin";
    var seedPassword = builder.Configuration["Seed:AdminPassword"] ?? "Admin@123";
    await IdentitySeeder.EnsureAdminSeededAsync(db, hasher, seedUserName, seedPassword);
}
```

（`WebMail.Data` 已在文件顶部 using 中。）

- [ ] **Step 4: 构建并跑全量测试**

Run: `dotnet build WebMail.sln`
Expected: Build succeeded，0 Error。

Run: `dotnet test WebMail.sln`
Expected: 全部通过（含既有测试）。

- [ ] **Step 5: 手动冒烟验证（人工）**

Run: `dotnet run --project src/WebMail`
- 浏览器开 `http://localhost:5230/` → 应 302 到 `/Login`。
- 用 `admin` / `Admin@123` 登录 → 应跳到 `/Admin/Buyers`。
- 开 `http://localhost:5230/?card=anything` → 应 302 到 `/Buyer/Verify?card=anything`。
- 项目根目录应生成 `webmail.dev.db`（含 `Users` 表与一条 admin）。
Expected: 上述行为符合预期；确认后停止应用。

- [ ] **Step 6: Commit**

```bash
git add src/WebMail/Program.cs src/WebMail/appsettings.json
git commit -m "feat: init db and seed default admin on startup

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: 导航栏按登录状态/角色显示

**Files:**
- Modify: `src/WebMail/Pages/Shared/_Layout.cshtml:20-29`

**Interfaces:**
- Consumes: Cookie 身份 Claims（角色名、`User.Identity.Name`）、`/Login`、`/Logout`、各后台页。

> 视图改动，无单测；以 `dotnet build` 与手动观察验证。

- [ ] **Step 1: 替换导航的 collapse 区块**

Modify `src/WebMail/Pages/Shared/_Layout.cshtml`：把现有 `<div class="navbar-collapse ...">...</div>`（第 20–29 行那段）整体替换为：

```cshtml
                <div class="navbar-collapse collapse d-sm-inline-flex justify-content-between">
                    <ul class="navbar-nav flex-grow-1">
                        <li class="nav-item">
                            <a class="nav-link text-dark" asp-area="" asp-page="/Index">Home</a>
                        </li>
                        @if (User.IsInRole("Administrator"))
                        {
                            <li class="nav-item"><a class="nav-link text-dark" asp-page="/Admin/Buyers">管理后台</a></li>
                        }
                        @if (User.IsInRole("Sales"))
                        {
                            <li class="nav-item"><a class="nav-link text-dark" asp-page="/Sales/Buyers">销售后台</a></li>
                        }
                        @if (User.IsInRole("Supplier"))
                        {
                            <li class="nav-item"><a class="nav-link text-dark" asp-page="/Supplier/Buyers">供应商后台</a></li>
                        }
                    </ul>
                    <ul class="navbar-nav">
                        @if (User.Identity?.IsAuthenticated == true)
                        {
                            <li class="nav-item"><span class="nav-link text-dark">@User.Identity!.Name</span></li>
                            <li class="nav-item">
                                <form method="post" asp-page="/Logout" class="d-inline">
                                    <button type="submit" class="btn btn-link nav-link text-dark">退出</button>
                                </form>
                            </li>
                        }
                        else
                        {
                            <li class="nav-item"><a class="nav-link text-dark" asp-page="/Login">登录</a></li>
                        }
                    </ul>
                </div>
```

- [ ] **Step 2: 构建**

Run: `dotnet build WebMail.sln`
Expected: Build succeeded，0 Error。

- [ ] **Step 3: 手动观察（人工）**

Run: `dotnet run --project src/WebMail`
- 未登录访问 `/Login` 页 → 导航右侧显示「登录」。
- 登录后 → 导航显示用户名、对应角色后台链接、「退出」；点「退出」回到 `/Login`。
Expected: 符合预期。

- [ ] **Step 4: Commit**

```bash
git add src/WebMail/Pages/Shared/_Layout.cshtml
git commit -m "feat: show role-aware nav with login/logout

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- 入口路由（§1）→ Task 3 ✓
- 登录页 / SignIn / 失败文案 / 角色落地 / ReturnUrl（§2.1）→ Task 4 ✓
- 登出（§2.2）→ Task 5 ✓
- AccessDenied（§2.3）→ Task 5 ✓
- 密码哈希注册（§2.4）→ Task 6 ✓（类型与播种由 Task 2 提供）
- Cookie 14 天 + 滑动 + 持久（§3）→ Task 6（选项）+ Task 4（IsPersistent）✓
- DB 初始化 + 播种 admin + 连接串（§4）→ Task 6（连接串已存在 `Data Source=webmail.dev.db`，无需新增；spec 的「缺失则补」条件不触发）✓
- 导航（§5）→ Task 7 ✓
- 测试矩阵（§测试）→ Task 1/2/3/4/5 ✓

**Placeholder scan:** 无 TBD/TODO；所有代码步骤含完整代码。✓

**Type consistency:** `AuthRouting.LandingPage`/`IsLocalUrl`、`IdentitySeeder.EnsureAdminSeededAsync`、`LoginModel(WebMailDbContext, IPasswordHasher<AppUser>)`、`FakeAuthenticationService`/`TestHttpContext.WithAuth` 在定义与使用处签名一致；角色 Claim 统一用 `user.Role.ToString()`（= 枚举名）与 `RequireRole`/`AuthRouting` 匹配。✓

**Note:** 连接串当前为 `Data Source=webmail.dev.db`，与 spec 中「缺失则补 webmail.db」不冲突——已存在即沿用，无需改动。
