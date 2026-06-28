# 卡密发送给销售（Card-Key Send-to-Sales）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有「卡密管理」页面上增加“发送给销售”的分发流程——独立发送状态、单个/批量发送弹出层、可复制链接、按发送状态分两个页签，且已发送不可重复发送。

**Architecture:** 复用现有 `Buyer` 体系与 `CardKeyService`。新增 `CardSendStatus` 枚举（与买家生命周期 `CardStatus` 正交）+ `Buyer` 两个字段；服务层加 `SendAsync` 与 `ListAsync` 发送状态过滤；页面加 `Tab` 页签 + `OnPostSendAsync`；视图加 Bootstrap modal 与每行操作；纯前端 JS 胶水，无第三方库。

**Tech Stack:** ASP.NET Core Razor Pages、EF Core（SQLite，`EnsureCreatedAsync` 无迁移）、`IStringLocalizer<SharedResource>`、Bootstrap 5（已含 `bootstrap.bundle.min.js`）、xUnit + EF InMemory 测试。

## Global Constraints

- 仅管理员可访问：页面已有 `[Authorize(Policy = "AdminOnly")]`，不改动。
- 数据库用 `EnsureCreatedAsync()`、**无 EF 迁移**：新增列不会自动加到已有 SQLite 库。开发环境删除 `webmail.dev.db` 让其重建；生产需手动 `ALTER TABLE`（见 Task 1 注记）。
- 枚举数值显式赋值（沿用 `Domain/Enums.cs` 风格，从 1 起）。
- 服务方法返回 `CardKeyResult(bool Success, string Message, int GeneratedCount=0)`，`Message` 为本地化 key；写 `AuditLog`。
- 测试本地化桩 `TestLocalizer`：无参 `_loc[key]` 回显 `key`；带参 `_loc[key, a]` 回显 `key|a`（断言据此）。
- 卡密对外链接格式：`{scheme}://{host}/?card={CardNo}`，`SaleId` 非空再加 `&saleid={SaleId}`。
- 文件路径一律相对仓库根：源码在 `src/WebMail/`，测试在 `tests/WebMail.Tests/`。
- 测试命令统一用：`dotnet test tests/WebMail.Tests/WebMail.Tests.csproj`（可加 `--filter`）。

---

### Task 1: 领域字段 + 生成时写入发送状态

**Files:**
- Modify: `src/WebMail/Domain/Enums.cs`
- Modify: `src/WebMail/Domain/Entities.cs:14-26`（`Buyer`）
- Modify: `src/WebMail/Services/CardKeyService.cs:60-68`（`GenerateAsync` 写卡循环）
- Test: `tests/WebMail.Tests/CardKeyServiceTests.cs`（扩展现有两个 generate 测试）

**Interfaces:**
- Produces: `enum CardSendStatus { NotSent = 1, Sent = 2 }`；`Buyer.CardSendStatus`（默认 `NotSent`）、`Buyer.CardSentAt`（`DateTimeOffset?`）。
- Consumes: 现有 `CardKeyService.GenerateAsync(int count, long? saleId, long? actingAdminId)`。

- [ ] **Step 1: 扩展现有两个 generate 测试的断言（先失败）**

在 `tests/WebMail.Tests/CardKeyServiceTests.cs` 中，把 `GenerateCreatesUnusedCardsBoundToSale` 的断言段（第 21-28 行附近）替换为：

```csharp
        Assert.True(result.Success);
        Assert.Equal("CardKey.Generated", result.Message);
        Assert.Equal(3, result.GeneratedCount);
        var cards = await db.Buyers.ToListAsync();
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.Equal(CardStatus.Unused, c.CardStatus));
        Assert.All(cards, c => Assert.Equal(sale.Id, c.SaleId));
        Assert.All(cards, c => Assert.Equal(CardSendStatus.Sent, c.CardSendStatus));
        Assert.All(cards, c => Assert.NotNull(c.CardSentAt));
        Assert.Equal(3, cards.Select(c => c.CardNo).Distinct().Count());
```

把 `GenerateWithoutSaleLeavesSaleIdNull` 的断言段（第 39-40 行附近）替换为：

