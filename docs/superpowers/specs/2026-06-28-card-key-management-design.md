# 卡密管理（Card-Key Management）设计文档

- 日期：2026-06-28
- 状态：已与需求方确认，待实现
- 范围：管理员卡密管理页面 + 相关流程补缺

## 1. 背景与目标

管理员需要一个卡密管理入口，能够：

- **生成卡密**（支持批量，单次上限 100 张）
- **删除卡密**（软删除）
- 查看**是否使用**、**使用时间**、**所属销售**

卡密对外格式（买家辛权链接）：

```
https://{域名}/?card={卡号}&saleid={销售Id}
```

> 注：现有 `Index.OnGet(string? card, long? saleid)` 的查询参数名是 `saleid`（非
> `salesid`）。本设计统一沿用 `saleid`。

## 2. 关键决策

1. **复用现有 `Buyer` 体系**，不新建独立卡密表。管理员"生成卡密"= 创建一条
   `Buyer` 记录（`CardStatus=Unused`）。买家用链接授权邮箱后，同一条记录变为
   `CardStatus=Authorized`，即"已使用"。
2. **销售绑定 = 两者结合**：生成时可选指定销售；若留空，则以买家辛权链接里的
   `saleid` 为准（在 `Verify` 流程回写）。
3. **删除 = 软删除**：`IsDeleted=true` 且 `CardStatus=DeletedOrDisabled`，保留记录与
   关联邮箱。
4. **实现方式 = 独立 `CardKeyService`**，仿照现有 `UserAdminService`：返回
   `(Success, 本地化消息key)` 记录，写审计日志，便于单测。

## 3. 现有结构（事实依据）

- `Buyer`（`src/WebMail/Domain/Entities.cs:14`）：`CardNo`、`CardStatus`、`SaleId?`、
  `EmailStatus`、`BuyerStatus`、`IsDeleted`、`CreatedAt`。
- `CardStatus` 枚举（`Domain/Enums.cs:4`）：`Unused=1, Entered=2, Authorized=3,
  DeletedOrDisabled=4`。
- `Buyer.CardNo` 有**唯一索引**（`Data/WebMailDbContext.cs:22`）。
- `CardGenerationService`（`Services/CardGenerationService.cs`）：`GenerateCardNo(length=32)`，
  最短 24 位随机串；已注册为 singleton，但此前未被任何页面使用。
- 数据库经 `EnsureCreatedAsync()` 创建（`Program.cs`），**无 EF 迁移**。
- 管理页参考：`Pages/Admin/Buyers.cshtml.cs`（筛选 + 软删除 + 审计）。
- 服务层参考：`Services/UserAdminService.cs`（结果记录 + 本地化 key + 审计）。
- 授权回调：`Pages/OAuth/Callback.cshtml.cs:95` 处设 `CardStatus=Authorized`。
- 买家入口：`Pages/Buyer/Verify.cshtml.cs:24` 接收 `saleid` 但**未回写** `Buyer.SaleId`
  （本设计补上）。

## 4. 数据模型改动

给 `Buyer` 新增一个字段（其余复用）：

```csharp
public DateTimeOffset? CardUsedAt { get; set; }   // 卡密首次授权（使用）时间
```

派生语义：

- **是否使用** = `CardStatus == CardStatus.Authorized`
- **使用时间** = `CardUsedAt`
- **所属销售** = `SaleId`（关联 `AppUser`，角色 Sales）

> ⚠️ 部署注意：项目用 `EnsureCreated`（无迁移），它**不会**在已存在的表上新增列。
> 开发环境需删除 `webmail.dev.db` 让其重建；若生产已有数据，需手动 `ALTER TABLE`
> 或引入迁移。文档在此明确标注，避免"加了字段但运行时报错/列不存在"。

## 5. 新增 `CardKeyService`

位置：`src/WebMail/Services/CardKeyService.cs`，DI 注册（scoped，与 DbContext 一致）。

```csharp
public sealed record CardKeyResult(bool Success, string Message, int GeneratedCount = 0);

public sealed record CardKeyListItem(
    long Id,
    string CardNo,
    CardStatus Status,
    long? SaleId,
    string? SaleDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UsedAt);
```

方法：

- `GenerateAsync(int count, long? saleId, long? actingAdminId)`
  - 校验 `count` 在 `1..MaxGenerateCount`（`MaxGenerateCount = 100`），否则返回
    `(false, "CardKey.CountInvalid")`。
  - 若 `saleId` 给定：必须存在且 `Role==Sales`，否则 `(false, "CardKey.SaleInvalid")`。
  - 循环用 `CardGenerationService.GenerateCardNo()` 生成卡号；**对唯一索引冲突做重试**
    （内存去重 + 入库前查重，单张重试若干次后仍冲突则报错兜底）。
  - 批量 `Buyers.Add`（`CardStatus=Unused`、`SaleId=saleId`、`CardUsedAt=null`）。
  - 写一条审计：`Action="AdminGenerateCardKeys"`，`Details="count={n};sale={saleId}"`。
  - 返回 `(true, "CardKey.Generated", n)`。
- `DeleteAsync(long id, long? actingAdminId)`
  - 取未删除 `Buyer`；置 `IsDeleted=true`、`CardStatus=DeletedOrDisabled`。
  - 审计：`Action="AdminDeleteCardKey"`，`Details="buyer={id}"`。
  - 返回 `(true, "CardKey.Deleted")` 或 `(false, "CardKey.DeleteFailed")`。
- `ListAsync(CardStatus? status, long? saleId, string? cardNo)`
  - 基线 `Where(b => !b.IsDeleted)`（已删除不展示）。
  - 依次按 `status`、`saleId`、`cardNo.Contains` 过滤。
  - 关联 `Users` 取销售显示名，按 `CreatedAt` 倒序返回 `CardKeyListItem` 列表。
