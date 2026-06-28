# 卡密管理（Card-Key Management）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给管理员加一个卡密管理页：批量生成（上限 100）、软删除、查看是否使用/使用时间/所属销售；并补齐买家流程里的 SaleId 回写与使用时间记录。

**Architecture:** 复用现有 `Buyer` 实体（`CardNo`/`SaleId`/`CardStatus`）。新增 `CardKeyService`（仿 `UserAdminService`，返回本地化消息 key + 写审计日志）承载生成/删除/列表逻辑；新增 `Pages/Admin/CardKeys` 页面调用该服务。买家授权流程里补两处：`Verify` 回写 `SaleId`，`Callback` 记录 `CardUsedAt`。

**Tech Stack:** ASP.NET Core Razor Pages (.NET)、EF Core + SQLite（运行时）/ EF InMemory（测试）、xUnit。

## Global Constraints

- 卡密 = `Buyer` 记录；"已使用" = `CardStatus == CardStatus.Authorized`；"使用时间" = 新字段 `Buyer.CardUsedAt`；"所属销售" = `Buyer.SaleId`（关联 `AppUser.Role == UserRole.Sales`）。
- 买家辛权链接查询参数名是 `card` 与 `saleid`（非 `salesid`），与现有 `IndexModel.OnGet(card, saleid)` 一致。
- 单次生成数量上限 `MaxGenerateCount = 100`。
- 删除 = 软删除：`IsDeleted = true` 且 `CardStatus = CardStatus.DeletedOrDisabled`，不物理删除。
- 状态列直接渲染枚举原值（如 `@card.Status`），与 `Admin/Buyers.cshtml` 既有约定一致，不为枚举值新增本地化 key。
- 服务方法返回 `CardKeyResult(bool Success, string Message, int GeneratedCount=0)`，`Message` 是本地化资源 key（由页面用 `IStringLocalizer` 翻译）。
- 每个变更操作写一条 `AuditLog`。
- 管理页授权策略：`[Authorize(Policy = "AdminOnly")]`。
- DB 通过 `EnsureCreated` 建表，无迁移：新增 `CardUsedAt` 列在已存在的 `webmail.dev.db` 上不会自动生成，开发环境需删库重建（运行 `Program.cs` 会重建）。
- 测试用 `new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options` 构造隔离 DB（沿用现有测试约定）。

---

### Task 1: `CardKeyService` + `Buyer.CardUsedAt` 字段 + DI 注册

**Files:**
- Modify: `src/WebMail/Domain/Entities.cs`（`Buyer` 加 `CardUsedAt`）
- Create: `src/WebMail/Services/CardKeyService.cs`
- Modify: `src/WebMail/Program.cs:28`（注册服务）
- Test: `tests/WebMail.Tests/CardKeyServiceTests.cs`

**Interfaces:**
- Consumes: `WebMailDbContext`、`CardGenerationService.GenerateCardNo(int length=32)`、`Buyer`、`AppUser`、`UserRole`、`CardStatus`、`AuditLog`。
- Produces:
  - `record CardKeyResult(bool Success, string Message, int GeneratedCount = 0)`
  - `record CardKeyListItem(long Id, string CardNo, CardStatus Status, long? SaleId, string? SaleDisplayName, DateTimeOffset CreatedAt, DateTimeOffset? UsedAt)`
  - `record SaleOption(long Id, string DisplayName)`
  - `CardKeyService` 方法：
    - `Task<CardKeyResult> GenerateAsync(int count, long? saleId, long? actingAdminId)`
    - `Task<CardKeyResult> DeleteAsync(long id, long? actingAdminId)`
    - `Task<IReadOnlyList<CardKeyListItem>> ListAsync(CardStatus? status, long? saleId, string? cardNo)`
    - `Task<IReadOnlyList<SaleOption>> ListSalesAsync()`
    - `const int MaxGenerateCount = 100`
  - 消息 key：`CardKey.Generated`、`CardKey.CountInvalid`、`CardKey.SaleInvalid`、`CardKey.Deleted`、`CardKey.DeleteFailed`