```csharp
        Assert.True(result.Success);
        var card = await db.Buyers.SingleAsync();
        Assert.Null(card.SaleId);
        Assert.Equal(CardSendStatus.NotSent, card.CardSendStatus);
        Assert.Null(card.CardSentAt);
```

- [ ] **Step 2: 运行测试确认编译失败**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeyServiceTests"`
Expected: 编译失败，错误形如 `'CardSendStatus' does not exist` / `'Buyer' does not contain a definition for 'CardSendStatus'`。

- [ ] **Step 3: 加枚举**

在 `src/WebMail/Domain/Enums.cs` 末尾（第 9 行 `MailFolder` 之后）新增一行：

```csharp
public enum CardSendStatus { NotSent = 1, Sent = 2 }
```

- [ ] **Step 4: 加 Buyer 字段**

在 `src/WebMail/Domain/Entities.cs` 的 `Buyer` 类里，紧接 `public CardStatus CardStatus { get; set; } = CardStatus.Unused;`（第 18 行）之后新增两行：

```csharp
    public CardSendStatus CardSendStatus { get; set; } = CardSendStatus.NotSent;
    public DateTimeOffset? CardSentAt { get; set; }
```

- [ ] **Step 5: 生成时按是否选销售写发送状态**

在 `src/WebMail/Services/CardKeyService.cs`，把写卡的 `foreach`（第 60-68 行）替换为：

```csharp
        var now = DateTimeOffset.UtcNow;
        var sent = saleId is not null;
        foreach (var cardNo in generated)
        {
            _db.Buyers.Add(new Buyer
            {
                CardNo = cardNo,
                CardStatus = CardStatus.Unused,
                SaleId = saleId,
                CardSendStatus = sent ? CardSendStatus.Sent : CardSendStatus.NotSent,
                CardSentAt = sent ? now : null
            });
        }
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeyServiceTests"`
Expected: PASS（全部 CardKeyServiceTests 通过）。

- [ ] **Step 7: 提交**

```bash
git add src/WebMail/Domain/Enums.cs src/WebMail/Domain/Entities.cs src/WebMail/Services/CardKeyService.cs tests/WebMail.Tests/CardKeyServiceTests.cs
git commit -m "feat(cardkey): add CardSendStatus and stamp it on generate"
```

> ⚠️ 部署注记（不在本任务执行，仅记录）：`EnsureCreated` 不会在已有表加列。开发环境删 `webmail.dev.db` 重建；生产执行
> `ALTER TABLE Buyers ADD COLUMN CardSendStatus INTEGER NOT NULL DEFAULT 1;`
> `ALTER TABLE Buyers ADD COLUMN CardSentAt TEXT NULL;`
> 历史数据可选校正：`UPDATE Buyers SET CardSendStatus = 2 WHERE SaleId IS NOT NULL;`

---

### Task 2: 服务层 SendAsync + 列表发送状态过滤

**Files:**
- Modify: `src/WebMail/Services/CardKeyService.cs`（`CardKeyListItem` 记录、`ListAsync`、新增 `SendAsync`）
- Test: `tests/WebMail.Tests/CardKeyServiceTests.cs`（新增 send/list 用例）

**Interfaces:**
- Consumes: Task 1 的 `CardSendStatus`、`Buyer.CardSendStatus`、`Buyer.CardSentAt`。
- Produces:
  - `record CardKeyListItem(long Id, string CardNo, CardStatus Status, CardSendStatus SendStatus, long? SaleId, string? SaleDisplayName, DateTimeOffset CreatedAt, DateTimeOffset? UsedAt, DateTimeOffset? SentAt)`
  - `Task<IReadOnlyList<CardKeyListItem>> ListAsync(CardStatus? status, long? saleId, string? cardNo, CardSendStatus? sendStatus = null)`
  - `Task<CardKeyResult> SendAsync(IReadOnlyCollection<long> buyerIds, long saleId, long? actingAdminId)` — 返回 `CardKey.Sent`(成功，`GeneratedCount`=实际发送数) / `CardKey.SaleInvalid` / `CardKey.SendNoneSelected`。

- [ ] **Step 1: 写失败测试（send + list 过滤）**

在 `tests/WebMail.Tests/CardKeyServiceTests.cs` 的 `ListSalesReturnsOnlySaleUsers` 之后、`SeedSale` 之前插入：

