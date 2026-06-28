# 卡密发送给销售（Card-Key Send-to-Sales）设计文档

- 日期：2026-06-28
- 状态：已与需求方确认，待实现
- 范围：在现有「卡密管理」页面上增加“发送给销售”的分发流程与发送状态

## 1. 背景与目标

现有「卡密管理」页面（`Pages/Admin/CardKeys.cshtml`，仅管理员可访问）支持生成、删除、按状态/销售/卡号筛选。生成时可直接选一个销售并立即写入 `SaleId`。

需求方希望把“分发给销售”作为一个**显式动作**并带独立状态：

- 生成的卡密默认是**空 / 未发送**；生成时**仍可选**销售（选了即视为已发送）。
- 支持**单个发送**和**批量发送**；发送时选择销售。
- 发送通过**弹出层（modal）**完成：可选销售、可复制卡密链接、支持批量。
- 表格内每行也支持**单张发送**与**复制链接**。
- 页面按发送状态分**两个页签**：`未发送` / `已发送`。
- **已发送的卡密不可重复发送**（不可重复使用）。

卡密对外链接格式（沿用现有）：

```
https://{域名}/?card={卡号}&saleid={销售Id}
```

## 2. 关键决策

1. **新增独立枚举 `CardSendStatus`**，与现有 `CardStatus` 正交：
   - `CardStatus`（`Unused/Entered/Authorized/DeletedOrDisabled`）管的是**买家使用生命周期**。
   - `CardSendStatus`（`NotSent/Sent`）管的是**是否已分发给销售**。
   - 两者互不替代：一张卡可以“已发送”同时仍是 `Unused`，之后买家授权后变 `Authorized`。
2. **发送状态用专门字段记录**，不靠 `SaleId` 是否为空推导。理由：需求方明确要状态枚举；专门字段能干净地实现“已发送不可重复发送”的不变量，并保留 `CardSentAt` 发送时间。
3. **生成时仍可选销售**：选了 → `CardSendStatus=Sent` + `CardSentAt`；留空 → `NotSent`，之后可再发送。
4. **发送 = 单个/批量同一方法**：`SendAsync(ids, saleId, adminId)`，单张就是只传一个 id。
5. **发送只作用于“未发送”卡**：对“已发送”卡调用是**无副作用跳过**（不报错、不覆盖原销售），落地“不可重复使用”。
6. **复制 = 完整链接**（与现有复制按钮一致）；弹出层内复制按**当前所选销售**拼 `&saleid=`，使“复制的”与“即将发送的”一致。
7. **沿用现有结构**：在 `CardKeyService` 上扩展，不新建表、不引迁移框架、不引第三方 JS（用项目已有 Bootstrap modal）。

## 3. 现有结构（事实依据）

- `Buyer`（`src/WebMail/Domain/Entities.cs:14`）：`CardNo`、`CardStatus`、`SaleId?`、`CardUsedAt?`、`IsDeleted`、`CreatedAt`。
- `CardStatus` 枚举（`Domain/Enums.cs:4`）：`Unused=1, Entered=2, Authorized=3, DeletedOrDisabled=4`。
- `CardKeyService`（`Services/CardKeyService.cs`）：`GenerateAsync` / `DeleteAsync` / `ListAsync` / `ListSalesAsync`；结果记录 `CardKeyResult(Success, Message, GeneratedCount)`；写 `AuditLog`。
- 页面 `Pages/Admin/CardKeys.cshtml(.cs)`：`[Authorize(Policy="AdminOnly")]`；生成表单（数量 + 可选销售）、筛选行、表格（含每行链接复制按钮）。
- 列表项 `CardKeyListItem`：`Id, CardNo, Status, SaleId, SaleDisplayName, CreatedAt, UsedAt`。
- 链接拼接现于 cshtml：`/?card={CardNo}`，`SaleId` 非空再加 `&saleid={SaleId}`。
- 数据库经 `EnsureCreatedAsync()` 创建（`Program.cs:77`），**无 EF 迁移**。
- 本地化资源：`Resources/SharedResource.zh-CN.resx` 与 `SharedResource.en.resx`。
- 服务测试：`tests/WebMail.Tests/CardKeyServiceTests.cs`（EF InMemory）。

## 4. 数据模型改动

**新增枚举**（`Domain/Enums.cs`）：