- `ListSalesAsync()`：返回角色为 Sales 的用户（Id + 显示名），供生成表单与筛选下拉用。

## 6. 新增页面 `Pages/Admin/CardKeys`

`CardKeysModel : PageModel`，`[Authorize(Policy="AdminOnly")]`，注入 `CardKeyService` +
`IStringLocalizer<SharedResource>`。

| 处理器 | 作用 |
|---|---|
| `OnGetAsync` | 加载列表（含筛选）+ 销售下拉 |
| `OnPostGenerateAsync` | 调 `GenerateAsync(GenerateCount, GenerateSaleId, adminId)`，设 `Message`，重载 |
| `OnPostDeleteAsync(long id)` | 调 `DeleteAsync`，设 `Message`，重载 |

绑定属性：

- `[BindProperty(SupportsGet=true)] CardStatus? StatusFilter`
- `[BindProperty(SupportsGet=true)] long? SaleFilter`
- `[BindProperty(SupportsGet=true)] string? CardNo`
- `[BindProperty] int GenerateCount`（默认 1）
- `[BindProperty] long? GenerateSaleId`

管理员 Id 取自 `User.FindFirstValue(ClaimTypes.NameIdentifier)`（与现有页一致）。

### 视图 `Pages/Admin/CardKeys.cshtml`

- 顶部生成表单：数量输入（1–100）+ 销售下拉（可空）+ 生成按钮。
- 筛选表单（GET）：状态下拉 + 销售下拉 + 卡号输入。
- 列表表格每行：卡号、状态徽章、所属销售名、创建时间、**使用时间**、**可复制完整链接**、
  删除按钮（每行独立 `asp-page-handler="Delete"` 表单，带隐藏 `id`）。
- 完整链接构造：`{Request.Scheme}://{Request.Host}/?card={CardNo}` + 若有 `SaleId` 则
  追加 `&saleid={SaleId}`。提供"复制"按钮（前端 `navigator.clipboard`）。

### 导航

`Pages/Shared/_Layout.cshtml` 管理员下拉菜单新增"卡密管理"（`/Admin/CardKeys`）。

## 7. 补现有流程缺口

1. **SaleId 回写** —— `Pages/Buyer/Verify.cshtml.cs`：
   在更新 `CardStatus` 的同处，加：
   ```csharp
   if (buyer.SaleId is null && saleid is not null)
       buyer.SaleId = saleid;
   ```
   （生成时已绑定销售的卡密不被链接参数覆盖。）

2. **使用时间写入** —— `Pages/OAuth/Callback.cshtml.cs`（设 `Authorized` 处）：
   ```csharp
   buyer.CardStatus = CardStatus.Authorized;
   buyer.CardUsedAt ??= DateTimeOffset.UtcNow;   // 仅记首次授权
   ```

## 8. 本地化 key（zh-CN / en 两份 resx）

至少包含：

- `Admin.CardKeys.Title`、`Nav.CardKeys`
- `CardKey.Generate`、`CardKey.Count`、`CardKey.Sale`、`CardKey.SaleUnassigned`
- `CardKey.Generated`、`CardKey.CountInvalid`、`CardKey.SaleInvalid`
- `CardKey.Delete`、`CardKey.Deleted`、`CardKey.DeleteFailed`
- `CardKey.Link`、`CardKey.Copy`、`CardKey.UsedAt`、`CardKey.NotUsed`
- `CardKey.StatusFilter`、`CardKey.SaleFilter`
- 卡密状态文案：未使用 / 已进入 / 已使用 / 已删除（复用或新增）

## 9. 测试

- `CardKeyServiceTests`（新建，用内存/SQLite in-memory DbContext）：
  - 批量生成数量正确、`CardStatus=Unused`、`SaleId` 正确写入。
  - `count<1` 或 `>100` 被拒。
  - 指定不存在/非 Sales 的 `saleId` 被拒。
  - 生成的卡号互不重复且唯一。
  - `DeleteAsync` 软删除（`IsDeleted` + `DeletedOrDisabled`），不存在时失败。
  - `ListAsync` 按状态/销售/卡号筛选正确，排除已删除。
- `VerifyModel` 测试：链接带 `saleid` 且 `Buyer.SaleId` 为空时回写；已绑定时不覆盖。
- `OAuthCallbackModelTests`（扩展现有）：首次授权写入 `CardUsedAt` 且不被二次授权覆盖。

## 10. 不做（YAGNI）

- 不做卡密导出/导入、不做有效期/过期、不做硬删除、不做卡密编辑。
- 不引入仓储层或 EF 迁移框架（沿用项目现状 `EnsureCreated`）。

## 11. 涉及文件清单

新增：

- `src/WebMail/Services/CardKeyService.cs`
- `src/WebMail/Pages/Admin/CardKeys.cshtml`(+`.cs`)
- `tests/WebMail.Tests/CardKeyServiceTests.cs`

修改：

- `src/WebMail/Domain/Entities.cs`（加 `CardUsedAt`）
- `src/WebMail/Program.cs`（注册 `CardKeyService`）
- `src/WebMail/Pages/Buyer/Verify.cshtml.cs`（回写 `SaleId`）
- `src/WebMail/Pages/OAuth/Callback.cshtml.cs`（写 `CardUsedAt`）
- `src/WebMail/Pages/Shared/_Layout.cshtml`（导航）
- `src/WebMail/Resources/SharedResource.zh-CN.resx` / `.en.resx`（本地化）
- `tests/WebMail.Tests/OAuthCallbackModelTests.cs`（扩展断言）