```csharp
    [Fact]
    public async Task SendAssignsSaleMarksSentAndStampsTime()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 5, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal("CardKey.Sent", result.Message);
        Assert.Equal(1, result.GeneratedCount);
        var card = await db.Buyers.SingleAsync();
        Assert.Equal(5, card.SaleId);
        Assert.Equal(CardSendStatus.Sent, card.CardSendStatus);
        Assert.NotNull(card.CardSentAt);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "AdminSendCardKeys"));
    }

    [Fact]
    public async Task SendBatchSendsMultiple()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent },
            new Buyer { Id = 2, CardNo = "c2", CardSendStatus = CardSendStatus.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L, 2L }, saleId: 5, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal(2, result.GeneratedCount);
        Assert.All(await db.Buyers.ToListAsync(), c => Assert.Equal(CardSendStatus.Sent, c.CardSendStatus));
    }

    [Fact]
    public async Task SendSkipsAlreadySentCards()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        SeedSale(db, id: 6, name: "Bob");
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.Sent, SaleId = 6 });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 5, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SendNoneSelected", result.Message);
        var card = await db.Buyers.SingleAsync();
        Assert.Equal(6, card.SaleId); // 原销售未被覆盖
    }

    [Fact]
    public async Task SendRejectsNonSaleSaleId()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { Id = 9, UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier });
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 9, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SaleInvalid", result.Message);
        Assert.Equal(CardSendStatus.NotSent, (await db.Buyers.SingleAsync()).CardSendStatus);
    }

    [Fact]
    public async Task SendWithNoEligibleCardsReturnsNoneSelected()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(Array.Empty<long>(), saleId: 5, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SendNoneSelected", result.Message);
    }

    [Fact]
    public async Task ListFiltersBySendStatus()
    {
        await using var db = CreateDb();
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent },
            new Buyer { Id = 2, CardNo = "c2", CardSendStatus = CardSendStatus.Sent, SaleId = null });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var notSent = await service.ListAsync(null, null, null, CardSendStatus.NotSent);
        Assert.Equal(new[] { 1L }, notSent.Select(c => c.Id).ToArray());

        var sent = await service.ListAsync(null, null, null, CardSendStatus.Sent);
        Assert.Equal(new[] { 2L }, sent.Select(c => c.Id).ToArray());
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeyServiceTests"`
Expected: 编译失败（`SendAsync` 不存在 / `ListAsync` 不接受第 4 参数 / `CardKeyListItem` 无 `SendStatus`）。

- [ ] **Step 3: 扩展 `CardKeyListItem`**

在 `src/WebMail/Services/CardKeyService.cs`，把 `CardKeyListItem` 记录（第 9-16 行）替换为：

```csharp
public sealed record CardKeyListItem(
    long Id,
    string CardNo,
    CardStatus Status,
    CardSendStatus SendStatus,
    long? SaleId,
    string? SaleDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UsedAt,
    DateTimeOffset? SentAt);
```

- [ ] **Step 4: `ListAsync` 加发送状态过滤与投影**

把 `ListAsync` 方法（第 100-129 行）替换为：

```csharp
    public async Task<IReadOnlyList<CardKeyListItem>> ListAsync(
        CardStatus? status, long? saleId, string? cardNo, CardSendStatus? sendStatus = null)
    {
        var query = _db.Buyers.Where(b => !b.IsDeleted);
        if (status is not null)
        {
            query = query.Where(b => b.CardStatus == status);
        }
        if (sendStatus is not null)
        {
            query = query.Where(b => b.CardSendStatus == sendStatus);
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
            b.CardStatus,
            b.CardSendStatus,
            b.SaleId,
            b.SaleId is not null && saleNames.TryGetValue(b.SaleId.Value, out var name) ? name : null,
            b.CreatedAt,
            b.CardUsedAt,
            b.CardSentAt)).ToList();
    }
```

- [ ] **Step 5: 新增 `SendAsync`**

在 `src/WebMail/Services/CardKeyService.cs` 的 `DeleteAsync` 之后（第 98 行 `}` 之后）插入：

