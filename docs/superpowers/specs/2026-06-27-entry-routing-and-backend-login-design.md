# 入口路由与后台登录 — 设计文档

**日期:** 2026-06-27
**目标:** 让应用「能进得去」。买家凭卡号默认进入买家界面；后台人员（管理员/销售/供应商）通过登录进入各自后台。补齐当前缺失的首页路由、登录/登出/拒绝访问页面，以及数据库初始化与默认管理员播种。

## 背景与问题

当前运行应用，根路径 `/` 显示的是 ASP.NET 脚手架自带的「Welcome」页：

- `Pages/Index.cshtml` 仍是模板默认内容，未替换。
- 导航栏（`_Layout.cshtml`）只有 Home / Privacy，没有指向任何功能页的链接。
- `Program.cs:26` 配置了 `LoginPath = "/Login"` 与 `AccessDeniedPath = "/AccessDenied"`，但这两个页面都不存在；访问带角色限制的页面（Admin/Sales/Supplier）会重定向到不存在的 `/Login` → 404。
- 没有密码哈希、没有 `SignInAsync`、没有任何已播种账号。`AppUser` 实体已定义 `UserName/Role/PasswordHash/DisplayName`，但无人写入或校验。
- `Program.cs:38` 警告：数据库未自动初始化，依赖 DB 的页面会失败。

功能页面其实已存在（`/Buyer/Email`、`/Buyer/Verify`、`/Admin/Buyers`、`/Sales/Buyers`、`/Supplier/Buyers`、`/Supplier/Mail`、`/OAuth/*`），但缺少「首页 + 登录 + DB 初始化」这层粘合，因此运行后只看到模板首页。

## 范围

**纳入:**

- 首页 `/` 改为重定向逻辑（带卡号→买家，否则→登录/后台）。
- 新增 `/Login`、`/Logout`、`/AccessDenied` 页面。
- 基于现有 Cookie 认证 + 框架自带 `PasswordHasher<AppUser>` 的密码校验与登录。
- 启动时 `EnsureCreated` 建库，并在缺失时播种一个默认管理员。
- 导航栏按登录状态/角色显示链接。

**不纳入（明确排除）:**

- 管理员管理 Sales/Supplier 账号的增删 UI（账号管理另开一轮）。
- 配置化的多账号播种、找回密码、注册流程。
- EF 迁移体系（本期用 `EnsureCreated`，迁移留待后续）。
- 生产级登录加固（验证码、登录限流、锁定等）。

## 设计

### 1. 入口路由（首页 `/`）

`IndexModel.OnGet` 改为纯重定向，不再渲染内容：

- 查询串带非空 `card`：`RedirectToPage("/Buyer/Verify", new { card, saleid })`，原样保留 `saleid`（`long?`）。`saleid` 的可信来源仍由买家记录决定，Index 仅透传给 Verify，不据此授权。
- 无 `card`：
  - 已登录（`User.Identity?.IsAuthenticated == true`）→ 按角色重定向到后台首页（见 §2 落地规则）。
  - 未登录 → `RedirectToPage("/Login")`。

现有完整链接 `/buyer/verify?card=...&saleid=1` 不受影响，照常工作；根路径只是额外入口。`Index.cshtml` 视图保留为空壳（重定向场景下不渲染）。

### 2. 后台登录

#### 2.1 `/Login`（Razor Page，允许匿名）

- 视图：用户名、密码输入框，提交按钮。可接受 `ReturnUrl` 查询参数。
- `OnPostAsync(string? returnUrl)`：
  1. 按 `UserName` 查 `AppUser`。
  2. 用 `PasswordHasher<AppUser>.VerifyHashedPassword` 校验密码。
  3. 校验失败（用户不存在或密码不符）：统一显示「用户名或密码错误」，不区分具体原因（防账号枚举）。
  4. 校验成功：构造 `ClaimsIdentity`，包含：
     - `ClaimTypes.NameIdentifier` = `AppUser.Id`
     - `ClaimTypes.Name` = `AppUser.UserName`
     - `ClaimTypes.Role` = 角色名（`UserRole` 枚举名：`Administrator`/`Sales`/`Supplier`，与 `Program.cs` 中 `RequireRole(...)` 完全对应）
     - 可附带 `DisplayName` 自定义 claim 供导航显示。
  5. `HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = true })`。
  6. 登录落地：
     - 若 `returnUrl` 非空且 `Url.IsLocalUrl(returnUrl)` → 跳回该地址。
     - 否则按角色：Administrator→`/Admin/Buyers`，Sales→`/Sales/Buyers`，Supplier→`/Supplier/Buyers`。

#### 2.2 `/Logout`（POST 处理）

- `OnPostAsync`：`HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)`，重定向到 `/Login`。
- 仅接受 POST（避免被 GET 链接/预取误触发登出）。

#### 2.3 `/AccessDenied`（Razor Page，允许匿名）

- 对应 `Program.cs` 已配置的 `AccessDeniedPath`。显示「无权访问」提示与返回链接。

#### 2.4 密码哈希