- [ ] **Step 1: 给 `Buyer` 加 `CardUsedAt` 字段**

在 `src/WebMail/Domain/Entities.cs` 的 `Buyer` 类里，`CreatedAt` 之后加一行：

```csharp
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CardUsedAt { get; set; }
```

（`WebMailDbContext` 的 `DateTimeOffset?` 值转换器循环已覆盖可空类型，无需额外配置。）

- [ ] **Step 2: 写失败测试**

创建 `tests/WebMail.Tests/CardKeyServiceTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class CardKeyServiceTests
{
    [Fact]
    public async Task GenerateCreatesUnusedCardsBoundToSale()
    {
        await using var db = CreateDb();
        var sale = SeedSale(db, id: 5, name: "Alice");
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(3, saleId: sale.Id, actingAdminId: 1);

        Assert.True(result.Success);
        Assert.Equal("CardKey.Generated", result.Message);
        Assert.Equal(3, result.GeneratedCount);
        var cards = await db.Buyers.ToListAsync();
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.Equal(CardStatus.Unused, c.CardStatus));
        Assert.All(cards, c => Assert.Equal(sale.Id, c.SaleId));
        Assert.Equal(3, cards.Select(c => c.CardNo).Distinct().Count());
    }

    [Fact]
    public async Task GenerateWithoutSaleLeavesSaleIdNull()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(1, saleId: null, actingAdminId: 1);

        Assert.True(result.Success);
        Assert.Null((await db.Buyers.SingleAsync()).SaleId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GenerateRejectsOutOfRangeCount(int count)
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(count, saleId: null, actingAdminId: 1);

        Assert.False(result.Success);
        Assert.Equal("CardKey.CountInvalid", result.Message);
        Assert.Empty(await db.Buyers.ToListAsync());
    }

    [Fact]
    public async Task GenerateRejectsNonSaleSaleId()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { Id = 9, UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(1, saleId: 9, actingAdminId: 1);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SaleInvalid", result.Message);
        Assert.Empty(await db.Buyers.ToListAsync());
    }

    [Fact]
    public async Task DeleteSoftDeletesAndMarksDisabled()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardStatus = CardStatus.Unused });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.DeleteAsync(1, actingAdminId: 1);

        Assert.True(result.Success);
        var buyer = await db.Buyers.SingleAsync();
        Assert.True(buyer.IsDeleted);
        Assert.Equal(CardStatus.DeletedOrDisabled, buyer.CardStatus);
    }

    [Fact]
    public async Task DeleteMissingReturnsFailure()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.DeleteAsync(404, actingAdminId: 1);

        Assert.False(result.Success);
        Assert.Equal("CardKey.DeleteFailed", result.Message);
    }

    [Fact]
    public async Task ListFiltersByStatusSaleAndCardAndExcludesDeleted()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "alpha", CardStatus = CardStatus.Unused, SaleId = 5 },
            new Buyer { Id = 2, CardNo = "beta", CardStatus = CardStatus.Authorized, SaleId = 5 },
            new Buyer { Id = 3, CardNo = "gamma", CardStatus = CardStatus.Unused, SaleId = null },
            new Buyer { Id = 4, CardNo = "alpha-del", CardStatus = CardStatus.Unused, SaleId = 5, IsDeleted = true });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var byStatus = await service.ListAsync(CardStatus.Unused, null, null);
        Assert.Equal(new[] { 1L, 3L }, byStatus.Select(c => c.Id).OrderBy(x => x).ToArray());

        var bySale = await service.ListAsync(null, 5, null);
        Assert.Equal(new[] { 1L, 2L }, bySale.Select(c => c.Id).OrderBy(x => x).ToArray());
        Assert.All(bySale, c => Assert.Equal("Alice", c.SaleDisplayName));

        var byCard = await service.ListAsync(null, null, "alph");
        Assert.Equal(new[] { 1L }, byCard.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task ListSalesReturnsOnlySaleUsers()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Users.Add(new AppUser { Id = 9, UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var sales = await service.ListSalesAsync();

        Assert.Single(sales);
        Assert.Equal(5, sales[0].Id);
        Assert.Equal("Alice", sales[0].DisplayName);
    }

    private static AppUser SeedSale(WebMailDbContext db, long id, string name)
    {
        var user = new AppUser { Id = id, UserName = $"u{id}", DisplayName = name, Role = UserRole.Sales };
        db.Users.Add(user);
        return user;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 3: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeyServiceTests"`