```csharp
    public async Task<CardKeyResult> SendAsync(
        IReadOnlyCollection<long> buyerIds, long saleId, long? actingAdminId)
    {
        if (buyerIds is null || buyerIds.Count == 0)
        {
            return new(false, "CardKey.SendNoneSelected");
        }

        if (!await _db.Users.AnyAsync(u => u.Id == saleId && u.Role == UserRole.Sales))
        {
            return new(false, "CardKey.SaleInvalid");
        }

        var targets = await _db.Buyers
            .Where(b => buyerIds.Contains(b.Id)
                && !b.IsDeleted
                && b.CardSendStatus == CardSendStatus.NotSent)
            .ToListAsync();
        if (targets.Count == 0)
        {
            return new(false, "CardKey.SendNoneSelected");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var buyer in targets)
        {
            buyer.SaleId = saleId;
            buyer.CardSendStatus = CardSendStatus.Sent;
            buyer.CardSentAt = now;
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminSendCardKeys",
            UserId = actingAdminId,
            Details = $"sale={saleId};ids={string.Join(",", targets.Select(b => b.Id))}"
        });
        await _db.SaveChangesAsync();
        return new(true, "CardKey.Sent", targets.Count);
    }
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeyServiceTests"`
Expected: PASS（含新增 6 个用例）。

- [ ] **Step 7: 提交**

```bash
git add src/WebMail/Services/CardKeyService.cs tests/WebMail.Tests/CardKeyServiceTests.cs
git commit -m "feat(cardkey): add SendAsync and send-status list filter"
```

---

### Task 3: 页面 Tab 页签 + 发送处理器

**Files:**
- Modify: `src/WebMail/Pages/Admin/CardKeys.cshtml.cs`
- Test: `tests/WebMail.Tests/CardKeysModelTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `CardKeyService.SendAsync(...)`、`ListAsync(..., CardSendStatus?)`、`CardSendStatus`。
- Produces: `CardKeysModel.Tab`（`CardSendStatus`，默认 `NotSent`，`SupportsGet`）、`SelectedIds`（`long[]`）、`SendSaleId`（`long`）、`OnPostSendAsync()`。

- [ ] **Step 1: 写失败测试**

在 `tests/WebMail.Tests/CardKeysModelTests.cs` 的 `DeleteHandlerSoftDeletesAndRemovesFromList` 之后插入：

```csharp
    [Fact]
    public async Task SendHandlerMarksSelectedCardsSent()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { Id = 5, UserName = "u5", DisplayName = "Alice", Role = UserRole.Sales });
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent });
        await db.SaveChangesAsync();
        var model = CreateModel(db);
        model.SelectedIds = new[] { 1L };
        model.SendSaleId = 5;

        await model.OnPostSendAsync();

        Assert.StartsWith("CardKey.Sent", model.Message);
        Assert.Equal(CardSendStatus.Sent, (await db.Buyers.SingleAsync()).CardSendStatus);
    }

    [Fact]
    public async Task SendHandlerWithNoSelectionSurfacesMessage()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.SelectedIds = Array.Empty<long>();
        model.SendSaleId = 5;

        await model.OnPostSendAsync();

        Assert.Equal("CardKey.SendNoneSelected", model.Message);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeysModelTests"`
Expected: 编译失败（`SelectedIds` / `SendSaleId` / `OnPostSendAsync` 不存在）。

- [ ] **Step 3: 加绑定属性与 Tab**

在 `src/WebMail/Pages/Admin/CardKeys.cshtml.cs`，把 `StatusFilter`/`SaleFilter`/`CardNo` 三个 `[BindProperty(SupportsGet = true)]`（第 28-30 行）之后补充：

```csharp
    [BindProperty(SupportsGet = true)] public CardSendStatus Tab { get; set; } = CardSendStatus.NotSent;

    [BindProperty] public long[] SelectedIds { get; set; } = Array.Empty<long>();
    [BindProperty] public long SendSaleId { get; set; }
```

（`CardSendStatus` 在 `WebMail.Domain` 命名空间，文件顶部已 `using WebMail.Domain;`）

- [ ] **Step 4: `LoadAsync` 传入 Tab，新增 `OnPostSendAsync`**

把 `LoadAsync` 里取卡那行（第 53 行）替换为：

```csharp
        Cards = await _cardKeys.ListAsync(StatusFilter, SaleFilter, CardNo, Tab);
```

在 `OnPostDeleteAsync` 之后（第 49 行 `}` 之后）插入：

