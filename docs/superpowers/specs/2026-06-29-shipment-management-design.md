# 发货表管理（Shipment Management）设计文档

- 日期：2026-06-29
- 状态：已与需求方确认，待实现
- 范围：新增「发货记录」实体与增删查流程；管理员与供应商可在买家邮箱页为某买家新增/删除发货，发货含图片、描述、随机发货单ID、关联买家

## 1. 背景与目标

需求方希望为每个买家记录「发货」信息，每条发货包含：

- 一张**图片**（如发货凭证/商品照）
- 一段**描述**
- 一个**发货单ID**（原述「订单ID」，确认改为系统生成的发货单号）
- **关联的买家**（BuyerId）

权限与入口：

- **管理员**与**供应商**都可以**新增**和**删除**发货。
- 入口为「买家邮箱页」：点击买家行进入其邮箱页（现 `/Supplier/Mail`），工具栏增加「增加发货」按钮，弹出层（modal）填写图片+描述提交。
- 该买家的历史发货记录在**同页**列出（缩略图、发货单ID、描述、时间、删除按钮）。

## 2. 关键决策

1. **复用现有买家邮箱页，不新建管理员页**：将 `/Supplier/Mail` 的授权从「仅供应商」放宽为「管理员 或 供应商」，页内按角色分支——供应商走现有「供应商-买家分配」校验，管理员直接放行（可查看任意买家）。`Admin/Buyers` 列表行点击进入同一页面。最大化复用、零新增页面。
2. **发货单ID 用雪花ID（Snowflake）**：新增 `SnowflakeIdGenerator`（单例、线程安全；时间戳+worker位+序列）。`epoch = 2024-01-01 UTC`，`workerId = 1`（单实例够用，将来多实例可配）。`ShipmentNo` 存为 `long`，时间有序、可解出日期，比纯随机串有意义。
3. **图片必须登录才能查看**：图片**不放 `wwwroot`**，存到 `wwwroot` 之外（`storage/shipments/`）；文件名用 `日期时间+随机串`。新增带 `[Authorize]` 的图片接口 `/Shipment/Image/{id}`：按 shipment id 找到记录 → 校验「管理员 或 该买家的分配供应商」→ 流式返回文件。`<img src>` 指向该接口。避免未授权访问与路径穿越。
4. **图片校验**：内容类型限 `image/jpeg`、`image/png`、`image/webp`、`image/gif`；大小 ≤ 5MB；按内容类型决定落盘扩展名。
5. **硬删除**：发货记录是操作型数据，删除时连同磁盘文件一并删除，并写 `AuditLog`。（买家用软删因其为核心实体；发货记录硬删更干净。）
6. **沿用现有结构**：在 EF + SQLite 上加一个实体与 `DbSet`，不引入 EF 迁移框架、不引第三方 JS（用项目已有 Bootstrap modal）。
7. **既有库的建表问题**：项目用 `EnsureCreatedAsync()`，它**不会**给已存在的库新增表。在启动建库之后，对 `Shipments` 执行一句幂等 `CREATE TABLE IF NOT EXISTS`（列与 EF 模型一致，`CreatedAt` 按项目约定存 UTC ticks 的 `INTEGER`），保证现有开发库数据不丢、新表也能建出来。

## 3. 现有结构（事实依据）