Expected: 编译失败（`CardKeyService` 不存在）。

- [ ] **Step 4: 实现 `CardKeyService`**

创建 `src/WebMail/Services/CardKeyService.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services;

public sealed record CardKeyResult(bool Success, string Message, int GeneratedCount = 0);

public sealed record CardKeyListItem(
    long Id,
    string CardNo,
    CardStatus Status,
    long? SaleId,
    string? SaleDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UsedAt);

public sealed record SaleOption(long Id, string DisplayName);

public sealed class CardKeyService
{
    public const int MaxGenerateCount = 100;

    private readonly WebMailDbContext _db;
    private readonly CardGenerationService _cardGen;

    public CardKeyService(WebMailDbContext db, CardGenerationService cardGen)
    {
        _db = db;
        _cardGen = cardGen;
    }

    public async Task<CardKeyResult> GenerateAsync(int count, long? saleId, long? actingAdminId)
    {
        if (count < 1 || count > MaxGenerateCount)
        {
            return new(false, "CardKey.CountInvalid");
        }

        if (saleId is not null
            && !await _db.Users.AnyAsync(u => u.Id == saleId && u.Role == UserRole.Sales))
        {
            return new(false, "CardKey.SaleInvalid");
        }

        var generated = new HashSet<string>();
        while (generated.Count < count)
        {
            var candidate = _cardGen.GenerateCardNo();
            if (!generated.Add(candidate))
            {
                continue;
            }
            if (await _db.Buyers.AnyAsync(b => b.CardNo == candidate))
            {
                generated.Remove(candidate);
            }
        }

        foreach (var cardNo in generated)
        {
            _db.Buyers.Add(new Buyer
            {
                CardNo = cardNo,
                CardStatus = CardStatus.Unused,
                SaleId = saleId
            });
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminGenerateCardKeys",
            UserId = actingAdminId,
            Details = $"count={count};sale={saleId}"
        });
        await _db.SaveChangesAsync();
        return new(true, "CardKey.Generated", count);
    }

    public async Task<CardKeyResult> DeleteAsync(long id, long? actingAdminId)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is null)
        {
            return new(false, "CardKey.DeleteFailed");
        }

        buyer.IsDeleted = true;
        buyer.CardStatus = CardStatus.DeletedOrDisabled;
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminDeleteCardKey",
            UserId = actingAdminId,
            Details = $"buyer={id}"
        });
        await _db.SaveChangesAsync();
        return new(true, "CardKey.Deleted");
    }

    public async Task<IReadOnlyList<CardKeyListItem>> ListAsync(CardStatus? status, long? saleId, string? cardNo)
    {
        var query = _db.Buyers.Where(b => !b.IsDeleted);
        if (status is not null)
        {
            query = query.Where(b => b.CardStatus == status);
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
            b.SaleId,
            b.SaleId is not null && saleNames.TryGetValue(b.SaleId.Value, out var name) ? name : null,
            b.CreatedAt,
            b.CardUsedAt)).ToList();
    }

    public async Task<IReadOnlyList<SaleOption>> ListSalesAsync()
    {
        return await _db.Users
            .Where(u => u.Role == UserRole.Sales)
            .OrderBy(u => u.DisplayName)
            .Select(u => new SaleOption(u.Id, u.DisplayName))
            .ToListAsync();
    }
}
```

- [ ] **Step 5: 注册服务**

在 `src/WebMail/Program.cs` 第 28 行 `AddScoped<UserAdminService>()` 之后加：

```csharp
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<CardKeyService>();
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeyServiceTests"`
Expected: 全部 PASS。