```csharp
    public async Task<IActionResult> OnPostSendAsync()
    {
        var result = await _cardKeys.SendAsync(SelectedIds, SendSaleId, AdminId());
        Message = result.Success
            ? _loc["CardKey.Sent", result.GeneratedCount]
            : _loc[result.Message];
        await LoadAsync();
        return Page();
    }
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeysModelTests"`
Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Pages/Admin/CardKeys.cshtml.cs tests/WebMail.Tests/CardKeysModelTests.cs
git commit -m "feat(cardkey): add tab filter and send handler to page model"
```

---

### Task 4: 视图（页签 + 弹出层 + 每行操作）与本地化

**Files:**
- Modify: `src/WebMail/Pages/Admin/CardKeys.cshtml`（整文件替换）
- Modify: `src/WebMail/Resources/SharedResource.zh-CN.resx`
- Modify: `src/WebMail/Resources/SharedResource.en.resx`

**Interfaces:**
- Consumes: Task 3 的 `Tab` / `SelectedIds` / `SendSaleId` / `OnPostSendAsync`；`CardKeyListItem.SendStatus` / `SentAt`；`CardSendStatus`。
- Produces: 无（终端 UI）。视图无单测，验证 = 构建 + 手动。

- [ ] **Step 1: 加本地化条目（zh-CN）**

在 `src/WebMail/Resources/SharedResource.zh-CN.resx` 中，找到 `CardKey.DeleteFailed` 那行（`<data name="CardKey.DeleteFailed" ...>`），在它**之后**插入：

```xml
  <data name="CardKey.Tab.NotSent" xml:space="preserve"><value>未发送</value></data>
  <data name="CardKey.Tab.Sent" xml:space="preserve"><value>已发送</value></data>
  <data name="CardKey.Send" xml:space="preserve"><value>发送</value></data>
  <data name="CardKey.SendBatch" xml:space="preserve"><value>批量发送</value></data>
  <data name="CardKey.SendModalTitle" xml:space="preserve"><value>发送给销售</value></data>
  <data name="CardKey.ConfirmSend" xml:space="preserve"><value>确认发送</value></data>
  <data name="CardKey.CopyLink" xml:space="preserve"><value>复制链接</value></data>
  <data name="CardKey.SentAt" xml:space="preserve"><value>发送时间</value></data>
  <data name="CardKey.Sent" xml:space="preserve"><value>已发送 {0} 张。</value></data>
  <data name="CardKey.SendNoneSelected" xml:space="preserve"><value>请先勾选要发送的卡密。</value></data>
  <data name="CardKey.SendNoSale" xml:space="preserve"><value>请选择销售。</value></data>
```

再找到 `Common.Filter` 那行（`<data name="Common.Filter" ...>`），在它**之后**插入：

```xml
  <data name="Common.Cancel" xml:space="preserve"><value>取消</value></data>
```

- [ ] **Step 2: 加本地化条目（en）**

在 `src/WebMail/Resources/SharedResource.en.resx` 中，找到 `CardKey.DeleteFailed` 那行，在它**之后**插入：

```xml
  <data name="CardKey.Tab.NotSent" xml:space="preserve"><value>Not sent</value></data>
  <data name="CardKey.Tab.Sent" xml:space="preserve"><value>Sent</value></data>
  <data name="CardKey.Send" xml:space="preserve"><value>Send</value></data>
  <data name="CardKey.SendBatch" xml:space="preserve"><value>Send selected</value></data>
  <data name="CardKey.SendModalTitle" xml:space="preserve"><value>Send to sales</value></data>
  <data name="CardKey.ConfirmSend" xml:space="preserve"><value>Confirm</value></data>
  <data name="CardKey.CopyLink" xml:space="preserve"><value>Copy link</value></data>
  <data name="CardKey.SentAt" xml:space="preserve"><value>Sent at</value></data>
  <data name="CardKey.Sent" xml:space="preserve"><value>Sent {0} card(s).</value></data>
  <data name="CardKey.SendNoneSelected" xml:space="preserve"><value>Select at least one unsent card.</value></data>
  <data name="CardKey.SendNoSale" xml:space="preserve"><value>Select a sales person.</value></data>