```csharp
public enum CardSendStatus { NotSent = 1, Sent = 2 }   // 1=空/未发送, 2=已发送给卖家
```

**`Buyer` 新增两个字段**（`Domain/Entities.cs`）：

```csharp
public CardSendStatus CardSendStatus { get; set; } = CardSendStatus.NotSent;  // 默认未发送
public DateTimeOffset? CardSentAt { get; set; }                               // 发送给销售的时间
```

`SaleId` 字段保留：发送时写入被指定销售。

> ⚠️ 部署注意：项目用 `EnsureCreated`（无迁移），**不会**在已存在的表上新增列。
> 开发环境删除 `webmail.dev.db` 重建；生产若已有数据需手动
> `ALTER TABLE Buyers ADD COLUMN CardSendStatus INTEGER NOT NULL DEFAULT 1;`
> 与 `ALTER TABLE Buyers ADD COLUMN CardSentAt TEXT NULL;`（或引入迁移）。
> 既有数据语义：`SaleId` 非空的历史卡视为已发送——重建/迁移后**必须**对任何已有卡密数据
> 一次性执行 `UPDATE Buyers SET CardSendStatus=2 WHERE SaleId IS NOT NULL;`（强制步骤，非可选）。
> 原因：否则这些已有 `SaleId` 的历史卡会显示为「未发送」并被重复发送，从而覆盖其原始销售。

## 5. 服务层改动（`CardKeyService`）

### 5.1 `GenerateAsync` 微调
保留可选销售下拉：

- `saleId` 非空（且校验为 Sales 角色）→ 新卡 `CardSendStatus=Sent`、`CardSentAt=now`、`SaleId=saleId`。
- `saleId` 为空 → 新卡 `CardSendStatus=NotSent`、`CardSentAt=null`、`SaleId=null`。
- 其余逻辑（数量校验、唯一卡号生成、审计）不变。

### 5.2 新增 `SendAsync`

```csharp
public async Task<CardKeyResult> SendAsync(
    IReadOnlyCollection<long> buyerIds, long saleId, long? actingAdminId)
```

行为：

1. `buyerIds` 为空 → 返回 `(false, "CardKey.SendNoneSelected")`。
2. 校验 `saleId` 为 `UserRole.Sales`，否则返回 `(false, "CardKey.SaleInvalid")`。
3. 加载 `Buyers` 中 `Id ∈ buyerIds && !IsDeleted && CardSendStatus == NotSent` 的记录。
   - 命中 0 条（全是已发送/不存在）→ 返回 `(false, "CardKey.SendNoneSelected")`。
4. 对命中记录：`SaleId=saleId`、`CardSendStatus=Sent`、`CardSentAt=now`。
5. 写 `AuditLog`：`Action="AdminSendCardKeys"`、`UserId=actingAdminId`、`Details=$"sale={saleId};ids={...}"`。
6. `SaveChanges`，返回 `(true, "CardKey.Sent", GeneratedCount: 命中数)`（复用 `GeneratedCount` 承载“已发送 N 张”）。

**不变量**：已发送卡永不被覆盖或重复计数；对其调用 `SendAsync` 仅是跳过。

### 5.3 `ListAsync` 增加发送状态过滤
签名加 `CardSendStatus? sendStatus`：非空则 `Where(b => b.CardSendStatus == sendStatus)`，与现有 status/sale/cardNo 过滤叠加。

### 5.4 `CardKeyListItem` 扩展
新增 `CardSendStatus SendStatus` 与 `DateTimeOffset? SentAt` 两个字段，由 `Buyer.CardSendStatus`、`Buyer.CardSentAt` 投影。

## 6. 页面与交互（`CardKeys.cshtml(.cs)`）

### 6.1 PageModel
- 新增 `[BindProperty(SupportsGet = true)] public CardSendStatus Tab { get; set; } = CardSendStatus.NotSent;`
- `OnGetAsync` 把 `Tab` 作为 `sendStatus` 传入 `ListAsync`。
- 新增 `OnPostSendAsync(long[] SelectedIds, long SendSaleId)`：调用 `SendAsync`，重载列表并 `return Page()`。
  - 成功提示带数量：`Message = _loc["CardKey.Sent", result.GeneratedCount]`（`IStringLocalizer` 支持格式参数 `{0}`）；失败提示 `Message = _loc[result.Message]`。可统一为：成功用带参重载，失败用无参。