- [ ] **Step 7: 提交**

```bash
git add src/WebMail/Domain/Entities.cs src/WebMail/Services/CardKeyService.cs src/WebMail/Program.cs tests/WebMail.Tests/CardKeyServiceTests.cs
git commit -m "feat(cardkey): add CardKeyService with generate/delete/list and CardUsedAt field"
```

---

### Task 2: `Verify` 回写 `SaleId` — 已取消（DROPPED）

> **执行期决定（2026-06-28）：** 取消本任务。代码库存在刻意的安全测试
> `BuyerPageModelTests.VerifyDoesNotTrustSaleIdFromPublicRequest`（提交 88b2fdd），
> 要求"不信任公开链接里的 saleid"。从公开链接回写 `SaleId` 会让任意人把卡密挂到
> 任意销售名下，破坏归属可信度。经用户裁定"听从安全"，`SaleId` **只**由管理员在
> 生成时指定（Task 4 生成表单的可选销售下拉）。`Verify.cshtml.cs` 不改动，
> 不新增 `VerifyModelTests.cs`。下方原始步骤保留作记录，不执行。

**Files:**
- Modify: `src/WebMail/Pages/Buyer/Verify.cshtml.cs:40-45`
- Test: `tests/WebMail.Tests/VerifyModelTests.cs`

**Interfaces:**
- Consumes: `VerifyModel(WebMailDbContext, IStringLocalizer<SharedResource>)`、`VerifyModel.OnGetAsync(string card, long? saleid)`、测试替身 `TestLocalizer.Shared`。
- Produces: 行为变更——当 `buyer.SaleId is null && saleid is not null` 时写入 `buyer.SaleId = saleid`。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/VerifyModelTests.cs`：

```csharp
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages.Buyer;
using Xunit;

namespace WebMail.Tests;

public sealed class VerifyModelTests
{
    [Fact]
    public async Task BackfillsSaleIdWhenBuyerHasNone()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardStatus = CardStatus.Unused, SaleId = null });
        await db.SaveChangesAsync();
        var model = new VerifyModel(db, TestLocalizer.Shared);

        await model.OnGetAsync("c1", saleid: 7);

        Assert.Equal(7, (await db.Buyers.SingleAsync()).SaleId);
    }

    [Fact]
    public async Task DoesNotOverwriteExistingSaleId()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardStatus = CardStatus.Unused, SaleId = 5 });
        await db.SaveChangesAsync();
        var model = new VerifyModel(db, TestLocalizer.Shared);

        await model.OnGetAsync("c1", saleid: 7);

        Assert.Equal(5, (await db.Buyers.SingleAsync()).SaleId);
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~VerifyModelTests"`
Expected: `BackfillsSaleIdWhenBuyerHasNone` FAIL（SaleId 仍为 null）。

- [ ] **Step 3: 实现回写**

修改 `src/WebMail/Pages/Buyer/Verify.cshtml.cs`，把第 40-45 行：

```csharp
        if (buyer.CardStatus == CardStatus.Unused)
        {
            buyer.CardStatus = CardStatus.Entered;
        }

        await _db.SaveChangesAsync();
```

改为：