```

再找到 `Common.Filter` 那行（若 en 文件含 `Common.Filter`；否则插在 `Common.Reset` 之后），在它之后插入：

```xml
  <data name="Common.Cancel" xml:space="preserve"><value>Cancel</value></data>
```

- [ ] **Step 3: 整体替换 `CardKeys.cshtml`**

把 `src/WebMail/Pages/Admin/CardKeys.cshtml` 全文替换为：

```cshtml
@page
@using WebMail.Domain
@model WebMail.Pages.Admin.CardKeysModel
@{
    ViewData["Title"] = L["Admin.CardKeys.Title"].Value;
    var notSent = Model.Tab == CardSendStatus.NotSent;
}

<h1 class="display-6">@L["Admin.CardKeys.Title"]</h1>

@if (!string.IsNullOrEmpty(Model.Message))
{
    <div class="alert alert-info" role="alert">@Model.Message</div>
}

<div class="card mb-4">
    <div class="card-body">
        <h2 class="h5">@L["Admin.CardKeys.GenerateHeading"]</h2>
        <form method="post" asp-page-handler="Generate" class="row g-2">
            <div class="col-auto">
                <input class="form-control" type="number" min="1" max="100" asp-for="GenerateCount" placeholder="@L["CardKey.Count"]" />
            </div>
            <div class="col-auto">
                <select class="form-select" asp-for="GenerateSaleId">
                    <option value="">@L["CardKey.SaleUnassigned"]</option>
                    @foreach (var sale in Model.Sales)
                    {
                        <option value="@sale.Id">@sale.DisplayName</option>
                    }
                </select>
            </div>
            <div class="col-auto">
                <button type="submit" class="btn btn-primary">@L["CardKey.Generate"]</button>
            </div>
        </form>
    </div>
</div>

<ul class="nav nav-tabs mb-3">
    <li class="nav-item">
        <a class="nav-link @(notSent ? "active" : "")"
           asp-page="/Admin/CardKeys" asp-route-Tab="@CardSendStatus.NotSent"
           asp-route-StatusFilter="@Model.StatusFilter" asp-route-SaleFilter="@Model.SaleFilter" asp-route-CardNo="@Model.CardNo">
            @L["CardKey.Tab.NotSent"]
        </a>
    </li>
    <li class="nav-item">
        <a class="nav-link @(notSent ? "" : "active")"
           asp-page="/Admin/CardKeys" asp-route-Tab="@CardSendStatus.Sent"
           asp-route-StatusFilter="@Model.StatusFilter" asp-route-SaleFilter="@Model.SaleFilter" asp-route-CardNo="@Model.CardNo">
            @L["CardKey.Tab.Sent"]
        </a>
    </li>
</ul>

<form method="get" class="row g-2 mb-3">
    <input type="hidden" asp-for="Tab" />
    <div class="col-auto">
        <select class="form-select" asp-for="StatusFilter"
                asp-items="Html.GetEnumSelectList<WebMail.Domain.CardStatus>()">
            <option value="">@L["CardKey.AllStatus"]</option>
        </select>
    </div>
    <div class="col-auto">
        <select class="form-select" asp-for="SaleFilter">
            <option value="">@L["CardKey.AllSales"]</option>
            @foreach (var sale in Model.Sales)
            {
                <option value="@sale.Id">@sale.DisplayName</option>
            }
        </select>
    </div>
    <div class="col-auto">
        <input class="form-control" asp-for="CardNo" placeholder="@L["Filter.CardKeyword"]" />
    </div>
    <div class="col-auto">
        <button type="submit" class="btn btn-outline-primary">@L["Common.Filter"]</button>
        <a class="btn btn-outline-secondary" asp-page="/Admin/CardKeys" asp-route-Tab="@Model.Tab">@L["Common.Reset"]</a>
    </div>
</form>

@if (notSent)
{
    <div class="mb-3">
        <button type="button" class="btn btn-primary" id="btn-batch-send">@L["CardKey.SendBatch"]</button>
    </div>
}