### 6.2 生成区
保持现状（数量 + 可选销售 + 生成按钮）。

### 6.3 页签
Bootstrap nav-tabs：`未发送` / `已发送`，用普通链接（`asp-route-Tab` + 携带现有筛选 query）切换，无需 JS。

### 6.4 「未发送」页签
- 表头全选 checkbox；每行最前一个 checkbox（`data-id`、`data-cardno`）。
- 顶部「批量发送」按钮：勾选 ≥1 张后点击 → JS 收集勾中行填入 modal。
- 每行操作列：`发送`（打开 modal，仅带该行）、`复制`（复制该行链接，未发送时为 `/?card=XXXX` 无 saleid）、`删除`。

### 6.5 发送弹出层（共享 modal）
- 待发送卡号列表（确认/查看）。
- 销售下拉 `name="SendSaleId"`。
- `复制链接`按钮：JS 按当前所选销售拼每张完整链接（`/?card=XXXX&saleid=N`），多张换行，一次复制。
- `确认发送`提交：JS 把勾中 id 注入隐藏 `SelectedIds`，POST 到 `OnPostSendAsync`。
- 前端校验：未选卡/未选销售给出提示；后端亦兜底（返回 `CardKey.SendNoneSelected` / `CardKey.SaleInvalid`）。

### 6.6 「已发送」页签
- 无 checkbox、无发送入口。
- 列显示 `销售`、`发送时间`（`SentAt`）、每行 `复制`（链接已带 saleid）、`删除`。

### 6.7 筛选
状态（`CardStatus`）/ 销售 / 卡号关键字三项两页签都保留，与页签的发送状态叠加。

## 7. 本地化（zh-CN + en 各一份）

| Key | zh-CN | en |
| --- | --- | --- |
| `CardKey.Tab.NotSent` | 未发送 | Not sent |
| `CardKey.Tab.Sent` | 已发送 | Sent |
| `CardKey.Send` | 发送 | Send |
| `CardKey.SendBatch` | 批量发送 | Send selected |
| `CardKey.SendModalTitle` | 发送给销售 | Send to sales |
| `CardKey.ConfirmSend` | 确认发送 | Confirm send |
| `CardKey.CopyLink` | 复制链接 | Copy link |
| `CardKey.SentAt` | 发送时间 | Sent at |
| `CardKey.Sent` | 已发送 {0} 张 | Sent {0} card(s) |
| `CardKey.SendNoneSelected` | 请先勾选要发送的卡密 | Select at least one unsent card |
| `CardKey.SendNoSale` | 请选择销售 | Select a sales person |

销售非法继续复用现有 `CardKey.SaleInvalid`。

## 8. 测试（`CardKeyServiceTests`，EF InMemory）

- `SendAssignsSaleMarksSentAndStampsTime` — 未发送卡 → `SendAsync` → `Sent` + `SaleId` + `CardSentAt`，并写 `AuditLog`。
- `SendSkipsAlreadySentCards` — 已发送卡不被覆盖、不计数（“不可重复使用”）。
- `SendBatchSendsMultiple` — 多张未发送一次发送成功，计数正确。
- `SendRejectsNonSaleSaleId` — 返回 `CardKey.SaleInvalid`，无改动。
- `SendWithNoEligibleCards` — 空选 / 全是已发送 → 返回 `CardKey.SendNoneSelected`，计数 0。
- `GenerateWithSaleMarksSent` — 生成带销售 → `Sent` + `CardSentAt` 非空。
- `GenerateWithoutSaleIsNotSent` — 生成不带销售 → `NotSent` + `CardSentAt` 为空。
- `ListFiltersBySendStatus` — `ListAsync` 按 `CardSendStatus` 过滤命中正确。
- 更新现有 `GenerateCreatesUnusedCardsBoundToSale`、`GenerateWithoutSaleLeavesSaleIdNull` 的断言以覆盖发送状态。

页面层（`CardKeysModelTests`）：补一个 `OnPostSendAsync` 用例（成功提示 / 空选提示），与现有覆盖程度一致。

## 9. 范围外（YAGNI）

- 不做“撤回发送”/改派销售（已发送不可变，符合“不可重复使用”）。
- 不做发送记录的独立历史表（`AuditLog` 已留痕）。
- 不引 EF 迁移框架、不引第三方前端库。
- 不改买家端授权链接的处理逻辑。