```csharp
        if (buyer.CardStatus == CardStatus.Unused)
        {
            buyer.CardStatus = CardStatus.Entered;
        }

        if (buyer.SaleId is null && saleid is not null)
        {
            buyer.SaleId = saleid;
        }

        await _db.SaveChangesAsync();
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~VerifyModelTests"`
Expected: 全部 PASS。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Pages/Buyer/Verify.cshtml.cs tests/WebMail.Tests/VerifyModelTests.cs
git commit -m "feat(cardkey): backfill Buyer.SaleId from verify link when unset"
```

---

### Task 3: `Callback` 记录 `CardUsedAt`

**Files:**
- Modify: `src/WebMail/Pages/OAuth/Callback.cshtml.cs:95`
- Test: `tests/WebMail.Tests/OAuthCallbackModelTests.cs`（扩展）

**Interfaces:**
- Consumes: 现有 `CallbackModel.OnGetAsync(...)`、`FakeOAuthStateStore`、`FakeAuthProvider`、`FakeTokenProtector`、`Buyer.CardUsedAt`（Task 1 已加）。
- Produces: 行为变更——授权成功设 `Authorized` 时 `buyer.CardUsedAt ??= DateTimeOffset.UtcNow`（仅记首次）。

- [ ] **Step 1: 写失败测试**

在 `tests/WebMail.Tests/OAuthCallbackModelTests.cs` 中，`ForgedStateIsRejectedAndNothingIsBound` 测试方法之后插入两个测试：

```csharp
    [Fact]
    public async Task FirstAuthorizationStampsCardUsedAt()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "c1", CardStatus = CardStatus.Entered });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "c1");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "c1");
        Assert.NotNull(buyer.CardUsedAt);
    }

    [Fact]
    public async Task ReauthorizationDoesNotChangeCardUsedAt()
    {
        await using var db = CreateDb();
        var firstUsed = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var buyer = new Buyer { CardNo = "c2", CardStatus = CardStatus.Authorized, EmailStatus = EmailAuthorizationStatus.Abnormal, CardUsedAt = firstUsed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Provider = "Gmail", Email = "same@example.com", ProviderUserId = "u", EncryptedRefreshToken = "enc:old" });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "c2");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "same@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        Assert.Equal(firstUsed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).CardUsedAt);
    }
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~OAuthCallbackModelTests"`
Expected: `FirstAuthorizationStampsCardUsedAt` FAIL（`CardUsedAt` 为 null）。

- [ ] **Step 3: 实现使用时间记录**

修改 `src/WebMail/Pages/OAuth/Callback.cshtml.cs` 第 95 行：

```csharp
        buyer.CardStatus = CardStatus.Authorized;
```

改为：

```csharp
        buyer.CardStatus = CardStatus.Authorized;
        buyer.CardUsedAt ??= DateTimeOffset.UtcNow;
```

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~OAuthCallbackModelTests"`
Expected: 全部 PASS（含原有 4 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Pages/OAuth/Callback.cshtml.cs tests/WebMail.Tests/OAuthCallbackModelTests.cs
git commit -m "feat(cardkey): stamp Buyer.CardUsedAt on first authorization"
```

---

### Task 4: 管理页 `Pages/Admin/CardKeys`（PageModel + 视图）

**Files:**
- Create: `src/WebMail/Pages/Admin/CardKeys.cshtml.cs`
- Create: `src/WebMail/Pages/Admin/CardKeys.cshtml`
- Test: `tests/WebMail.Tests/CardKeysModelTests.cs`

**Interfaces:**
- Consumes: `CardKeyService`（Task 1）、`IStringLocalizer<SharedResource>`、`TestLocalizer.Shared`。
- Produces: `CardKeysModel`（绑定属性 `StatusFilter`/`SaleFilter`/`CardNo`/`GenerateCount`/`GenerateSaleId`；处理器 `OnGetAsync`/`OnPostGenerateAsync`/`OnPostDeleteAsync(long id)`；只读集合 `Cards`/`Sales`；`Message`）。视图 `/Admin/CardKeys`。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/CardKeysModelTests.cs`：

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages.Admin;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class CardKeysModelTests
{
    [Fact]
    public async Task GenerateHandlerCreatesCardsAndLoadsList()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.GenerateCount = 3;
        model.GenerateSaleId = null;

        await model.OnPostGenerateAsync();

        Assert.Equal("CardKey.Generated", model.Message);
        Assert.Equal(3, model.Cards.Count);
        Assert.Equal(3, await db.Buyers.CountAsync());
    }

    [Fact]
    public async Task GenerateHandlerSurfacesValidationMessage()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.GenerateCount = 0;

        await model.OnPostGenerateAsync();

        Assert.Equal("CardKey.CountInvalid", model.Message);
        Assert.Empty(model.Cards);
    }

    [Fact]
    public async Task DeleteHandlerSoftDeletesAndRemovesFromList()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardStatus = CardStatus.Unused });
        await db.SaveChangesAsync();
        var model = CreateModel(db);

        await model.OnPostDeleteAsync(1);

        Assert.Equal("CardKey.Deleted", model.Message);
        Assert.Empty(model.Cards);
        Assert.True((await db.Buyers.SingleAsync()).IsDeleted);
    }

    private static CardKeysModel CreateModel(WebMailDbContext db) =>
        new(new CardKeyService(db, new CardGenerationService()), TestLocalizer.Shared)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeysModelTests"`