@if (Model.Cards.Count == 0)
{
    <p>@L["Admin.CardKeys.Empty"]</p>
}
else
{
    <div class="table-responsive">
    <table class="table table-striped">
        <thead>
            <tr>
                @if (notSent)
                {
                    <th><input type="checkbox" id="check-all" /></th>
                }
                <th>@L["Table.CardNo"]</th>
                <th>@L["Table.Status"]</th>
                <th>@L["CardKey.Sale"]</th>
                <th>@L["Table.CreatedAt"]</th>
                @if (notSent)
                {
                    <th>@L["CardKey.UsedAt"]</th>
                }
                else
                {
                    <th>@L["CardKey.SentAt"]</th>
                }
                <th>@L["CardKey.Link"]</th>
                <th>@L["Table.Operations"]</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var card in Model.Cards)
            {
                var link = $"{Request.Scheme}://{Request.Host}/?card={card.CardNo}";
                if (card.SaleId is not null)
                {
                    link += $"&saleid={card.SaleId}";
                }
                <tr>
                    @if (notSent)
                    {
                        <td><input type="checkbox" class="row-check" data-id="@card.Id" data-cardno="@card.CardNo" /></td>
                    }
                    <td>@card.CardNo</td>
                    <td>@card.Status</td>
                    <td>@(card.SaleDisplayName ?? "-")</td>
                    <td>@card.CreatedAt</td>
                    @if (notSent)
                    {
                        <td>@(card.UsedAt is null ? L["CardKey.NotUsed"].Value : card.UsedAt.ToString())</td>
                    }
                    else
                    {
                        <td>@(card.SentAt is null ? "-" : card.SentAt.ToString())</td>
                    }
                    <td>
                        <div class="input-group input-group-sm">
                            <input class="form-control" type="text" value="@link" readonly />
                            <button type="button" class="btn btn-outline-secondary copy-link" data-link="@link">@L["CardKey.Copy"]</button>
                        </div>
                    </td>
                    <td>
                        <div class="d-flex gap-1">
                            @if (notSent)
                            {
                                <button type="button" class="btn btn-sm btn-outline-primary btn-row-send"
                                        data-id="@card.Id" data-cardno="@card.CardNo">@L["CardKey.Send"]</button>
                            }
                            <form method="post" asp-page-handler="Delete">
                                <input type="hidden" name="id" value="@card.Id" />
                                <button type="submit" class="btn btn-sm btn-outline-danger">@L["Common.Delete"]</button>
                            </form>
                        </div>
                    </td>
                </tr>
            }
        </tbody>
    </table>
    </div>
}

@if (notSent)
{
    <div class="modal fade" id="sendModal" tabindex="-1" aria-hidden="true">
        <div class="modal-dialog">
            <div class="modal-content">
                <form method="post" asp-page-handler="Send">
                    <div class="modal-header">
                        <h5 class="modal-title">@L["CardKey.SendModalTitle"]</h5>
                        <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
                    </div>
                    <div class="modal-body">
                        <div class="mb-3">
                            <label class="form-label">@L["CardKey.Sale"]</label>
                            <select class="form-select" id="SendSaleSelect" name="SendSaleId">
                                <option value="">@L["CardKey.SaleUnassigned"]</option>
                                @foreach (var sale in Model.Sales)
                                {
                                    <option value="@sale.Id">@sale.DisplayName</option>
                                }
                            </select>
                        </div>
                        <div id="send-card-list" class="small text-muted mb-2"></div>
                        <div id="send-ids"></div>
                        <button type="button" class="btn btn-outline-secondary btn-sm" id="btn-copy-links">@L["CardKey.CopyLink"]</button>
                    </div>
                    <div class="modal-footer">
                        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">@L["Common.Cancel"]</button>
                        <button type="submit" class="btn btn-primary">@L["CardKey.ConfirmSend"]</button>
                    </div>
                </form>
            </div>
        </div>
    </div>
}