- `Buyer`（`Domain/Entities.cs:14`）：`CardNo`、`Stage`、`ReviewStatus`、`EmailStatus`、`IsDeleted`、`CreatedAt` 等。
- `BuyerSupplierAssignment`（`Domain/Entities.cs:67`）：`BuyerId`、`SupplierId`、导航属性 `Buyer`；`BuyerId` 唯一索引。
- `AuditLog`（`Domain/Entities.cs:94`）：`Action`、`UserId?`、`Details`、`CreatedAt`。
- `WebMailDbContext`（`Data/WebMailDbContext.cs`）：所有 `DbSet`；`OnModelCreating` 配索引；**全局 `DateTimeOffset → UTC ticks(long)` 值转换器**。
- `/Supplier/Mail`（`Pages/Supplier/Mail.cshtml(.cs)`）：`[Authorize(Policy="SupplierOnly")]`；`OnGetAsync(long buyerId)` 先校验该买家已分配给当前供应商且已审核通过/邮箱已授权，再列出邮件、刷新 `ActiveSyncWindow`。工具栏现仅「返回列表」。
- `Pages/Supplier/_BuyerTable.cshtml`：买家行内「查看邮件」按钮 `asp-page="/Supplier/Mail" asp-route-buyerId`。
- `Pages/Admin/Buyers.cshtml(.cs)`：`[Authorize(Policy="AdminOnly")]`；列表 + 删除/审核 handler；无按买家的邮箱页。
- `CardGenerationService`（`Services/CardGenerationService.cs`）：`RandomNumberGenerator` 风格随机串，可作随机后缀参考。
- 服务注册与策略（`Program.cs`）：`AddScoped`/`AddSingleton`；`UseStaticFiles()`；策略 `AdminOnly`/`SalesOnly`/`SupplierOnly`；DB 经 `EnsureCreatedAsync()`（`Program.cs:77`），**无 EF 迁移**。
- 本地化：`Resources/SharedResource.zh-CN.resx` 与 `SharedResource.en.resx`；视图用 `L["..."]`。
- 测试：`tests/WebMail.Tests/*`，服务用 EF InMemory（如 `CardKeyServiceTests`），页面用 PageModel 直测（如 `SupplierBuyersModelTests`、`AdminBuyersModelTests`）。

## 4. 数据模型改动

**新增实体**（`Domain/Entities.cs`）：

```csharp
public sealed class Shipment
{
    public long Id { get; set; }
    public long BuyerId { get; set; }            // 关联买家
    public long ShipmentNo { get; set; }         // 发货单ID，雪花ID，唯一
    public string StoredFileName { get; set; } = string.Empty; // 磁盘文件名（不含目录），形如 20260629T142233123_a1b2c3.jpg
    public string ContentType { get; set; } = string.Empty;    // image/jpeg 等，供图片接口回写响应头
    public string Description { get; set; } = string.Empty;    // 描述
    public long? CreatedByUserId { get; set; }   // 谁发的货
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

**`WebMailDbContext`**：

```csharp
public DbSet<Shipment> Shipments => Set<Shipment>();
// OnModelCreating:
modelBuilder.Entity<Shipment>().HasIndex(x => x.BuyerId);
modelBuilder.Entity<Shipment>().HasIndex(x => x.ShipmentNo).IsUnique();
```

> 说明：图片不存数据库，只存磁盘文件名 + 内容类型；图片接口据此定位文件并设置响应头。

## 5. 服务层

### 5.1 `SnowflakeIdGenerator`（单例）

- 字段：`epoch = 2024-01-01T00:00:00Z`，`workerId = 1`（11 bit 内），`sequence`（12 bit），上次时间戳。
- `long NextId()`：`lock` 保护；同毫秒内自增 `sequence`，溢出则自旋到下一毫秒；时钟回拨则抛异常或等待（取保守：等待到不回拨）。
- 组装：`((nowMs - epochMs) << 22) | (workerId << 12) | sequence`。
- 用 `DateTimeOffset.UtcNow` 取时间（应用内可用）。

### 5.2 `ShipmentService`（Scoped）

依赖：`WebMailDbContext`、`SnowflakeIdGenerator`、文件存储根路径（来自配置/环境）。

- `Task<IReadOnlyList<Shipment>> GetForBuyerAsync(long buyerId)`：按 `CreatedAt` 倒序。
- `Task<ShipmentResult> CreateAsync(long buyerId, string? description, IFormFile? image, long? userId)`：
  1. 校验 image 非空、类型在白名单、大小 ≤ 5MB；否则返回失败。
  2. 生成 `StoredFileName = {yyyyMMddTHHmmssfff}_{6位随机}.{ext}`，写入 `storage/shipments/`（目录不存在则建）。
  3. `ShipmentNo = snowflake.NextId()`；落库；写 `AuditLog(Action="CreateShipment", UserId, Details="buyer={buyerId};shipment={ShipmentNo}")`。
- `Task<bool> DeleteAsync(long shipmentId, long? userId)`：删库记录 + 删磁盘文件（文件缺失忽略）；写 `AuditLog(Action="DeleteShipment", ...)`。
- `Task<Shipment?> GetByIdAsync(long shipmentId)`：供图片接口/删除取记录。
- 结果类型：`record ShipmentResult(bool Success, string MessageKey)`，`MessageKey` 为本地化键。

> 授权不在服务里做（服务只管数据与文件）；「管理员 或 分配供应商」的判断放在调用方（页面/接口），复用同一个私有辅助。

### 5.3 授权辅助（共享）

在需要处（图片接口、邮箱页 handler）使用统一判断：

```
bool isAdmin = User.IsInRole("Administrator");
bool ok = isAdmin || await _db.BuyerSupplierAssignments
    .AnyAsync(x => x.BuyerId == buyerId && x.SupplierId == userId);