Expected: 编译失败（`CardKeysModel` 不存在）。

- [ ] **Step 3: 实现 PageModel**

创建 `src/WebMail/Pages/Admin/CardKeys.cshtml.cs`：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class CardKeysModel : PageModel
{
    private readonly CardKeyService _cardKeys;
    private readonly IStringLocalizer<SharedResource> _loc;

    public CardKeysModel(CardKeyService cardKeys, IStringLocalizer<SharedResource> loc)
    {
        _cardKeys = cardKeys;
        _loc = loc;
    }

    public IReadOnlyList<CardKeyListItem> Cards { get; private set; } = Array.Empty<CardKeyListItem>();
    public IReadOnlyList<SaleOption> Sales { get; private set; } = Array.Empty<SaleOption>();
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public CardStatus? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public long? SaleFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? CardNo { get; set; }

    [BindProperty] public int GenerateCount { get; set; } = 1;
    [BindProperty] public long? GenerateSaleId { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostGenerateAsync()
    {
        Message = _loc[(await _cardKeys.GenerateAsync(GenerateCount, GenerateSaleId, AdminId())).Message];
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        Message = _loc[(await _cardKeys.DeleteAsync(id, AdminId())).Message];
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Cards = await _cardKeys.ListAsync(StatusFilter, SaleFilter, CardNo);
        Sales = await _cardKeys.ListSalesAsync();
    }

    private long? AdminId() =>
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
```

- [ ] **Step 4: 实现视图**

创建 `src/WebMail/Pages/Admin/CardKeys.cshtml`：

```cshtml
@page
@using WebMail.Domain
@model WebMail.Pages.Admin.CardKeysModel
@{
    ViewData["Title"] = L["Admin.CardKeys.Title"].Value;
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

<form method="get" class="row g-2 mb-3">
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
        <a class="btn btn-outline-secondary" asp-page="/Admin/CardKeys">@L["Common.Reset"]</a>
    </div>
</form>

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
                <th>@L["Table.CardNo"]</th>
                <th>@L["Table.Status"]</th>
                <th>@L["CardKey.Sale"]</th>
                <th>@L["Table.CreatedAt"]</th>
                <th>@L["CardKey.UsedAt"]</th>
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
                    <td>@card.CardNo</td>
                    <td>@card.Status</td>
                    <td>@(card.SaleDisplayName ?? "-")</td>
                    <td>@card.CreatedAt</td>
                    <td>@(card.UsedAt is null ? L["CardKey.NotUsed"].Value : card.UsedAt.ToString())</td>
                    <td>
                        <div class="input-group input-group-sm">
                            <input class="form-control" type="text" value="@link" readonly />
                            <button type="button" class="btn btn-outline-secondary copy-link" data-link="@link">@L["CardKey.Copy"]</button>
                        </div>
                    </td>
                    <td>
                        <form method="post" asp-page-handler="Delete">
                            <input type="hidden" name="id" value="@card.Id" />
                            <button type="submit" class="btn btn-sm btn-outline-danger">@L["Common.Delete"]</button>
                        </form>
                    </td>
                </tr>
            }
        </tbody>
    </table>
    </div>
}

@section Scripts {
    <script>
        document.querySelectorAll('.copy-link').forEach(function (btn) {
            btn.addEventListener('click', function () {
                navigator.clipboard.writeText(btn.getAttribute('data-link'));
            });
        });
    </script>
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~CardKeysModelTests"`
Expected: 全部 PASS。

- [ ] **Step 6: 整体编译确认视图无误**

Run: `dotnet build src/WebMail/WebMail.csproj`
Expected: Build succeeded（无 Razor 编译错误）。

- [ ] **Step 7: 提交**

```bash
git add src/WebMail/Pages/Admin/CardKeys.cshtml.cs src/WebMail/Pages/Admin/CardKeys.cshtml tests/WebMail.Tests/CardKeysModelTests.cs
git commit -m "feat(cardkey): add admin card-key management page"
```

---

### Task 5: 本地化文案 + 导航入口

**Files:**
- Modify: `src/WebMail/Resources/SharedResource.zh-CN.resx`
- Modify: `src/WebMail/Resources/SharedResource.en.resx`
- Modify: `src/WebMail/Pages/Shared/_Layout.cshtml:37`

**Interfaces:**
- Consumes: 前述任务用到的资源 key 与页面路由 `/Admin/CardKeys`。
- Produces: 中英文文案与管理后台下拉里的"卡密管理"入口。

- [ ] **Step 1: 加中文资源**

在 `src/WebMail/Resources/SharedResource.zh-CN.resx` 的 `</root>` 之前插入：

```xml
  <data name="Nav.Admin.CardKeys" xml:space="preserve"><value>卡密管理</value></data>
  <data name="Admin.CardKeys.Title" xml:space="preserve"><value>卡密管理</value></data>
  <data name="Admin.CardKeys.GenerateHeading" xml:space="preserve"><value>生成卡密</value></data>
  <data name="Admin.CardKeys.Empty" xml:space="preserve"><value>暂无卡密。</value></data>
  <data name="CardKey.Generate" xml:space="preserve"><value>生成</value></data>
  <data name="CardKey.Count" xml:space="preserve"><value>数量</value></data>
  <data name="CardKey.Sale" xml:space="preserve"><value>所属销售</value></data>
  <data name="CardKey.SaleUnassigned" xml:space="preserve"><value>不指定销售</value></data>
  <data name="CardKey.AllStatus" xml:space="preserve"><value>全部状态</value></data>
  <data name="CardKey.AllSales" xml:space="preserve"><value>全部销售</value></data>
  <data name="CardKey.UsedAt" xml:space="preserve"><value>使用时间</value></data>
  <data name="CardKey.NotUsed" xml:space="preserve"><value>未使用</value></data>
  <data name="CardKey.Link" xml:space="preserve"><value>链接</value></data>
  <data name="CardKey.Copy" xml:space="preserve"><value>复制</value></data>
  <data name="CardKey.Generated" xml:space="preserve"><value>卡密已生成。</value></data>
  <data name="CardKey.CountInvalid" xml:space="preserve"><value>生成数量需在 1 到 100 之间。</value></data>
  <data name="CardKey.SaleInvalid" xml:space="preserve"><value>所选销售无效。</value></data>
  <data name="CardKey.Deleted" xml:space="preserve"><value>卡密已删除。</value></data>
  <data name="CardKey.DeleteFailed" xml:space="preserve"><value>删除失败：卡密不存在。</value></data>
```

- [ ] **Step 2: 加英文资源**

在 `src/WebMail/Resources/SharedResource.en.resx` 的 `</root>` 之前插入：

```xml
  <data name="Nav.Admin.CardKeys" xml:space="preserve"><value>Card Keys</value></data>
  <data name="Admin.CardKeys.Title" xml:space="preserve"><value>Card Key Management</value></data>
  <data name="Admin.CardKeys.GenerateHeading" xml:space="preserve"><value>Generate Card Keys</value></data>
  <data name="Admin.CardKeys.Empty" xml:space="preserve"><value>No card keys yet.</value></data>
  <data name="CardKey.Generate" xml:space="preserve"><value>Generate</value></data>
  <data name="CardKey.Count" xml:space="preserve"><value>Count</value></data>
  <data name="CardKey.Sale" xml:space="preserve"><value>Sales</value></data>
  <data name="CardKey.SaleUnassigned" xml:space="preserve"><value>No sales assigned</value></data>
  <data name="CardKey.AllStatus" xml:space="preserve"><value>All statuses</value></data>
  <data name="CardKey.AllSales" xml:space="preserve"><value>All sales</value></data>
  <data name="CardKey.UsedAt" xml:space="preserve"><value>Used at</value></data>
  <data name="CardKey.NotUsed" xml:space="preserve"><value>Not used</value></data>
  <data name="CardKey.Link" xml:space="preserve"><value>Link</value></data>
  <data name="CardKey.Copy" xml:space="preserve"><value>Copy</value></data>
  <data name="CardKey.Generated" xml:space="preserve"><value>Card keys generated.</value></data>
  <data name="CardKey.CountInvalid" xml:space="preserve"><value>Count must be between 1 and 100.</value></data>
  <data name="CardKey.SaleInvalid" xml:space="preserve"><value>Selected sales is invalid.</value></data>
  <data name="CardKey.Deleted" xml:space="preserve"><value>Card key deleted.</value></data>
  <data name="CardKey.DeleteFailed" xml:space="preserve"><value>Delete failed: card key not found.</value></data>
```

- [ ] **Step 3: 加导航入口**

修改 `src/WebMail/Pages/Shared/_Layout.cshtml` 第 37 行，在买家管理项之后加一行：

```cshtml
                                    <li><a class="dropdown-item" asp-page="/Admin/Buyers">@L["Nav.Admin.Buyers"]</a></li>
                                    <li><a class="dropdown-item" asp-page="/Admin/CardKeys">@L["Nav.Admin.CardKeys"]</a></li>
```

- [ ] **Step 4: 编译确认**

Run: `dotnet build src/WebMail/WebMail.csproj`
Expected: Build succeeded。

- [ ] **Step 5: 手动验证（运行应用）**

Run: `dotnet run --project src/WebMail/WebMail.csproj`
以管理员登录 → 管理后台下拉应出现"卡密管理" → 生成 3 张、按销售/状态/卡号筛选、复制链接、删除一张。中英文切换文案正确。
（提示：若旧 `webmail.dev.db` 无 `CardUsedAt` 列报错，先删除该文件再运行重建。）

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Resources/SharedResource.zh-CN.resx src/WebMail/Resources/SharedResource.en.resx src/WebMail/Pages/Shared/_Layout.cshtml
git commit -m "feat(cardkey): localize card-key page and add admin nav entry"
```

---

## Self-Review

**Spec coverage：**
- 生成卡密（批量、上限 100）→ Task 1 `GenerateAsync` + Task 4 页面。✅
- 删除卡密（软删除）→ Task 1 `DeleteAsync` + Task 4 页面。✅
- 是否使用 / 使用时间 → Task 1 `CardUsedAt` 字段 + Task 3 回调记录 + Task 4 列表展示。✅
- 所属销售（生成时指定）→ Task 1 生成绑定。✅（链接回写/Task 2 已取消：与既有安全测试 `VerifyDoesNotTrustSaleIdFromPublicRequest` 冲突，经用户裁定听从安全。）
- URL 格式 `?card=..&saleid=..` → Task 4 视图链接构造（用 `saleid`）。✅
- 筛选（状态/销售/卡号）→ Task 1 `ListAsync` + Task 4 视图。✅
- 本地化 + 导航 → Task 5。✅
- 测试覆盖 → Task 1/2/3/4 均含 xUnit 测试。✅

**Placeholder scan：** 无 TBD/TODO，所有代码步骤含完整代码。✅

**Type consistency：** `CardKeyResult`/`CardKeyListItem`/`SaleOption` 在 Task 1 定义，Task 4 一致使用；方法名 `GenerateAsync`/`DeleteAsync`/`ListAsync`/`ListSalesAsync` 全程一致；`CardUsedAt` 在 Task 1 定义、Task 3 写入、Task 4 读取（`UsedAt` 列）。✅

**未做项（YAGNI）：** 不做导出/有效期/硬删除/编辑、不引入 EF 迁移（沿用 `EnsureCreated`）。