@section Scripts {
    <script>
        document.querySelectorAll('.copy-link').forEach(function (btn) {
            btn.addEventListener('click', function () {
                navigator.clipboard.writeText(btn.getAttribute('data-link'));
            });
        });

        (function () {
            var modalEl = document.getElementById('sendModal');
            if (!modalEl) { return; }
            var bsModal = new bootstrap.Modal(modalEl);
            var modalCards = [];
            var noneSelectedMsg = '@L["CardKey.SendNoneSelected"].Value';

            function openSendModal(cards) {
                modalCards = cards;
                document.getElementById('send-card-list').innerHTML =
                    cards.map(function (c) { return '<div>' + c.cardNo + '</div>'; }).join('');
                document.getElementById('send-ids').innerHTML =
                    cards.map(function (c) { return '<input type="hidden" name="SelectedIds" value="' + c.id + '" />'; }).join('');
                bsModal.show();
            }

            var batchBtn = document.getElementById('btn-batch-send');
            if (batchBtn) {
                batchBtn.addEventListener('click', function () {
                    var checked = Array.prototype.slice.call(document.querySelectorAll('.row-check:checked'));
                    if (checked.length === 0) { alert(noneSelectedMsg); return; }
                    openSendModal(checked.map(function (cb) {
                        return { id: cb.getAttribute('data-id'), cardNo: cb.getAttribute('data-cardno') };
                    }));
                });
            }

            document.querySelectorAll('.btn-row-send').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    openSendModal([{ id: btn.getAttribute('data-id'), cardNo: btn.getAttribute('data-cardno') }]);
                });
            });

            var checkAll = document.getElementById('check-all');
            if (checkAll) {
                checkAll.addEventListener('change', function () {
                    document.querySelectorAll('.row-check').forEach(function (cb) { cb.checked = checkAll.checked; });
                });
            }

            var copyBtn = document.getElementById('btn-copy-links');
            if (copyBtn) {
                copyBtn.addEventListener('click', function () {
                    var saleId = document.getElementById('SendSaleSelect').value;
                    var origin = window.location.origin;
                    var text = modalCards.map(function (c) {
                        var link = origin + '/?card=' + encodeURIComponent(c.cardNo);
                        if (saleId) { link += '&saleid=' + saleId; }
                        return link;
                    }).join('\n');
                    navigator.clipboard.writeText(text);
                });
            }
        })();
    </script>
}
```

- [ ] **Step 4: 构建并跑全量测试**

Run: `dotnet build src/WebMail/WebMail.csproj` 然后 `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj`
Expected: 构建无错误；测试全绿。

- [ ] **Step 5: 手动验证（开发环境）**

1. 若本地已有 `webmail.dev.db`，先删除它（让 `EnsureCreated` 重建带新列的表）。
2. `dotnet run --project src/WebMail`，以管理员登录，进入「卡密管理」。
3. 不选销售生成 2 张 → 它们出现在「未发送」页签。
4. 勾选其中 1-2 张 → 点「批量发送」→ 弹出层选销售 → 点「复制链接」（剪贴板得到带 `&saleid=` 的链接）→「确认发送」。
5. 切到「已发送」页签：应看到刚发送的卡，含销售与发送时间；该页签无发送入口。
6. 回「未发送」页签：已发送的卡不再出现（无法重复发送）。
7. 在「未发送」页签用每行「发送」按钮做单张发送，确认同样生效。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Pages/Admin/CardKeys.cshtml src/WebMail/Resources/SharedResource.zh-CN.resx src/WebMail/Resources/SharedResource.en.resx
git commit -m "feat(cardkey): tabs, send modal, and per-row send/copy in UI"
```

---

## Self-Review（作者自查结论）

- **Spec 覆盖**：枚举+字段(Task1)、`GenerateAsync`默认状态(Task1)、`SendAsync`单/批+不可重复(Task2)、`ListAsync`过滤(Task2)、`Tab`+发送处理器(Task3)、两页签/弹出层/复制链接/每行发送/本地化(Task4)、测试(Task1-3)。spec 第 1-9 节均有对应任务。
- **占位符**：无 TBD/TODO；每个代码步骤含完整代码。
- **类型一致性**：`SendAsync(IReadOnlyCollection<long>, long, long?)`、`ListAsync(..., CardSendStatus? = null)`、`CardKeyListItem` 9 字段顺序、`Tab/SelectedIds/SendSaleId` 命名在 Task2/3/4 间一致；`CardKey.Sent` 成功消息带 `{0}`，页面用 `_loc["CardKey.Sent", count]`，测试用 `StartsWith("CardKey.Sent")` 匹配桩输出 `CardKey.Sent|n`。
- **边界**：`ListAsync` 第 4 参数可选，现有 3 参调用与测试不受影响；`CardKey.SendNoSale` 仅前端用，后端缺销售时 `SendSaleId=0` 经角色校验落到 `CardKey.SaleInvalid`（可接受兜底）。
