# 设计：管理员后台 — 销售员 / 供应商 / 买家管理

日期：2026-06-27
状态：已确认（待写实现计划）

## 背景

`AppUser` 是带角色（Administrator / Sales / Supplier）的登录账号。当前**没有任何管理界面**来创建或维护销售员、供应商账号——只有启动时播种的默认管理员能登录。

买家（`Buyer`）是独立的卡号实体，不是登录账号。`/Admin/Buyers` 页面已存在，支持审核通过/拒绝。

本设计为管理员后台补齐三块管理能力：销售员管理、供应商管理、买家管理。

## 范围（需求确认结论）

- **销售员管理 / 供应商管理**：新建账号、查看列表、重置密码、启用/禁用。
- **买家管理**：保留现有审核通过/拒绝；新增管理员软删除买家、列表筛选/搜索。
- **不在范围内**：管理员重新分配买家的销售员/供应商；供应商-买家分配关系的创建管理；销售员/供应商账号的删除（用禁用替代）。

## 架构（方案 A：按角色分页 + 共享服务）

销售员与供应商管理逻辑几乎相同（同为 `AppUser` 增/查/重置密码/启停用，仅 `Role` 不同），公共逻辑抽到共享服务，页面只负责展示与表单。每角色一个页面，沿用现有「Pages 下按角色分目录」约定。

## 1. 数据模型变更

- `AppUser` 新增字段：`public bool IsActive { get; set; } = true;`（默认启用）。
- 无需新增表。`UserRole` 已含 Sales / Supplier。
- 项目用 `EnsureCreatedAsync()`，无 EF 迁移。新字段不会自动加到已有 SQLite 库。实现计划须说明：开发环境删库重建，或手动执行
  `ALTER TABLE Users ADD COLUMN IsActive INTEGER NOT NULL DEFAULT 1;`

## 2. 共享服务 `UserAdminService`

放在 `Services/` 下，Scoped 注册，依赖 `WebMailDbContext` 与 `IPasswordHasher<AppUser>`。纯逻辑，可独立单测。

方法：

- `CreateAsync(UserRole role, string userName, string displayName, string password)`
  - 校验：用户名非空；用户名唯一（沿用现有大小写不敏感比较 `UserName.ToLower()`）；密码长度 ≥ 6。
  - 成功：哈希密码，写入 `AppUser`（`IsActive = true`），写 `AuditLog`（Action 如 `"AdminCreateUser"`，Details 含 role/userName）。
  - 返回携带成功标志与提示信息的结果（成功消息或具体错误：用户名为空 / 已存在 / 密码过短）。
- `ResetPasswordAsync(long userId, string newPassword)`
  - 校验密码长度 ≥ 6；重置 `PasswordHash`；写 `AuditLog`。
- `SetActiveAsync(long userId, bool isActive)`
  - 设置 `IsActive`；写 `AuditLog`。
- `ListByRoleAsync(UserRole role)`
  - 返回该角色账号列表，每项含关联买家数：
    - 销售员：`Buyers.Count(b => !b.IsDeleted && b.SaleId == user.Id)`
    - 供应商：`BuyerSupplierAssignments.Count(a => a.SupplierId == user.Id && !a.Buyer.IsDeleted)`
  - 返回视图所需的轻量投影（用户名、显示名、IsActive、关联买家数、创建时间、Id）。

所有写操作经 `SaveChangesAsync` 持久化。审计记录沿用现有 `AuditLog` 模式，`UserId` 取当前管理员（由调用方页面传入）。

## 3. 管理页面

两页均 `[Authorize(Policy = "AdminOnly")]`，PageModel 极薄，注入 `UserAdminService`，各自固定一个角色。

- `/Admin/Sales`（`Pages/Admin/Sales.cshtml` + `.cs`）— 角色 Sales。
- `/Admin/Suppliers`（`Pages/Admin/Suppliers.cshtml` + `.cs`）— 角色 Supplier。

页面元素：
- 列表表格列：用户名、显示名、状态（启用/禁用）、关联买家数、创建时间、操作。
- 新建表单：用户名、显示名、初始密码。
- 每行操作：重置密码（输入新密码提交）、启用/禁用切换。
- 操作结果通过 `Message` 文本反馈（沿用现有页面风格）。

PageModel 处理器：`OnGetAsync`（加载列表）、`OnPostCreateAsync`、`OnPostResetPasswordAsync`、`OnPostSetActiveAsync`，均委托给 `UserAdminService`，并把当前管理员 Id 传入用于审计。

## 4. 买家页 `/Admin/Buyers` 增强

- 保留现有 `OnPostApproveAsync` / `OnPostRejectAsync`。
- 新增 `OnPostDeleteAsync(long id)`：将未删除买家置 `IsDeleted = true`，写 `AuditLog`（管理员可删任意未删除买家）。
- 新增筛选/搜索：`OnGetAsync` 读取查询参数
  - `BuyerStatus?`（按买家审核状态过滤）
  - `EmailStatus?`（按邮箱授权状态过滤）
  - `cardNo`（卡号关键字，包含匹配）
  - 服务端在查询中应用过滤，未传则不过滤。
- 视图新增筛选表单（GET）与删除按钮（POST）。

## 5. 登录校验禁用账号

`Login.cshtml.cs` 在凭据校验通过后，增加判断：若 `AppUser.IsActive == false`，拒绝登录并提示「账号已禁用」，不签发 Cookie。这是禁用功能真正生效的关键点。

## 6. 导航

`_Layout.cshtml` 管理员区现为单个「管理后台」链接。改为一个 Bootstrap 下拉菜单「管理后台」，展开三项：**销售员管理**（/Admin/Sales）、**供应商管理**（/Admin/Suppliers）、**买家管理**（/Admin/Buyers）。仅 Administrator 可见，沿用现有 `User.IsInRole("Administrator")` 判断。

## 7. 测试

沿用现有 xUnit + SQLite 测试风格：

- `UserAdminServiceTests`：创建成功；重名拒绝；弱密码拒绝；重置密码；启用/禁用；列表关联买家计数（销售员按 SaleId、供应商按 assignment）。
- `AdminSalesModelTests` / `AdminSuppliersModelTests`：GET 加载列表；各 POST 处理器调用服务且角色正确。
- 扩展 `AdminBuyersModelTests`：删除置 `IsDeleted`；筛选按状态/卡号过滤。
- `LoginModelTests`：禁用账号被拒登录，启用账号正常登录。

## 决策记录

- 导航采用下拉菜单（而非三个并列链接）。
- 密码最小长度 = 6。
- 禁用销售员/供应商仅阻止其**后续登录**；已持有有效 Cookie 的会话不强制登出，自然过期即可（不在本次范围内做主动踢出）。不影响已关联买家的展示与邮件同步。
- 销售员/供应商不提供删除，只提供禁用（避免 `Buyer.SaleId` / assignment 出现孤立引用）。