```

供应商额外沿用现有「审核通过 + 邮箱已授权」前置条件（与现 `MailModel` 一致）。

## 6. 页面与路由改动

### 6.1 邮箱页授权放宽 + 角色分支（`Pages/Supplier/Mail.cshtml.cs`）

- 授权由 `[Authorize(Policy="SupplierOnly")]` 改为允许管理员或供应商：新增策略 `SupplierOrAdmin`（`RequireRole("Administrator","Supplier")`）并应用。
- `OnGetAsync(long buyerId)`：取当前用户 id 与角色；
  - 管理员：跳过分配校验，直接加载该买家邮件 + 发货记录。
  - 供应商：沿用现有分配/审核/授权校验。
  - 两者都加载 `Model.Shipments = await _shipmentService.GetForBuyerAsync(buyerId)`。
  - `ActiveSyncWindow` 刷新逻辑保留（对两角色一致，用于拉取最新邮件）。
- 新增 handler：
  - `OnPostAddShipmentAsync(long buyerId, string? description, IFormFile? image)`：先做授权辅助校验 → `ShipmentService.CreateAsync` → 设置提示消息 → 重定向回本页（PRG，避免刷新重复提交）。
  - `OnPostDeleteShipmentAsync(long shipmentId)`：取记录 → 用其 `BuyerId` 做授权校验 → `DeleteAsync` → 重定向回本页。

### 6.2 视图（`Pages/Supplier/Mail.cshtml`）

- 工具栏增加「增加发货」按钮，触发 Bootstrap modal：`<form method="post" enctype="multipart/form-data" asp-page-handler="AddShipment">`，含隐藏 `buyerId`、`<input type="file" name="image">`、描述 `<textarea name="description">`。
- 邮件表下方新增「发货记录」表：缩略图（`<img src="/Shipment/Image/{id}">`）、发货单ID（`ShipmentNo`）、描述、时间、删除按钮（`asp-page-handler="DeleteShipment"` + 隐藏 `shipmentId`，带确认）。
- 抽出共享 partial `Pages/Shared/_ShipmentSection.cshtml`（模型含 `BuyerId` + `IReadOnlyList<Shipment>`），便于将来其他页复用。

### 6.3 管理员入口（`Pages/Admin/Buyers.cshtml`）

- 列表行增加「查看邮件 / 进入」按钮：`asp-page="/Supplier/Mail" asp-route-buyerId="@buyer.Id"`，进入复用后的邮箱页。

### 6.4 图片接口（`Pages/Shipment/Image.cshtml.cs`）

- 路由 `/Shipment/Image/{id}`（Razor Page，`@page "{id:long}"`），`[Authorize]`（登录即过初筛）。
- `OnGetAsync(long id)`：取 `Shipment` → 用 `BuyerId` 做「管理员 或 分配供应商」校验 → 不过返回 `Forbid()` / 找不到返回 `NotFound()` → 否则 `PhysicalFile(fullPath, shipment.ContentType)` 流式返回。
- 文件路径由存储根 + `StoredFileName` 拼出；只用文件名拼接，杜绝外部传入路径。

## 7. 配置与启动（`Program.cs`）

- 注册：`AddSingleton<SnowflakeIdGenerator>()`、`AddScoped<ShipmentService>()`。
- 存储根：`storage/shipments/`，路径来自 `IWebHostEnvironment.ContentRootPath` 拼接（可由 `appsettings` 配置项 `Shipments:StoragePath` 覆盖，默认 `storage/shipments`）。
- 新增授权策略 `SupplierOrAdmin`。
- `EnsureCreatedAsync()` 之后执行幂等建表：

```sql
CREATE TABLE IF NOT EXISTS "Shipments" (
  "Id" INTEGER NOT NULL CONSTRAINT "PK_Shipments" PRIMARY KEY AUTOINCREMENT,
  "BuyerId" INTEGER NOT NULL,
  "ShipmentNo" INTEGER NOT NULL,
  "StoredFileName" TEXT NOT NULL,
  "ContentType" TEXT NOT NULL,
  "Description" TEXT NOT NULL,
  "CreatedByUserId" INTEGER NULL,
  "CreatedAt" INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_Shipments_BuyerId" ON "Shipments" ("BuyerId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Shipments_ShipmentNo" ON "Shipments" ("ShipmentNo");
```

（仅为兼容**已存在**的开发库；全新库由 `EnsureCreated` 直接建出，两者列定义需保持一致。）

## 8. 本地化（resx，zh-CN + en）

新增键（示例）：

- `Action.AddShipment`：增加发货 / Add Shipment
- `Shipment.SectionTitle`：发货记录 / Shipments
- `Shipment.Image`：图片 / Image
- `Shipment.Description`：描述 / Description
- `Shipment.No`：发货单ID / Shipment No.
- `Shipment.CreatedAt`：发货时间 / Shipped At
- `Shipment.Submit`：提交发货 / Submit
- `Shipment.Delete`：删除 / Delete
- `Shipment.DeleteConfirm`：确定删除此发货记录？/ Delete this shipment?
- `Shipment.Added`：发货已添加 / Shipment added
- `Shipment.Deleted`：发货已删除 / Shipment deleted
- `Shipment.InvalidImage`：图片无效（类型或大小不符）/ Invalid image (type or size)
- `Shipment.AddFailed`：发货添加失败 / Failed to add shipment

## 9. 测试计划（TDD）

**`SnowflakeIdGeneratorTests`**：

- 连续生成 ID 单调递增且唯一（含高频循环 N 个无重复）。
- 解出的时间戳落在合理范围（≥ epoch、接近当前）。

**`ShipmentServiceTests`**（EF InMemory + 临时目录）：

- `CreateAsync` 合法图片：落库一条、文件写入、`ShipmentNo` 非零、写审计。
- `CreateAsync` 无图片 / 类型非法 / 超大：返回失败，不落库、不写文件。
- `DeleteAsync`：记录与文件均被删除、写审计；文件缺失时不抛错。
- `GetForBuyerAsync`：仅返回该买家、按时间倒序。

**`SupplierMailModelTests`（或扩展现有）**：

- 供应商对**已分配**买家：进入成功、可加载发货；对**未分配**买家：`Forbid`。
- 管理员：对任意买家进入成功、可新增/删除发货。
- `OnPostAddShipmentAsync` 未授权买家：拒绝、不创建。
- `OnPostDeleteShipmentAsync`：删他人买家的发货被拒（按记录的 BuyerId 校验）。

**`ShipmentImageModelTests`**：

- 管理员取任意图片：成功返回文件。
- 供应商取已分配买家图片：成功；未分配：`Forbid`；不存在：`NotFound`。

## 10. 不做（YAGNI）

- 不做发货编辑（只增/删）。
- 不做多图、不做缩略图压缩（`<img>` 直接缩放显示）。
- 不引入 EF 迁移框架、不引第三方上传/JS 组件。
- 不做多实例 worker id 分配（默认 1，留作配置项即可）。
