# 买家流程线 / 审核状态拆分 + 按卡免审核 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 `CardStatus` + `CardSendStatus` + `BuyerStatus(NotSubmitted部分)` 合并为一条流程线 `BuyerStage`，把审核结论独立为 `ReviewStatus`，并支持按卡密「免审核」（授权后直接通过）。

**Architecture:** `Buyer` 实体用 `Stage`（流程）+ `ReviewStatus`（审核）+ `EmailStatus`（邮箱健康，不变）+ `SupplierStatus`（不变）+ `AutoApprove`（免审核标记）取代旧的 `CardStatus`/`CardSendStatus`/`BuyerStatus`。规则仍由 `BuyerRuleService` 计算。程序未上线，DB 走 `EnsureCreated`，无迁移（开发库需删库重建）。

**Tech Stack:** .NET / ASP.NET Core Razor Pages, EF Core (SQLite + InMemory for tests), xUnit。

## Global Constraints

- 枚举数值从 1 起编号（与现有约定一致）：`BuyerStage { NotSent=1, Sent=2, NotSubmitted=3, Submitted=4 }`、`ReviewStatus { Pending=1, Approved=2, Rejected=3 }`。
- 不新增数据库迁移；不保留任何兼容旧字段的过渡代码。
- 枚举值在视图中沿用现有「直接渲染 `ToString()`」风格（除 `SupplierStatus` 用 `_SupplierStatusBadge` 分区），不为每个枚举值新增本地化键。
- C# 整个 `WebMail` 程序集是一次性编译，Task 1 必须把 `src` 全部改完并迁移全部既有测试到绿；中途不要求可编译。
- 设计依据：`docs/superpowers/specs/2026-06-29-buyer-stage-review-split-design.md`。

---

## 状态/字段映射表（贯穿全程，尤其测试迁移用）

旧 → 新（构造测试买家、读改代码时统一套用）：

| 旧 | 新 |
|---|---|
| `CardStatus.Unused`（库存/未录卡） | `Stage = BuyerStage.NotSent`（无销售）或 `BuyerStage.Sent`（已发销售） |
| `CardStatus.Entered` | `Stage = BuyerStage.NotSubmitted` |
| `CardStatus.Authorized` | `Stage = BuyerStage.Submitted` |
| `CardStatus.DeletedOrDisabled` | 删除（软删除用 `IsDeleted`） |
| `CardSendStatus.NotSent` | `Stage = BuyerStage.NotSent` |
| `CardSendStatus.Sent` | `Stage = BuyerStage.Sent`（或更靠后） |
| `BuyerStatus.NotSubmitted` | 由 `Stage<Submitted` 表达；`ReviewStatus = Pending` |
| `BuyerStatus.PendingReview` | `Stage = Submitted` + `ReviewStatus = Pending` |
| `BuyerStatus.Approved` | `Stage = Submitted` + `ReviewStatus = Approved` |
| `BuyerStatus.Rejected` | `Stage = Submitted` + `ReviewStatus = Rejected` |

---

## File Structure

- `src/WebMail/Domain/Enums.cs` — 删 `CardStatus`/`CardSendStatus`，加 `BuyerStage`，`BuyerStatus`→`ReviewStatus`。
- `src/WebMail/Domain/Entities.cs` — `Buyer` 字段重组 + 新增 `AutoApprove`。
- `src/WebMail/Services/BuyerRuleService.cs` — 规则改用 `Stage`/`ReviewStatus`。
- `src/WebMail/Services/CardKeyService.cs` — 生成/发送/列表改用 `Stage`；`CardKeyListItem` 重构；Task 2 加 `autoApprove`。
- `src/WebMail/Pages/Buyer/Verify.cshtml.cs` — 录卡 `Stage` 转换。
- `src/WebMail/Pages/OAuth/Callback.cshtml.cs` — 授权成功 `Stage=Submitted`；Task 3 加免审核分支。
- `src/WebMail/Pages/OAuth/Start.cshtml.cs` — 移除死守卫。
- `src/WebMail/Pages/Buyer/Email.cshtml(.cs)` — 字段更名 + 换/清授权写新字段。
- `src/WebMail/Pages/Admin/Buyers.cshtml(.cs)` — 加载/审核/筛选/两列显示。
- `src/WebMail/Pages/Admin/CardKeys.cshtml(.cs)` — Tab/筛选/列改 `Stage`；Task 2 加免审核勾选与列。
- `src/WebMail/Pages/Sales/Buyers.cshtml`、`src/WebMail/Pages/Supplier/_BuyerTable.cshtml`、`src/WebMail/Pages/Supplier/Mail.cshtml.cs`、`src/WebMail/Pages/Supplier/Buyers.cshtml.cs` — 字段更新 + 两列。
- `src/WebMail/Resources/SharedResource.en.resx` / `.zh-CN.resx` — 新增本地化键。
- `tests/WebMail.Tests/*` — 按映射表迁移 + 新增免审核用例。