- 注册 `Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>` 为单例（`IPasswordHasher<AppUser>`）。该类型随 ASP.NET Core 提供，无需引入第三方包，算法为 PBKDF2。
- 播种与校验均通过它，保证哈希格式一致。

### 3. Cookie 选项与「记住登录」

在 `Program.cs` 的 `AddCookie` 选项中设置：

- `ExpireTimeSpan = TimeSpan.FromDays(14)`
- `SlidingExpiration = true`（每次访问自动续期）
- 登录时一律 `IsPersistent = true`（总是记住，关浏览器后仍保持，无「记住我」勾选框）。

保留现有 `LoginPath = "/Login"`、`AccessDeniedPath = "/AccessDenied"`。

### 4. 数据库初始化与播种管理员

在 `Program.cs` 的 `app.Build()` 之后、`app.Run()` 之前：

1. 创建一个 `app.Services.CreateScope()`，取出 `WebMailDbContext`。
2. 调用 `db.Database.EnsureCreated()` 建库（SQLite）。
3. 播种默认管理员：
   - 读取配置 `Seed:AdminUserName`（默认 `admin`）与 `Seed:AdminPassword`（默认 `Admin@123`）。
   - 若 `Users` 中不存在该 `UserName`，则插入：`Role=Administrator`、`PasswordHash=hasher.HashPassword(user, password)`、`DisplayName="管理员"`。
   - 已存在则跳过（不重置密码、不报错）。
4. 移除 `Program.cs:38` 现有「数据库未初始化」的警告日志。

**连接串:** 确认 `appsettings.json` 存在 `ConnectionStrings:Default`；缺失则补默认 `Data Source=webmail.db`。

> 安全提示：`admin / Admin@123` 为开发默认值，上线前必须修改。

### 5. 导航栏（`_Layout.cshtml`）

- 未登录：显示「登录」链接（指向 `/Login`）。
- 已登录：显示当前用户名（`User.Identity?.Name`）、按角色显示对应后台入口链接、「退出」按钮（POST 到 `/Logout`）。
- 买家通过 card 进入的页面（`/Buyer/*`）不携带身份，不显示后台导航项。

## 数据流

```
浏览器 GET /              ──► IndexModel.OnGet
  ├─ ?card=XXX[&saleid=Y] ──► 302 /Buyer/Verify?card=XXX&saleid=Y ──► 既有买家流程
  └─ 无 card
       ├─ 已登录          ──► 302 角色后台首页
       └─ 未登录          ──► 302 /Login

浏览器 GET /Login         ──► 登录表单
浏览器 POST /Login        ──► 校验 ─成功─► SignInAsync(persistent) ─► 302 角色落地/ReturnUrl
                                 └失败─► 重新渲染 + 「用户名或密码错误」

访问角色受限页 (未授权)   ──► Cookie 中间件 302 /Login?ReturnUrl=...
访问角色受限页 (角色不符) ──► 302 /AccessDenied

POST /Logout             ──► SignOutAsync ─► 302 /Login

启动时                    ──► EnsureCreated ─► 缺失则播种 admin
```

## 错误处理

- 登录失败：统一文案「用户名或密码错误」，不泄露账号是否存在。
- `card` 为空或无效：沿用既有 `/Buyer/Verify` 与 `/Buyer/Email` 的「链接无效或已失效」处理，Index 仅负责透传跳转。
- `ReturnUrl` 非本站相对路径：忽略，改用角色默认落地，防开放重定向。
- 播种时用户名已存在：静默跳过。

## 测试

新增测试（xUnit，沿用 `tests/WebMail.Tests` 现有模式，使用 EF InMemory/SQLite in-memory）：

- **IndexModel**：带 `card`（含 `saleid`）→ 重定向到 `/Buyer/Verify` 且参数正确；无 card 且未登录 → 重定向 `/Login`；无 card 且已登录各角色 → 对应后台首页。
- **LoginModel**：
  - 正确用户名+密码 → 调用 SignInAsync、落地到角色首页（或合法 ReturnUrl）。
  - 错误密码 / 不存在用户 → 返回页面且含统一错误文案，未登录。
  - 非本站 ReturnUrl → 忽略，走角色默认落地。
- **密码哈希往返**：`HashPassword` 后 `VerifyHashedPassword` 成功；错误密码失败。
- **播种逻辑**：空库播种创建一个 Administrator；重复运行不新增、不重置。

## 改动文件清单（预估）

- `src/WebMail/Pages/Index.cshtml(.cs)` — 改为重定向。
- `src/WebMail/Pages/Login.cshtml(.cs)` — 新增。
- `src/WebMail/Pages/Logout.cshtml(.cs)` — 新增（POST）。
- `src/WebMail/Pages/AccessDenied.cshtml(.cs)` — 新增。
- `src/WebMail/Pages/Shared/_Layout.cshtml` — 导航。
- `src/WebMail/Program.cs` — Cookie 选项、注册 PasswordHasher、EnsureCreated + 播种、移除旧警告。
- `src/WebMail/appsettings.json` — `Seed` 节点、确认 `ConnectionStrings:Default`。
- `tests/WebMail.Tests/*` — 新增上述测试。