---

## Task 1: 状态模型整体替换（域 + 全部消费方 + 既有测试迁移到绿）

**Files:**
- Modify: `src/WebMail/Domain/Enums.cs`
- Modify: `src/WebMail/Domain/Entities.cs`
- Modify: `src/WebMail/Services/BuyerRuleService.cs`
- Modify: `src/WebMail/Services/CardKeyService.cs`
- Modify: `src/WebMail/Pages/Buyer/Verify.cshtml.cs`
- Modify: `src/WebMail/Pages/Buyer/Email.cshtml.cs`, `src/WebMail/Pages/Buyer/Email.cshtml`
- Modify: `src/WebMail/Pages/OAuth/Callback.cshtml.cs`, `src/WebMail/Pages/OAuth/Start.cshtml.cs`
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml.cs`, `src/WebMail/Pages/Admin/Buyers.cshtml`
- Modify: `src/WebMail/Pages/Admin/CardKeys.cshtml.cs`, `src/WebMail/Pages/Admin/CardKeys.cshtml`
- Modify: `src/WebMail/Pages/Sales/Buyers.cshtml`
- Modify: `src/WebMail/Pages/Supplier/_BuyerTable.cshtml`, `src/WebMail/Pages/Supplier/Mail.cshtml.cs`, `src/WebMail/Pages/Supplier/Buyers.cshtml.cs`
- Modify: `src/WebMail/Resources/SharedResource.en.resx`, `src/WebMail/Resources/SharedResource.zh-CN.resx`
- Modify: all affected `tests/WebMail.Tests/*.cs`

**Interfaces:**
- Produces:
  - `enum BuyerStage { NotSent=1, Sent=2, NotSubmitted=3, Submitted=4 }`
  - `enum ReviewStatus { Pending=1, Approved=2, Rejected=3 }`
  - `Buyer.Stage : BuyerStage`、`Buyer.ReviewStatus : ReviewStatus`、`Buyer.AutoApprove : bool`
  - `CardKeyService.ListAsync(BuyerStage? stage, long? saleId, string? cardNo, bool sentTab)`
  - `CardKeyListItem(long Id, string CardNo, BuyerStage Stage, bool AutoApprove, long? SaleId, string? SaleDisplayName, DateTimeOffset CreatedAt, DateTimeOffset? UsedAt, DateTimeOffset? SentAt)`
  - `enum CardKeyTab { NotSent=1, Sent=2 }`（仅 UI，定义在 `CardKeys.cshtml.cs`）

- [ ] **Step 1: 改枚举 `Domain/Enums.cs`**

把第 4、6、10 行的三个枚举替换为：

```csharp
public enum UserRole { Administrator = 1, Sales = 2, Supplier = 3 }
public enum EmailAuthorizationStatus { NotAuthorized = 1, Authorized = 2, Abnormal = 3 }
public enum BuyerStage { NotSent = 1, Sent = 2, NotSubmitted = 3, Submitted = 4 }
public enum ReviewStatus { Pending = 1, Approved = 2, Rejected = 3 }
public enum SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }
public enum SyncJobStatus { Pending = 1, Running = 2, Succeeded = 3, Failed = 4 }
public enum MailFolder { Inbox = 1, Junk = 2 }
```

（删除 `CardStatus`、`CardSendStatus`。）

- [ ] **Step 2: 改实体 `Domain/Entities.cs` 的 `Buyer`（第 14-28 行）**

替换为：

```csharp
public sealed class Buyer
{
    public long Id { get; set; }
    public string CardNo { get; set; } = string.Empty;
    public BuyerStage Stage { get; set; } = BuyerStage.NotSent;
    public bool AutoApprove { get; set; }
    public DateTimeOffset? CardSentAt { get; set; }
    public long? SaleId { get; set; }
    public EmailAuthorizationStatus EmailStatus { get; set; } = EmailAuthorizationStatus.NotAuthorized;
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
    public SupplierProcessingStatus SupplierStatus { get; set; } = SupplierProcessingStatus.Unprocessed;
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CardUsedAt { get; set; }
}
```

- [ ] **Step 3: 改 `Services/BuyerRuleService.cs`**

把 `CanSalesDeleteBuyer`、`CanSupplierViewBuyer`、`ResolveBuyerMailAction` 三处主体替换为：

```csharp
public bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId) =>
    !buyer.IsDeleted
    && buyer.SaleId == salesUserId
    && buyer.EmailStatus != EmailAuthorizationStatus.Abnormal
    && !(buyer.ReviewStatus == ReviewStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Unprocessed);

public bool CanSupplierViewBuyer(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
    !buyer.IsDeleted
    && buyer.ReviewStatus == ReviewStatus.Approved
    && buyer.EmailStatus == EmailAuthorizationStatus.Authorized
    && assignedSupplierId == currentSupplierId;

public BuyerMailAction ResolveBuyerMailAction(Buyer buyer)
{
    if (buyer.IsDeleted)
    {
        return BuyerMailAction.None;
    }

    if (buyer.EmailStatus == EmailAuthorizationStatus.Abnormal)
    {
        return BuyerMailAction.ReAuthorize | BuyerMailAction.ChangeEmail;
    }

    return buyer.Stage switch
    {
        BuyerStage.NotSubmitted => BuyerMailAction.Authorize,
        BuyerStage.Submitted => buyer.ReviewStatus switch
        {
            ReviewStatus.Pending or ReviewStatus.Rejected => BuyerMailAction.ChangeEmail | BuyerMailAction.ClearAuth,
            ReviewStatus.Approved => buyer.SupplierStatus switch
            {
                SupplierProcessingStatus.Failed => BuyerMailAction.ChangeEmail,
                SupplierProcessingStatus.Completed => buyer.EmailStatus == EmailAuthorizationStatus.Authorized
                    ? BuyerMailAction.ClearAuth
                    : BuyerMailAction.None,
                _ => BuyerMailAction.None
            },
            _ => BuyerMailAction.None
        },
        _ => BuyerMailAction.None
    };
}
```

（`CanSupplierSetStatus` 不变，仍委托 `CanSupplierViewBuyer`。）

- [ ] **Step 4: 改 `Services/CardKeyService.cs`**

4a. `CardKeyListItem`（第 9-18 行）替换为：

```csharp
public sealed record CardKeyListItem(
    long Id,
    string CardNo,
    BuyerStage Stage,
    bool AutoApprove,
    long? SaleId,
    string? SaleDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UsedAt,
    DateTimeOffset? SentAt);
```

4b. `GenerateAsync` 内构造买家（第 66-73 行）替换为（本任务暂不接 `autoApprove` 参数，Task 2 再加）：

```csharp
            _db.Buyers.Add(new Buyer
            {
                CardNo = cardNo,
                Stage = sent ? BuyerStage.Sent : BuyerStage.NotSent,
                SaleId = saleId,
                CardSentAt = sent ? now : null
            });
```

4c. `DeleteAsync` 删除第 95 行 `buyer.CardStatus = CardStatus.DeletedOrDisabled;`（仅保留 `buyer.IsDeleted = true;`）。

4d. `SendAsync` 第 123 行 `&& b.CardSendStatus == CardSendStatus.NotSent)` → `&& b.Stage == BuyerStage.NotSent)`；第 137 行 `buyer.CardSendStatus = CardSendStatus.Sent;` → `buyer.Stage = BuyerStage.Sent;`。

4e. `ListAsync`（第 151-187 行）整体替换为：

```csharp
    public async Task<IReadOnlyList<CardKeyListItem>> ListAsync(
        BuyerStage? stage, long? saleId, string? cardNo, bool sentTab)
    {
        var query = _db.Buyers.Where(b => !b.IsDeleted);
        query = sentTab
            ? query.Where(b => b.Stage != BuyerStage.NotSent)
            : query.Where(b => b.Stage == BuyerStage.NotSent);
        if (stage is not null)
        {
            query = query.Where(b => b.Stage == stage);
        }
        if (saleId is not null)
        {
            query = query.Where(b => b.SaleId == saleId);
        }
        if (!string.IsNullOrWhiteSpace(cardNo))
        {
            query = query.Where(b => b.CardNo.Contains(cardNo));
        }

        var buyers = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
        var saleNames = await _db.Users
            .Where(u => u.Role == UserRole.Sales)
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return buyers.Select(b => new CardKeyListItem(
            b.Id,
            b.CardNo,
            b.Stage,
            b.AutoApprove,
            b.SaleId,
            b.SaleId is not null && saleNames.TryGetValue(b.SaleId.Value, out var name) ? name : null,
            b.CreatedAt,
            b.CardUsedAt,
            b.CardSentAt)).ToList();
    }
```

- [ ] **Step 5: 改 `Pages/Buyer/Verify.cshtml.cs`**

第 34 行 `if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)` → `if (buyer is null)`。
第 40-43 行替换为：

```csharp
        if (buyer.Stage is BuyerStage.NotSent or BuyerStage.Sent)
        {
            buyer.Stage = BuyerStage.NotSubmitted;
        }
```

- [ ] **Step 6: 改 `Pages/OAuth/Start.cshtml.cs` 与 `Callback.cshtml.cs` 的死守卫**

`Start.cshtml.cs` 第 23 行 `if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)` → `if (buyer is null)`。
`Callback.cshtml.cs` 第 55 行 `if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)` → `if (buyer is null)`。

- [ ] **Step 7: 改 `Pages/OAuth/Callback.cshtml.cs` 授权成功段（第 95-101 行）**

替换为（本任务用固定 `Pending`，Task 3 再接免审核）：

```csharp
        buyer.Stage = BuyerStage.Submitted;
        buyer.CardUsedAt ??= DateTimeOffset.UtcNow;
        if (isNewOrChangedAccount)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
            buyer.ReviewStatus = ReviewStatus.Pending;
        }
        else if (buyer.EmailStatus == EmailAuthorizationStatus.Abnormal)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
        }
```

- [ ] **Step 8: 改 `Pages/Buyer/Email.cshtml.cs`**

8a. 属性（第 27、29 行）：`public CardStatus CardStatus` → `public BuyerStage Stage`；`public BuyerStatus BuyerStatus` → `public ReviewStatus ReviewStatus`。

8b. `OnPostChangeEmailAsync` 第 71-73 行替换为：

```csharp
        buyer.Stage = BuyerStage.NotSubmitted;
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        buyer.ReviewStatus = ReviewStatus.Pending;
        buyer.SupplierStatus = SupplierProcessingStatus.Unprocessed;
```

8c. `OnPostClearAuthAsync` 第 101-107 行替换为：

```csharp
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        // Keep Approved+Completed as the terminal "cleared" state; otherwise reset to a fresh cycle.
        if (!(buyer.ReviewStatus == ReviewStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Completed))
        {
            buyer.Stage = BuyerStage.NotSubmitted;
            buyer.ReviewStatus = ReviewStatus.Pending;
            buyer.SupplierStatus = SupplierProcessingStatus.Unprocessed;
        }
```

8d. `LoadBuyerAsync` 第 121 行 `if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)` → `if (buyer is null)`。

8e. `Render` 第 131、133 行：`CardStatus = buyer.CardStatus;` → `Stage = buyer.Stage;`；`BuyerStatus = buyer.BuyerStatus;` → `ReviewStatus = buyer.ReviewStatus;`。

- [ ] **Step 9: 改 `Pages/Buyer/Email.cshtml`（第 19-24 行）**

```html
        <dt class="col-sm-3">@L["Buyer.Email.Stage"]</dt>
        <dd class="col-sm-9">@Model.Stage</dd>
        <dt class="col-sm-3">@L["Table.EmailStatus"]</dt>
        <dd class="col-sm-9">@Model.EmailStatus</dd>
        <dt class="col-sm-3">@L["Table.ReviewStatus"]</dt>
        <dd class="col-sm-9">@Model.ReviewStatus</dd>
```

- [ ] **Step 10: 改 `Pages/Admin/Buyers.cshtml.cs`**

10a. 筛选属性（第 28 行）替换为：

```csharp
    [BindProperty(SupportsGet = true)] public BuyerStage? StageFilter { get; set; }
    [BindProperty(SupportsGet = true)] public ReviewStatus? ReviewFilter { get; set; }
```

10b. 审核 handler（第 34-36 行）：

```csharp
    public async Task<IActionResult> OnPostApproveAsync(long id) => await ReviewAsync(id, ReviewStatus.Approved);

    public async Task<IActionResult> OnPostRejectAsync(long id) => await ReviewAsync(id, ReviewStatus.Rejected);
```

10c. `ReviewAsync`（第 63-68 行）签名与判定：

```csharp
    private async Task<IActionResult> ReviewAsync(long id, ReviewStatus decision)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null && buyer.Stage == BuyerStage.Submitted && buyer.ReviewStatus == ReviewStatus.Pending)
        {
            buyer.ReviewStatus = decision;
```

10d. `LoadAsync`（第 92-96 行）：

```csharp
        var query = _db.Buyers.Where(b => !b.IsDeleted && b.Stage != BuyerStage.NotSent);
        if (StageFilter is not null)
        {
            query = query.Where(b => b.Stage == StageFilter);
        }
        if (ReviewFilter is not null)
        {
            query = query.Where(b => b.ReviewStatus == ReviewFilter);
        }
```

- [ ] **Step 11: 改 `Pages/Admin/Buyers.cshtml`**

11a. 筛选下拉（第 16-21 行）替换为两个下拉：

```html
    <div class="col-12 col-sm-auto">
        <select class="form-select" asp-for="StageFilter"
                asp-items="Html.GetEnumSelectList<WebMail.Domain.BuyerStage>()">
            <option value="">@L["Filter.AllStage"]</option>
        </select>
    </div>
    <div class="col-12 col-sm-auto">
        <select class="form-select" asp-for="ReviewFilter"
                asp-items="Html.GetEnumSelectList<WebMail.Domain.ReviewStatus>()">
            <option value="">@L["Filter.AllReviewStatus"]</option>
        </select>
    </div>
```

11b. 表头（第 49 行 `<th>@L["Table.BuyerStatus"]</th>`）替换为两列：

```html
                <th>@L["Table.Stage"]</th>
                <th>@L["Table.ReviewStatus"]</th>
```

11c. 单元格（第 66 行）替换为两格（审核列在未提交时显示「—」）：

```html
                    <td data-label="@L["Table.Stage"]">@buyer.Stage</td>
                    <td data-label="@L["Table.ReviewStatus"]">@(buyer.Stage == BuyerStage.Submitted ? buyer.ReviewStatus.ToString() : "—")</td>
```

11d. 审核按钮条件（第 71 行）：

```html
                        @if (buyer.Stage == BuyerStage.Submitted && buyer.ReviewStatus == ReviewStatus.Pending)
```

- [ ] **Step 12: 改 `Pages/Admin/CardKeys.cshtml.cs`**

12a. 文件顶部 `namespace` 之后加 UI 用枚举：

```csharp
public enum CardKeyTab { NotSent = 1, Sent = 2 }
```

12b. 筛选/Tab 属性（第 28、32 行）替换为：

```csharp
    [BindProperty(SupportsGet = true)] public BuyerStage? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public long? SaleFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? CardNo { get; set; }

    [BindProperty(SupportsGet = true)] public CardKeyTab Tab { get; set; } = CardKeyTab.NotSent;
```

12c. `LoadAsync`（第 68 行）：

```csharp
        Cards = await _cardKeys.ListAsync(StatusFilter, SaleFilter, CardNo, Tab == CardKeyTab.Sent);
```

- [ ] **Step 13: 改 `Pages/Admin/CardKeys.cshtml`**

- 第 6 行 `var notSent = Model.Tab == CardSendStatus.NotSent;` → `var notSent = Model.Tab == CardKeyTab.NotSent;`
- 第 43、50 行 `@CardSendStatus.NotSent` / `@CardSendStatus.Sent` → `@CardKeyTab.NotSent` / `@CardKeyTab.Sent`
- 第 60-61 行筛选下拉枚举类型 `WebMail.Domain.CardStatus` → `WebMail.Domain.BuyerStage`
- 第 144 行 `<td data-label="@L["Table.Status"]">@card.Status</td>` → `<td data-label="@L["Table.Status"]">@card.Stage</td>`
- 顶部 `@using WebMail.Domain` 已有；`CardKeyTab` 在 `WebMail.Pages.Admin` 命名空间，视图 `@model WebMail.Pages.Admin.CardKeysModel` 同命名空间可直接用 `CardKeyTab`。

- [ ] **Step 14: 改 `Pages/Sales/Buyers.cshtml`**

- 表头第 27 行 `<th>@L["Table.BuyerStatus"]</th>` → 两列 `<th>@L["Table.Stage"]</th>` 和 `<th>@L["Table.ReviewStatus"]</th>`
- 单元格第 44 行替换为：

```html
                    <td data-label="@L["Table.Stage"]">@buyer.Stage</td>
                    <td data-label="@L["Table.ReviewStatus"]">@(buyer.Stage == BuyerStage.Submitted ? buyer.ReviewStatus.ToString() : "—")</td>
```

- [ ] **Step 15: 改 `Pages/Supplier/_BuyerTable.cshtml`**

- 表头第 16 行 `<th>@L["Table.BuyerStatus"]</th>` → `<th>@L["Table.Stage"]</th>` 和 `<th>@L["Table.ReviewStatus"]</th>`
- 单元格第 27 行替换为：

```html
                    <td data-label="@L["Table.Stage"]">@buyer.Stage</td>
                    <td data-label="@L["Table.ReviewStatus"]">@(buyer.Stage == BuyerStage.Submitted ? buyer.ReviewStatus.ToString() : "—")</td>
```

- [ ] **Step 16: 改供应商页模型**

`Pages/Supplier/Mail.cshtml.cs` 第 36 行、`Pages/Supplier/Buyers.cshtml.cs` 第 87 行：
`x.Buyer.BuyerStatus == BuyerStatus.Approved` → `x.Buyer.ReviewStatus == ReviewStatus.Approved`。

- [ ] **Step 17: 加本地化键到两个 resx**

在 `SharedResource.en.resx` 与 `SharedResource.zh-CN.resx` 各新增（沿用现有 `<data name=...><value>...</value></data>` 结构）：

| key | en | zh-CN |
|---|---|---|
| `Table.Stage` | Stage | 流程 |
| `Table.ReviewStatus` | Review | 审核 |
| `Filter.AllStage` | All stages | 全部流程 |
| `Filter.AllReviewStatus` | All review states | 全部审核状态 |
| `Buyer.Email.Stage` | Stage | 流程 |

（若 `Table.BuyerStatus`、`Filter.AllBuyerStatus`、`Buyer.Email.CardStatus`、`Table.SupplierProcessingStatus` 等旧键已无引用，可保留不删，避免牵动其它处。）

- [ ] **Step 18: 迁移既有测试（按映射表）**

对 `tests/WebMail.Tests/` 下所有引用 `CardStatus` / `CardSendStatus` / `BuyerStatus` 的文件，按本计划顶部「映射表」逐一替换构造与断言。重点文件与改法：

- `OAuthCallbackModelTests.cs`：
  - 所有 `CardStatus.Entered` → `Stage = BuyerStage.NotSubmitted`；`CardStatus.Authorized` → `Stage = BuyerStage.Submitted`。
  - `NewBinding...` 用例断言 `Assert.Equal(BuyerStatus.PendingReview, buyer.BuyerStatus);` → `Assert.Equal(ReviewStatus.Pending, buyer.ReviewStatus);` 并加 `Assert.Equal(BuyerStage.Submitted, buyer.Stage);`。
  - `AbnormalSameMailbox...`：构造 `BuyerStatus = BuyerStatus.Approved` → `Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved`；断言同步改 `ReviewStatus`/`Stage`。
  - `ChangedAccountBlockedWhenLocked`：构造 `Approved + Unprocessed` → `Stage=Submitted, ReviewStatus=Approved, SupplierStatus=Unprocessed`；断言改 `ReviewStatus.Approved`、`Stage` 不变。
- `AdminBuyersModelTests.cs`：构造 `BuyerStatus.PendingReview` → `Stage=Submitted, ReviewStatus=Pending`；`CardSendStatus.Sent` → `Stage=Sent`（未授权场景）或 `Submitted`（已授权场景，结合该用例语义）；审核断言改 `ReviewStatus`。加载用例（含 `CardSendStatus=Sent`）按语义映射到 `Stage`。
- `CardKeyServiceTests.cs`：`CardStatus.Unused` → `Stage=BuyerStage.NotSent`（无销售）或 `Sent`（有 `SaleId`）；`CardStatus.Authorized` → `Stage=Submitted`；`ListAsync` 调用改新签名 `ListAsync(stage, saleId, cardNo, sentTab)`。
- `CardKeysModelTests.cs`：构造与 `Tab`/`StatusFilter` 类型按新 `CardKeyTab`/`BuyerStage` 调整。
- `SupplierBuyersModelTests.cs`、`BuyerRuleServiceTests.cs`：`BuyerStatus.*` → `Stage`+`ReviewStatus`（套映射表）；`BuyerRuleServiceTests` 的 `[InlineData(BuyerStatus.PendingReview)]` 等改为 `ReviewStatus` 并补 `Stage` 维度（见 Task 1 验证用例）。
- `StartModelTests.cs`：`CardStatus.Entered` → `Stage=BuyerStage.NotSubmitted`。

- [ ] **Step 19: 构建并跑全部测试**

Run: `dotnet test`
Expected: 编译通过，全部测试 PASS（行为等价迁移）。

- [ ] **Step 20: 提交**

```bash
git add src/WebMail tests/WebMail.Tests
git commit -m "refactor: replace card/buyer status fields with BuyerStage + ReviewStatus"
```

---

## Task 2: 按卡免审核 —— 生成端写入 `AutoApprove`

**Files:**
- Modify: `src/WebMail/Services/CardKeyService.cs`
- Modify: `src/WebMail/Pages/Admin/CardKeys.cshtml.cs`, `src/WebMail/Pages/Admin/CardKeys.cshtml`
- Modify: `src/WebMail/Resources/SharedResource.en.resx`, `.zh-CN.resx`
- Test: `tests/WebMail.Tests/CardKeyServiceTests.cs`

**Interfaces:**
- Consumes: `Buyer.AutoApprove`（Task 1 已加）。
- Produces: `CardKeyService.GenerateAsync(int count, long? saleId, bool autoApprove, long? actingAdminId)`。

- [ ] **Step 1: 写失败测试**

在 `CardKeyServiceTests.cs` 新增：

```csharp
[Fact]
public async Task GenerateWithAutoApproveMarksBuyers()
{
    await using var db = CreateDb();
    var service = new CardKeyService(db, new CardGenerationService());

    var result = await service.GenerateAsync(2, null, autoApprove: true, actingAdminId: null);

    Assert.True(result.Success);
    var cards = await db.Buyers.ToListAsync();
    Assert.Equal(2, cards.Count);
    Assert.All(cards, c => Assert.True(c.AutoApprove));
}

[Fact]
public async Task GenerateWithoutAutoApproveLeavesFlagFalse()
{
    await using var db = CreateDb();
    var service = new CardKeyService(db, new CardGenerationService());

    await service.GenerateAsync(1, null, autoApprove: false, actingAdminId: null);

    var card = await db.Buyers.SingleAsync();
    Assert.False(card.AutoApprove);
}
```

> 注：`CreateDb()` / `CardGenerationService` 用法对齐该测试文件既有写法；若既有 `GenerateAsync` 调用未带 `autoApprove`，同时更新它们为新签名（加 `autoApprove: false`）。

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter FullyQualifiedName~CardKeyServiceTests.GenerateWithAutoApprove`
Expected: FAIL（`GenerateAsync` 无 `autoApprove` 重载 / 编译错误）。

- [ ] **Step 3: 改 `CardKeyService.GenerateAsync`**

签名（第 35 行）改为：

```csharp
    public async Task<CardKeyResult> GenerateAsync(int count, long? saleId, bool autoApprove, long? actingAdminId)
```

构造买家加 `AutoApprove = autoApprove,`：

```csharp
            _db.Buyers.Add(new Buyer
            {
                CardNo = cardNo,
                Stage = sent ? BuyerStage.Sent : BuyerStage.NotSent,
                AutoApprove = autoApprove,
                SaleId = saleId,
                CardSentAt = sent ? now : null
            });
```

审计 `Details` 追加免审核信息：`Details = $"count={count};sale={saleId};autoApprove={autoApprove}"`。

- [ ] **Step 4: 改 `CardKeys.cshtml.cs` 生成绑定**

加绑定属性：

```csharp
    [BindProperty] public bool GenerateAutoApprove { get; set; }
```

`OnPostGenerateAsync`（第 44 行）改为：

```csharp
        Message = _loc[(await _cardKeys.GenerateAsync(GenerateCount, GenerateSaleId, GenerateAutoApprove, AdminId())).Message];
```

- [ ] **Step 5: 改 `CardKeys.cshtml` 生成表单加勾选**

在生成表单（第 33 行的提交按钮 `<div>` 之前）插入：

```html
            <div class="col-12 col-sm-auto d-flex align-items-center">
                <div class="form-check">
                    <input class="form-check-input" type="checkbox" asp-for="GenerateAutoApprove" id="GenerateAutoApprove" />
                    <label class="form-check-label" for="GenerateAutoApprove">@L["CardKey.AutoApprove"]</label>
                </div>
            </div>
```

并在卡密列表「状态」列后增加免审标记（第 144 行单元格后追加一格，可选展示）：

```html
                    <td data-label="@L["Table.AutoApprove"]">@(card.AutoApprove ? L["CardKey.AutoApprove"].Value : "—")</td>
```

> 同步在表头（第 105 行 `<th>@L["Table.Status"]</th>` 之后）加 `<th>@L["Table.AutoApprove"]</th>`。

- [ ] **Step 6: 加本地化键**

| key | en | zh-CN |
|---|---|---|
| `CardKey.AutoApprove` | Auto-approve | 免审核 |
| `Table.AutoApprove` | Auto-approve | 免审核 |

- [ ] **Step 7: 跑测试**

Run: `dotnet test --filter FullyQualifiedName~CardKeyServiceTests`
Expected: PASS。

- [ ] **Step 8: 提交**

```bash
git add src/WebMail tests/WebMail.Tests
git commit -m "feat(cardkey): per-card auto-approve flag at generation"
```

---

## Task 3: 授权回调遵循 `AutoApprove` → 直接通过

**Files:**
- Modify: `src/WebMail/Pages/OAuth/Callback.cshtml.cs`
- Test: `tests/WebMail.Tests/OAuthCallbackModelTests.cs`

**Interfaces:**
- Consumes: `Buyer.AutoApprove`、`ReviewStatus`、`BuyerStage`。

- [ ] **Step 1: 写失败测试**

在 `OAuthCallbackModelTests.cs` 新增：

```csharp
[Fact]
public async Task AutoApproveCardGoesStraightToApprovedOnNewBinding()
{
    await using var db = CreateDb();
    db.Buyers.Add(new Buyer { CardNo = "ca", Stage = BuyerStage.NotSubmitted, AutoApprove = true });
    await db.SaveChangesAsync();

    var store = new FakeOAuthStateStore();
    var state = store.Issue("Gmail", "ca");
    var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
    await model.OnGetAsync("code", state, null, CancellationToken.None);

    var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "ca");
    Assert.Equal(BuyerStage.Submitted, buyer.Stage);
    Assert.Equal(EmailAuthorizationStatus.Authorized, buyer.EmailStatus);
    Assert.Equal(ReviewStatus.Approved, buyer.ReviewStatus);
}

[Fact]
public async Task NonAutoApproveCardStaysPendingOnNewBinding()
{
    await using var db = CreateDb();
    db.Buyers.Add(new Buyer { CardNo = "cn", Stage = BuyerStage.NotSubmitted, AutoApprove = false });
    await db.SaveChangesAsync();

    var store = new FakeOAuthStateStore();
    var state = store.Issue("Gmail", "cn");
    var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
    await model.OnGetAsync("code", state, null, CancellationToken.None);

    var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "cn");
    Assert.Equal(ReviewStatus.Pending, buyer.ReviewStatus);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --filter FullyQualifiedName~OAuthCallbackModelTests.AutoApproveCardGoesStraightToApproved`
Expected: FAIL（`ReviewStatus` 实为 `Pending`）。

- [ ] **Step 3: 改 `Callback.cshtml.cs` 新绑定分支**

把 Task 1 写下的：

```csharp
        if (isNewOrChangedAccount)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
            buyer.ReviewStatus = ReviewStatus.Pending;
        }
```

改为：

```csharp
        if (isNewOrChangedAccount)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
            buyer.ReviewStatus = buyer.AutoApprove ? ReviewStatus.Approved : ReviewStatus.Pending;
        }
```

- [ ] **Step 4: 跑测试**

Run: `dotnet test --filter FullyQualifiedName~OAuthCallbackModelTests`
Expected: PASS（含既有用例与两条新用例）。

- [ ] **Step 5: 全量回归 + 提交**

Run: `dotnet test`
Expected: 全绿。

```bash
git add src/WebMail tests/WebMail.Tests
git commit -m "feat(cardkey): auto-approve cards bypass review on authorization"
```

---

## Self-Review（计划对照 spec）

- **流程线 `BuyerStage` 吸收 `CardSendStatus`+`CardStatus`** → Task 1 Step 1-2、4、5、7 ✅
- **审核 `ReviewStatus` 独立、去掉 NotSubmitted** → Task 1 Step 1-2、3、10 ✅
- **`EmailStatus` 不变、异常恢复不退化** → Task 1 Step 3、7（异常分支仅改 Email）；`MailSyncProcessor` 仅翻 `EmailStatus`，无字段重命名，无需改 ✅
- **按卡免审核（生成勾选 + 回调直通）** → Task 2、Task 3 ✅
- **免审核粘性（换邮箱重授权仍通过）** → 由 `AutoApprove` 持久 + Callback 分支保证；换邮箱重置 `ReviewStatus=Pending` 后重授权再次按 `AutoApprove` 置 `Approved`（Task 1 Step 8b + Task 3 Step 3）✅
- **两列显示 + 筛选** → Task 1 Step 10-15 ✅
- **权限规则字段替换** → Task 1 Step 3、16 ✅
- **卡密页 是否使用=Stage==Submitted、免审列** → Task 1 Step 4e/13、Task 2 Step 5 ✅
- **范围外（迁移/事后驳回/按销售或全局免审）** → 未纳入 ✅
- **占位符扫描**：无 TBD/TODO；所有代码步骤含具体代码 ✅
- **类型一致性**：`ListAsync(BuyerStage?, long?, string?, bool)`、`GenerateAsync(int,long?,bool,long?)`、`CardKeyTab`、`CardKeyListItem(... BuyerStage Stage, bool AutoApprove ...)`、`ReviewAsync(long, ReviewStatus)` 全程一致 ✅
