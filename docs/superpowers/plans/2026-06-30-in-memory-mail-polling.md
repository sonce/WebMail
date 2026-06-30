# 内存邮件缓存 + AJAX 轮询 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把邮件从"后台定时拉取 + DB 持久化"改为"内存缓存最新 10 条 + AJAX 轮询按需拉取 Gmail"，页面带自动轮询开关与手动刷新按钮，拉取失败时返回旧数据并界面提示。

**Architecture:** 新增单例 `MailCacheService`（按 `buyerId` 缓存最新 10 条 + TTL），`Supplier/Mail` 页面新增 `OnGetPoll` JSON 处理器复用现有授权/访问检查；前端用已加载的 jQuery 轮询该处理器。删除 `MailSyncBackgroundService`/`MailSyncProcessor`/`MailSyncJobQueueService`/`MailSyncPlanner` 及 `EmailMessage`/`SyncJob`/`ActiveSyncWindow` 实体与表。

**Tech Stack:** ASP.NET Core 8 Razor Pages, EF Core (in-memory for tests), xUnit, jQuery（已在 `_Layout` 加载）。

## Global Constraints

- 仓库在 `master` 分支做特性开发，无 remote；提交保持 surgical/local，勿动用户在途的未提交改动。
- 资源文案走 i18n：新文案同时在 `SharedResource.en.resx` 与 `SharedResource.zh-CN.resx` 添加。
- 测试沿用 xUnit + in-memory DB + fake provider 风格（参考原 `MailSyncProcessorTests`）。
- 生产代码改动必须先有失败测试（TDD）。
- `MailSync:InitialSyncDays`（缺省 30）保留；新增 `MailSync:CacheTtlSeconds`（缺省 30）。
- 每个任务结束 `dotnet build` + `dotnet test` 全绿后提交。

---

## File Structure

**Create:**
- `src/WebMail/Domain/MailMessageView.cs` — 列表视图 record（`Id, Sender, Subject, SentAt, Folder`）。
- `src/WebMail/Services/MailCacheService.cs` — `IMailCacheService` + `MailCacheService`（单例）+ `MailCacheResult` record + `internal static` 截断/映射方法。
- `tests/WebMail.Tests/MailCacheServiceTests.cs` — `MailCacheService` 单测。

**Modify:**
- `src/WebMail/Domain/Entities.cs` — 删除 `EmailMessage`、`SyncJob`、`ActiveSyncWindow` 实体。
- `src/WebMail/Data/WebMailDbContext.cs` — 删除三个 `DbSet` 与对应索引配置。
- `src/WebMail/Program.cs` — 删除四个旧服务注册，新增 `AddSingleton<IMailCacheService, MailCacheService>()`。
- `src/WebMail/Pages/Supplier/Mail.cshtml.cs` — 移除 `ActiveSyncWindow` 写入；`OnGet` 改读缓存；新增 `OnGetPoll`；构造注入 `IMailCacheService`；`Messages` 类型改 `IReadOnlyList<MailMessageView>`。
- `src/WebMail/Pages/Supplier/Mail.cshtml` — 移除"同步窗口到期"行；表格由 JS 重绘；加开关/刷新按钮/`@section Scripts`。
- `src/WebMail/appsettings.json` — `MailSync` 节加 `CacheTtlSeconds`，删 `ActiveWindowMinutes`/`ActiveWindowIntervalMinutes`/`GlobalIntervalMinutes`。
- `src/WebMail/Resources/SharedResource.en.resx` + `SharedResource.zh-CN.resx` — 新增轮询相关文案 key。
- `tests/WebMail.Tests/SupplierMailModelTests.cs` — 适配 `MailModel` 构造变更，加 `OnGetPoll` 测试。

**Delete:**
- `src/WebMail/Services/Background/MailSyncBackgroundService.cs`
- `src/WebMail/Services/Background/MailSyncJobQueueService.cs`
- `src/WebMail/Services/Background/MailSyncProcessor.cs`
- `src/WebMail/Services/MailSyncPlanner.cs`
- `tests/WebMail.Tests/MailSyncProcessorTests.cs`
- `tests/WebMail.Tests/MailSyncJobQueueServiceTests.cs`

---

### Task 1: `MailMessageView` 视图 record

**Files:**
- Create: `src/WebMail/Domain/MailMessageView.cs`

**Interfaces:**
- Produces: `public sealed record MailMessageView(string Id, string Sender, string Subject, DateTimeOffset SentAt, MailFolder Folder);` — 位于 `WebMail.Domain`，被 Task 4 `MailCacheService` 与 Task 6 `MailModel` 消费。

- [ ] **Step 1: 创建 record**

```csharp
namespace WebMail.Domain;

public sealed record MailMessageView(
    string Id,
    string Sender,
    string Subject,
    DateTimeOffset SentAt,
    MailFolder Folder);
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build src/WebMail`
Expected: 成功，0 错误。

- [ ] **Step 3: 提交**

```bash
git add src/WebMail/Domain/MailMessageView.cs
git commit -m "feat(mail): add MailMessageView list-view record"
```

---

### Task 2: `MailCacheService` 截断/映射纯逻辑 + 单测

先测纯逻辑（`internal static`），不动 DI / DB。

**Files:**
- Create: `src/WebMail/Services/MailCacheService.cs`（本任务仅放 record + 静态方法，接口与服务实现放 Task 4）
- Create: `tests/WebMail.Tests/MailCacheServiceTests.cs`

**Interfaces:**
- Consumes: `WebMail.Domain.MailMessageView`, `WebMail.Domain.MailFolder`, `WebMail.Services.EmailProviders.ProviderMessage`（来自 `IEmailProvider.cs`）。
- Produces:
  - `public sealed record MailCacheResult(IReadOnlyList<MailMessageView> Messages, bool Stale, string? Error);`（`WebMail.Services`）
  - `internal static IReadOnlyList<MailMessageView> MailCacheService.ProjectLatest(IReadOnlyList<ProviderMessage> messages, int limit = 10)`（`WebMail.Services`）

`ProviderMessage` 字段（来自 `IEmailProvider.cs:5`）：`ProviderMessageId, ProviderThreadId, Sender, Recipients, Subject, SentAt, TextBody, HtmlBody, AttachmentMetadataJson, Folder`。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/MailCacheServiceTests.cs`：

```csharp
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class MailCacheServiceTests
{
    [Fact]
    public void ProjectLatestKeepsLimitNewestBySentAtDescending()
    {
        var baseTime = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var messages = Enumerable.Range(0, 15)
            .Select(i => new ProviderMessage(
                $"m-{i}", null, "s@x.com", "r@x.com", "sub",
                baseTime.AddMinutes(i), null, null, null, MailFolder.Inbox))
            .ToArray();

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        Assert.Equal(10, result.Count);
        // 最新的 10 条 = m-6 .. m-14，倒序排列（m-14 在前）
        Assert.Equal("m-14", result[0].Id);
        Assert.Equal("m-5", result[9].Id);
    }

    [Fact]
    public void ProjectLatestMapsFieldsAndFolder()
    {
        var sentAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            new ProviderMessage("id-1", "t", "sender@x.com", "r", "Hello",
                sentAt, "body", null, null, MailFolder.Junk)
        };

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        var view = Assert.Single(result);
        Assert.Equal("id-1", view.Id);
        Assert.Equal("sender@x.com", view.Sender);
        Assert.Equal("Hello", view.Subject);
        Assert.Equal(sentAt, view.SentAt);
        Assert.Equal(MailFolder.Junk, view.Folder);
    }

    [Fact]
    public void ProjectLatestReturnsAllWhenFewerThanLimit()
    {
        var messages = new[]
        {
            new ProviderMessage("m-0", null, "s", "r", "x",
                DateTimeOffset.UtcNow, null, null, null, MailFolder.Inbox)
        };

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        Assert.Single(result);
    }
}
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/WebMail.Tests --filter "FullyQualifiedName~MailCacheServiceTests"`
Expected: FAIL — `MailCacheService` 未定义 / `ProjectLatest` 不存在。

- [ ] **Step 3: 实现最小代码**

创建 `src/WebMail/Services/MailCacheService.cs`：

```csharp
using WebMail.Domain;
using WebMail.Services.EmailProviders;

namespace WebMail.Services;

public sealed record MailCacheResult(
    IReadOnlyList<MailMessageView> Messages,
    bool Stale,
    string? Error);

public sealed partial class MailCacheService
{
    internal static IReadOnlyList<MailMessageView> ProjectLatest(IReadOnlyList<ProviderMessage> messages, int limit = 10) =>
        messages
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .Select(m => new MailMessageView(
                m.ProviderMessageId,
                m.Sender,
                m.Subject,
                m.SentAt,
                m.Folder))
            .ToList();
}
```

> 用 `partial class` 以便 Task 4 在同文件追加实例部分（保持单一职责文件，避免跨文件拼接）。

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/WebMail.Tests --filter "FullyQualifiedName~MailCacheServiceTests"`
Expected: PASS（3 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Services/MailCacheService.cs tests/WebMail.Tests/MailCacheServiceTests.cs
git commit -m "feat(mail): add MailCacheResult + ProjectLatest projection"
```

---

### Task 3: `MailCacheService` 接口与实例实现 + 单测

**Files:**
- Modify: `src/WebMail/Services/MailCacheService.cs`（追加接口 + 实例方法）
- Modify: `tests/WebMail.Tests/MailCacheServiceTests.cs`（追加缓存/TTL/降级测试）

**Interfaces:**
- Consumes:
  - `IEmailProviderResolver.Resolve(string)` → `IEmailProvider`（`EmailProviderResolver.cs:14`）
  - `IEmailProvider.FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken)` → `IReadOnlyList<ProviderMessage>`
  - `ITokenProtector.Unprotect(string)` → `string`（`ITokenProtector.cs:6`）
  - `WebMailDbContext.EmailAccounts` / `Buyers` / `AuditLogs`（scoped）
  - `ProviderAuthorizationException`（`EmailProviders` 命名空间）
  - `AuditLog { Action, Details }`（`Entities.cs:94`）
- Produces:
  - `public interface IMailCacheService { Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken cancellationToken); }`
  - `public sealed partial class MailCacheService(IServiceScopeFactory scopeFactory, IEmailProviderResolver providers, IConfiguration configuration, ITokenProtector tokenProtector) : IMailCacheService`

- [ ] **Step 1: 写失败测试（缓存/TTL/降级）**

在 `MailCacheServiceTests.cs` 追加（顶部 using 增补 `Microsoft.Extensions.Configuration`、`Microsoft.EntityFrameworkCore`、`WebMail.Data`、`WebMail.Domain`、`WebMail.Services.EmailProviders`、`WebMail.Services.Security`）：

```csharp
    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static MailCacheService CreateService(IEmailProvider provider, WebMailDbContext? db = null, int ttlSeconds = 30)
    {
        var resolver = new EmailProviderResolver([provider]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MailSync:InitialSyncDays"] = "30",
                ["MailSync:CacheTtlSeconds"] = ttlSeconds.ToString()
            })
            .Build();
        var scopeFactory = new StubScopeFactory(db ?? CreateDb());
        return new MailCacheService(scopeFactory, resolver, config, new FakeTokenProtector());
    }

    private static EmailAccount Account(long buyerId = 1, long accountId = 1, string provider = "Fake") => new()
    {
        Id = accountId, BuyerId = buyerId, Email = "b@x.com",
        Provider = provider, ProviderUserId = "p", EncryptedRefreshToken = "enc:token"
    };

    private static ProviderMessage Msg(string id, int minOffset = 0, MailFolder folder = MailFolder.Inbox) =>
        new(id, null, "s@x.com", "r@x.com", "sub",
            new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero).AddMinutes(minOffset),
            null, null, null, folder);

    [Fact]
    public async Task GetOrFetchReturnsCachedWithinTtlWithoutRefetch()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account());
        await db.SaveChangesAsync();
        var provider = new CountingProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        await svc.GetOrFetchAsync(buyerId: 1, force: false, CancellationToken.None);
        await svc.GetOrFetchAsync(buyerId: 1, force: false, CancellationToken.None);

        Assert.Equal(1, provider.FetchCount);
    }

    [Fact]
    public async Task GetOrFetchForcesRefreshWhenForced()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account());
        await db.SaveChangesAsync();
        var provider = new CountingProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        await svc.GetOrFetchAsync(buyerId: 1, force: false, CancellationToken.None);
        await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task GetOrFetchReturnsStaleCacheWithErrorOnFetchFailure()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account());
        await db.SaveChangesAsync();
        var provider = new ToggleProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        var first = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);
        provider.Throw = true;
        var second = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.False(first.Stale);
        Assert.True(second.Stale);
        Assert.NotNull(second.Error);
        Assert.Single(second.Messages);
        Assert.Equal("m-1", second.Messages[0].Id);
    }

    [Fact]
    public async Task GetOrFetchReturnsEmptyWithErrorOnFirstFailure()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account(provider: "Boom"));
        await db.SaveChangesAsync();
        var provider = new ThrowingProvider("Boom");
        var svc = CreateService(provider, db, ttlSeconds: 30);

        var result = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.Empty(result.Messages);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task GetOrFetchReturnsEmptyWithErrorWhenNoAccount()
    {
        await using var db = CreateDb();
        var provider = new CountingProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        var result = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.Empty(result.Messages);
        Assert.NotNull(result.Error);
        Assert.Equal(0, provider.FetchCount);
    }

    [Fact]
    public async Task GetOrFetchFlipsBuyerToAbnormalOnAuthFailure()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer
        {
            Id = 1, CardNo = "c1", EmailStatus = EmailAuthorizationStatus.Authorized,
            Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved
        });
        db.EmailAccounts.Add(Account(provider: "AuthBoom"));
        await db.SaveChangesAsync();
        var provider = new AuthThrowingProvider("AuthBoom");
        var svc = CreateService(provider, db, ttlSeconds: 30);

        await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.Id == 1);
        Assert.Equal(EmailAuthorizationStatus.Abnormal, buyer.EmailStatus);
    }

    // ---- test doubles ----

    private sealed class CountingProvider(string name, IReadOnlyList<ProviderMessage> messages) : IEmailProvider
    {
        public string Name { get; } = name;
        public int FetchCount { get; private set; }
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct)
        { FetchCount++; return Task.FromResult(messages); }
    }

    private sealed class ToggleProvider(string name, IReadOnlyList<ProviderMessage> messages) : IEmailProvider
    {
        public string Name { get; } = name;
        public bool Throw { get; set; }
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct)
            => Throw ? throw new InvalidOperationException("network down") : Task.FromResult(messages);
    }

    private sealed class ThrowingProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed class AuthThrowingProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct) => throw new ProviderAuthorizationException("auth failed");
    }

    private sealed class StubScopeFactory(WebMailDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope(db);
        private sealed class StubScope(WebMailDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new StubProvider(db);
            public void Dispose() { }
        }
        private sealed class StubProvider(WebMailDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(WebMailDbContext) ? db : null;
        }
    }
```

> `StubScopeFactory` 让单例 `MailCacheService` 每次开 scope 拿到同一个 in-memory `WebMailDbContext` 实例（测试可控）。

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/WebMail.Tests --filter "FullyQualifiedName~MailCacheServiceTests"`
Expected: FAIL — `IMailCacheService`/`GetOrFetchAsync` 未定义。

- [ ] **Step 3: 实现接口 + 实例方法**

在 `src/WebMail/Services/MailCacheService.cs` 追加（保留 Task 2 的 record 与 `ProjectLatest`，整体替换文件内容为下方合并版）：

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;
using WebMail.Services.Security;

namespace WebMail.Services;

public sealed record MailCacheResult(
    IReadOnlyList<MailMessageView> Messages,
    bool Stale,
    string? Error);

public interface IMailCacheService
{
    Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken cancellationToken);
}

public sealed partial class MailCacheService(
    IServiceScopeFactory scopeFactory,
    IEmailProviderResolver providers,
    IConfiguration configuration,
    ITokenProtector tokenProtector) : IMailCacheService
{
    private readonly ConcurrentDictionary<long, CacheEntry> _cache = new();

    public async Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken cancellationToken)
    {
        var ttlSeconds = configuration.GetValue("MailSync:CacheTtlSeconds", 30);
        var now = DateTimeOffset.UtcNow;

        if (!force && _cache.TryGetValue(buyerId, out var entry) && (now - entry.FetchedAt).TotalSeconds < ttlSeconds)
        {
            return new MailCacheResult(entry.Messages, Stale: false, Error: null);
        }

        try
        {
            var fetched = await FetchAsync(buyerId, cancellationToken);
            var views = ProjectLatest(fetched);
            _cache[buyerId] = new CacheEntry(views, now);
            return new MailCacheResult(views, Stale: false, Error: null);
        }
        catch (ProviderAuthorizationException)
        {
            await MarkBuyerAbnormalAsync(buyerId, cancellationToken);
            return Degraded(buyerId, "邮件刷新失败，且无历史数据");
        }
        catch
        {
            return Degraded(buyerId, "邮件刷新失败，以下为上次结果");
        }
    }

    private MailCacheResult Degraded(long buyerId, string emptyError)
    {
        if (_cache.TryGetValue(buyerId, out var stale) && stale.Messages.Count > 0)
        {
            return new MailCacheResult(stale.Messages, Stale: true, Error: "邮件刷新失败，以下为上次结果");
        }
        return new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: emptyError);
    }

    private async Task<IReadOnlyList<ProviderMessage>> FetchAsync(long buyerId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();

        var account = await db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyerId, cancellationToken);
        if (account is null)
        {
            return [];
        }

        var days = configuration.GetValue("MailSync:InitialSyncDays", 30);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var refreshToken = tokenProtector.Unprotect(account.EncryptedRefreshToken);
        var provider = providers.Resolve(account.Provider);
        return await provider.FetchMessagesAsync(refreshToken, Array.Empty<string>(), since, cancellationToken);
    }

    private async Task MarkBuyerAbnormalAsync(long buyerId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
        var buyer = await db.Buyers.FirstOrDefaultAsync(b => b.Id == buyerId, cancellationToken);
        if (buyer is not null && buyer.EmailStatus == EmailAuthorizationStatus.Authorized)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Abnormal;
            db.AuditLogs.Add(new AuditLog { Action = "MailboxAbnormal", Details = $"buyer={buyerId}" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    internal static IReadOnlyList<MailMessageView> ProjectLatest(IReadOnlyList<ProviderMessage> messages, int limit = 10) =>
        messages
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .Select(m => new MailMessageView(
                m.ProviderMessageId,
                m.Sender,
                m.Subject,
                m.SentAt,
                m.Folder))
            .ToList();

    private sealed record CacheEntry(IReadOnlyList<MailMessageView> Messages, DateTimeOffset FetchedAt);
}
```

> `EmailAccount` 不存在时 `FetchAsync` 返回空数组 → `ProjectLatest([])` = 空 → 写入空缓存 → 返回 `Messages=空, Stale=false, Error=null`。但 spec 要求"无账号"时带 `Error`。修正：在 `GetOrFetchAsync` 中区分空账号。见 Step 3b。

- [ ] **Step 3b: 区分"无账号"错误**

`FetchAsync` 返回空数组无法区分"无账号"与"真无邮件"。改为抛专用异常。把 `FetchAsync` 中 `if (account is null) return [];` 改为：

```csharp
        var account = await db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyerId, cancellationToken);
        if (account is null)
        {
            throw new AccountNotFoundException(buyerId);
        }
```

并在文件底部追加：

```csharp
internal sealed class AccountNotFoundException(long buyerId) : Exception($"No email account for buyer {buyerId}.");
```

在 `GetOrFetchAsync` 的 catch 块中，`AccountNotFoundException` 不应触发 `MarkBuyerAbnormal`，且错误信息不同。把两个 catch 之间插入：

```csharp
        catch (AccountNotFoundException)
        {
            return new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: "该买家未绑定邮箱账号");
        }
```

（置于 `ProviderAuthorizationException` catch 之前。）

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/WebMail.Tests --filter "FullyQualifiedName~MailCacheServiceTests"`
Expected: PASS（3 + 6 = 9 个测试）。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Services/MailCacheService.cs tests/WebMail.Tests/MailCacheServiceTests.cs
git commit -m "feat(mail): add MailCacheService with TTL cache + stale-on-failure"
```

---

### Task 4: 注册 `MailCacheService` + 删除旧同步服务与实体

**Files:**
- Modify: `src/WebMail/Program.cs`
- Modify: `src/WebMail/Domain/Entities.cs`（删 `EmailMessage`/`SyncJob`/`ActiveSyncWindow`）
- Modify: `src/WebMail/Data/WebMailDbContext.cs`（删三个 DbSet + 索引）
- Delete: `src/WebMail/Services/Background/MailSyncBackgroundService.cs`
- Delete: `src/WebMail/Services/Background/MailSyncJobQueueService.cs`
- Delete: `src/WebMail/Services/Background/MailSyncProcessor.cs`
- Delete: `src/WebMail/Services/MailSyncPlanner.cs`
- Delete: `tests/WebMail.Tests/MailSyncProcessorTests.cs`
- Delete: `tests/WebMail.Tests/MailSyncJobQueueServiceTests.cs`

**Interfaces:**
- Produces: `builder.Services.AddSingleton<IMailCacheService, MailCacheService>();` 注册。删除后 `Supplier/Mail.cshtml.cs` 旧读 `EmailMessages` 的代码会编译失败——本任务**不**改 `MailModel`，故本任务结束后 `MailModel` 暂时编译失败是预期的，由 Task 5 修复。**因此本任务单独 `dotnet build src/WebMail` 会失败**；验证改为只编译测试库的依赖项不可行，故本任务与 Task 5 合并验证：本任务步骤里只做删除 + 注册，不单独 build，Task 5 结束时统一 build + test。

> 实际上为保持每任务可编译，调整顺序：先做 Task 5（改 `MailModel` 不再读 `EmailMessages`）再做本任务。但 `MailModel` 需注入 `IMailCacheService`，依赖本任务的注册。为打破循环，**本任务先注册服务 + 删实体/DbSet/旧服务，同时把 `MailModel` 对 `EmailMessages`/`ActiveSyncWindow` 的引用在本任务内一并摘除（最小改动：让 `OnGet` 暂时返回空列表、移除 `ActiveSyncWindow` 写入）**，`OnGetPoll` 与缓存接入留 Task 5。这样每任务结束都能编译。

**修订本任务范围（含 MailModel 最小摘除）：**
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml.cs` —— 移除 `ActiveSyncWindow` 写入；`OnGet` 中读 `EmailMessages` 改为 `Messages = Array.Empty<MailMessageView>();`（临时）；`Messages` 属性类型改 `IReadOnlyList<MailMessageView>`；移除 `ActiveWindowExpiresAt` 属性及其赋值（视图在 Task 6 同步移除引用）。
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml` —— 移除第 10 行"同步窗口到期"段落（`<p><strong>@L["Supplier.Mail.WindowExpires"]</strong>@Model.ActiveWindowExpiresAt</p>`），否则编译失败。

- [ ] **Step 1: 删除旧服务文件与测试**

```bash
git rm src/WebMail/Services/Background/MailSyncBackgroundService.cs
git rm src/WebMail/Services/Background/MailSyncJobQueueService.cs
git rm src/WebMail/Services/Background/MailSyncProcessor.cs
git rm src/WebMail/Services/MailSyncPlanner.cs
git rm tests/WebMail.Tests/MailSyncProcessorTests.cs
git rm tests/WebMail.Tests/MailSyncJobQueueServiceTests.cs
```

- [ ] **Step 2: 删除实体**

在 `src/WebMail/Domain/Entities.cs` 删除 `EmailMessage`（第 42-58 行整块）、`SyncJob`、`ActiveSyncWindow`（第 76 行起整块）三个类。保留 `AllowedSender`、`AuditLog`、`Shipment` 等。

- [ ] **Step 3: 删除 DbSet 与索引**

在 `src/WebMail/Data/WebMailDbContext.cs`：
- 删 `public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();`
- 删 `public DbSet<ActiveSyncWindow> ActiveSyncWindows => Set<ActiveSyncWindow>();`
- 删 `public DbSet<SyncJob> SyncJobs => Set<SyncJob>();`
- 删 `modelBuilder.Entity<EmailMessage>().HasIndex(x => x.BuyerId);`
- 删 `modelBuilder.Entity<EmailMessage>().HasIndex(x => new { x.EmailAccountId, x.ProviderMessageId }).IsUnique();`
- 删 `modelBuilder.Entity<ActiveSyncWindow>().HasIndex(x => x.BuyerId).IsUnique();`

- [ ] **Step 4: 改 Program.cs 注册**

在 `src/WebMail/Program.cs`：
- 删第 42 行 `builder.Services.AddSingleton<MailSyncPlanner>();`
- 删第 65 行 `builder.Services.AddSingleton<MailSyncJobQueueService>();`
- 删第 66 行 `builder.Services.AddScoped<MailSyncProcessor>();`
- 删第 67 行 `builder.Services.AddHostedService<MailSyncBackgroundService>();`
- 在原第 47 行 `AddScoped<IEmailProviderResolver, EmailProviderResolver>();` 之后追加：
  ```csharp
  builder.Services.AddSingleton<IMailCacheService, MailCacheService>();
  ```
- 顶部 using 若有 `WebMail.Services.Background` 且不再被引用，移除；确保 `using WebMail.Services;` 存在（`MailCacheService` 所在命名空间）。

- [ ] **Step 5: MailModel 最小摘除**

在 `src/WebMail/Pages/Supplier/Mail.cshtml.cs`：
- 构造函数追加 `IMailCacheService` 参数（本任务暂不使用，仅为 Task 5 预埋；存为字段 `_cache`）。实际上为避免"未使用字段"警告，本任务先**不**注入，Task 5 再注入。改为：本任务只做摘除。
- 删除 `ActiveWindowExpiresAt` 属性及其在 `OnGet` 中的赋值（第 42 行属性声明、第 71-83 行 `var expiresAt = ...` 到 `ActiveWindowExpiresAt = expiresAt;` 整块）。
- `OnGet` 中读 `EmailMessages` 的块（第 60-67 行 `var account = ...; if (account is not null) { Messages = await _db.EmailMessages... }`）替换为：
  ```csharp
          Messages = Array.Empty<Domain.MailMessageView>();
  ```
- `Messages` 属性类型（第 40 行）改为 `public IReadOnlyList<Domain.MailMessageView> Messages { get; private set; } = Array.Empty<Domain.MailMessageView>();`
- 顶部 using 增补 `using WebMail.Domain;`（若已有则跳过）。

- [ ] **Step 6: Mail.cshtml 移除窗口行**

在 `src/WebMail/Pages/Supplier/Mail.cshtml` 删除第 10 行：
```
<p><strong>@L["Supplier.Mail.WindowExpires"]</strong>@Model.ActiveWindowExpiresAt</p>
```

- [ ] **Step 7: 编译 + 全量测试**

Run: `dotnet build src/WebMail`
Expected: 成功，0 错误。

Run: `dotnet test tests/WebMail.Tests`
Expected: PASS（旧 MailSync 测试已删，其余全绿；`SupplierMailModelTests` 仍应通过，因其测的是 shipment handler，未触及已删字段——若 `MailModel` 构造未变则不受影响；本任务未改构造，应通过）。

- [ ] **Step 8: 提交**

```bash
git add -A
git commit -m "refactor(mail): drop DB sync pipeline, register MailCacheService"
```

---

### Task 5: `MailModel` 接入缓存 + `OnGetPoll` 处理器 + 单测

**Files:**
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml.cs`
- Modify: `tests/WebMail.Tests/SupplierMailModelTests.cs`

**Interfaces:**
- Consumes: `IMailCacheService.GetOrFetchAsync(long buyerId, bool force, CancellationToken)` → `MailCacheResult`
- Produces: `MailModel.OnGetPoll(long buyerId, bool force)` → `IActionResult`（`JsonResult` 或 `ForbidResult`）；`MailModel` 构造签名变为 `MailModel(WebMailDbContext db, ShipmentService shipments, IMailCacheService cache, IStringLocalizer<SharedResource> loc)`。

- [ ] **Step 1: 写失败测试（OnGetPoll）**

在 `SupplierMailModelTests.cs` 的 `CreateModel` 改为注入缓存桩，并新增测试。修改 `CreateModel`：

```csharp
    private MailModel CreateModel(WebMailDbContext db, long userId, string role, IMailCacheService? cache = null)
    {
        var model = new MailModel(db, Svc(db), cache ?? new StubCache(), TestLocalizer.Shared);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "test"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return model;
    }
```

文件顶部 using 增补 `using WebMail.Services;`。

新增测试与桩：

```csharp
    [Fact]
    public async Task OnGetPollReturnsJsonMessagesForAdmin()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 50));
        await db.SaveChangesAsync();
        var cache = new StubCache(new[]
        {
            new MailMessageView("m-1", "s@x.com", "Hello",
                new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero), MailFolder.Inbox)
        });
        var model = CreateModel(db, userId: 1, role: "Administrator", cache);

        var result = await model.OnGetPoll(buyerId: 50, force: false);

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<PollPayload>(json.Value);
        Assert.Single(payload.Messages);
        Assert.False(payload.Stale);
    }

    [Fact]
    public async Task OnGetPollForbidsUnassignedSupplier()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 51));
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 9, role: "Supplier"); // not assigned

        var result = await model.OnGetPoll(buyerId: 51, force: false);

        Assert.IsType<ForbidResult>(result);
    }

    private sealed class StubCache : IMailCacheService
    {
        private readonly MailCacheResult _result;
        public StubCache(IReadOnlyList<MailMessageView> messages) =>
            _result = new MailCacheResult(messages, Stale: false, Error: null);
        public Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken ct) => Task.FromResult(_result);
    }

    public sealed class PollPayload
    {
        public IReadOnlyList<MailMessageView> Messages { get; set; } = Array.Empty<MailMessageView>();
        public bool Stale { get; set; }
        public string? Error { get; set; }
    }
```

- [ ] **Step 2: 运行测试验证失败**

Run: `dotnet test tests/WebMail.Tests --filter "FullyQualifiedName~OnGetPoll"`
Expected: FAIL — `MailModel` 构造不匹配 / `OnGetPoll` 不存在。

- [ ] **Step 3: 实现构造注入 + OnGetPoll + OnGet 接缓存**

在 `src/WebMail/Pages/Supplier/Mail.cshtml.cs`：
- 构造函数改为：
  ```csharp
  public MailModel(WebMailDbContext db, ShipmentService shipments, IMailCacheService cache, IStringLocalizer<SharedResource> loc)
  {
      _db = db;
      _shipments = shipments;
      _cache = cache;
      _loc = loc;
  }
  private readonly IMailCacheService _cache;
  ```
- `OnGet` 中把 `Messages = Array.Empty<Domain.MailMessageView>();` 替换为：
  ```csharp
          var cached = await _cache.GetOrFetchAsync(buyerId, force: false, CancellationToken.None);
          Messages = cached.Messages;
  ```
  （放在 `BuyerId = buyerId;` 之后、shipments 加载之前；保留 shipments 加载与授权检查。）
- 新增处理器：
  ```csharp
      public async Task<IActionResult> OnGetPoll(long buyerId, bool force = false)
      {
          if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
          {
              return Forbid();
          }
          if (!User.IsInRole("Administrator") && !await IsBuyerReadyAsync(buyerId))
          {
              return Forbid();
          }

          var cached = await _cache.GetOrFetchAsync(buyerId, force, CancellationToken.None);
          return new JsonResult(new
          {
              messages = cached.Messages,
              stale = cached.Stale,
              error = cached.Error
          });
      }
  ```
- 顶部 using 增补 `using WebMail.Services;`（若已存在跳过）。

- [ ] **Step 4: 运行测试验证通过**

Run: `dotnet test tests/WebMail.Tests --filter "FullyQualifiedName~OnGetPoll|FullyQualifiedName~SupplierMailModelTests"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Pages/Supplier/Mail.cshtml.cs tests/WebMail.Tests/SupplierMailModelTests.cs
git commit -m "feat(mail): wire MailModel to cache + add OnGetPoll JSON handler"
```

---

### Task 6: 前端 AJAX 轮询 + 开关 + 刷新按钮 + i18n

**Files:**
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml`
- Modify: `src/WebMail/Resources/SharedResource.en.resx`
- Modify: `src/WebMail/Resources/SharedResource.zh-CN.resx`

**Interfaces:**
- Consumes: `OnGetPoll` JSON `{ messages: [{id,sender,subject,sentAt,folder}], stale, error }`；现有 i18n key `Common.NoMail`、`Mail.Junk`、`Table.Source/Sender/Subject/SentAt`。

- [ ] **Step 1: 添加 i18n key**

在 `SharedResource.en.resx` 的 `</root>` 前追加：
```xml
  <data name="Mail.AutoPoll" xml:space="preserve"><value>Auto-refresh</value></data>
  <data name="Mail.Refresh" xml:space="preserve"><value>Refresh</value></data>
  <data name="Mail.RefreshFailed" xml:space="preserve"><value>Mail refresh failed. Showing previous results.</value></data>
```
在 `SharedResource.zh-CN.resx` 的 `</root>` 前追加：
```xml
  <data name="Mail.AutoPoll" xml:space="preserve"><value>自动刷新</value></data>
  <data name="Mail.Refresh" xml:space="preserve"><value>刷新</value></data>
  <data name="Mail.RefreshFailed" xml:space="preserve"><value>邮件刷新失败，以下为上次结果。</value></data>
```

- [ ] **Step 2: 改 Mail.cshtml 视图**

整体替换 `src/WebMail/Pages/Supplier/Mail.cshtml` 为：

```razor
@page
@using WebMail.Domain
@model WebMail.Pages.Supplier.MailModel
@{
    ViewData["Title"] = L["Supplier.Mail.Title"].Value;
}

<h1 class="display-6">@L["Supplier.Mail.Title"]</h1>

<p>
    <label class="me-3">
        <input type="checkbox" id="autoPollToggle" checked /> @L["Mail.AutoPoll"]
    </label>
    <button type="button" class="btn btn-secondary btn-sm" id="refreshBtn">@L["Mail.Refresh"]</button>
</p>

<p>
    @if (User.IsInRole("Administrator"))
    {
        <a class="btn btn-secondary btn-sm" asp-page="/Admin/Buyers">@L["Common.BackToList"]</a>
    }
    else
    {
        <a class="btn btn-secondary btn-sm" asp-page="Buyers">@L["Common.BackToList"]</a>
    }
</p>

@if (!string.IsNullOrEmpty(Model.Message))
{
    <div class="alert alert-info">@Model.Message</div>
}

<div id="mailAlert" class="alert alert-warning d-none"></div>

<div id="mailContent">
    @if (Model.Messages.Count == 0)
    {
        <p>@L["Common.NoMail"]</p>
    }
    else
    {
        <div class="table-responsive">
        <table class="table table-striped table-cards">
            <thead>
                <tr>
                    <th>@L["Table.Source"]</th>
                    <th>@L["Table.Sender"]</th>
                    <th>@L["Table.Subject"]</th>
                    <th>@L["Table.SentAt"]</th>
                </tr>
            </thead>
            <tbody id="mailBody">
                @foreach (var message in Model.Messages)
                {
                    <tr>
                        <td data-label="@L["Table.Source"]">
                            @if (message.Folder == MailFolder.Junk)
                            {
                                <span class="badge bg-warning text-dark">@L["Mail.Junk"]</span>
                            }
                        </td>
                        <td data-label="@L["Table.Sender"]">@message.Sender</td>
                        <td data-label="@L["Table.Subject"]">@message.Subject</td>
                        <td data-label="@L["Table.SentAt"]">@message.SentAt</td>
                    </tr>
                }
            </tbody>
        </table>
        </div>
    }
</div>

<partial name="_ShipmentSection" model="Model" />

@section Scripts {
<script>
    (function () {
        var buyerId = @Model.BuyerId;
        var noMailText = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(L["Common.NoMail"].Value));
        var junkText = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(L["Mail.Junk"].Value));
        var refreshFailedText = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(L["Mail.RefreshFailed"].Value));
        var pollUrl = '?handler=poll&buyerId=' + buyerId;
        var timer = null;

        function render(data) {
            var body = document.getElementById('mailBody');
            var content = document.getElementById('mailContent');
            var alert = document.getElementById('mailAlert');
            var msgs = data.messages || [];

            if (data.stale || data.error) {
                alert.textContent = refreshFailedText;
                alert.classList.remove('d-none');
            } else {
                alert.classList.add('d-none');
            }

            if (msgs.length === 0) {
                content.innerHTML = '<p>' + noMailText + '</p>';
                return;
            }

            var rows = msgs.map(function (m) {
                var source = m.folder === 2 ? '<span class="badge bg-warning text-dark">' + junkText + '</span>' : '';
                return '<tr>'
                    + '<td data-label="Source">' + source + '</td>'
                    + '<td data-label="Sender">' + escapeHtml(m.sender) + '</td>'
                    + '<td data-label="Subject">' + escapeHtml(m.subject) + '</td>'
                    + '<td data-label="SentAt">' + escapeHtml(m.sentAt) + '</td>'
                    + '</tr>';
            }).join('');

            content.innerHTML = '<div class="table-responsive"><table class="table table-striped table-cards">'
                + '<thead><tr><th>Source</th><th>Sender</th><th>Subject</th><th>SentAt</th></tr></thead>'
                + '<tbody>' + rows + '</tbody></table></div>';
        }

        function escapeHtml(s) {
            return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
                return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
            });
        }

        function fetchMail(force) {
            $.getJSON(pollUrl + (force ? '&force=1' : ''))
                .done(render)
                .fail(function () {
                    var alert = document.getElementById('mailAlert');
                    alert.textContent = refreshFailedText;
                    alert.classList.remove('d-none');
                });
        }

        function startTimer() { stopTimer(); timer = setInterval(function () { fetchMail(false); }, 15000); }
        function stopTimer() { if (timer) { clearInterval(timer); timer = null; } }

        document.getElementById('refreshBtn').addEventListener('click', function () { fetchMail(true); });
        document.getElementById('autoPollToggle').addEventListener('change', function () {
            if (this.checked) { startTimer(); } else { stopTimer(); }
        });
        window.addEventListener('beforeunload', stopTimer);

        if (document.getElementById('autoPollToggle').checked) { startTimer(); }
    })();
</script>
}
```

> `MailFolder.Junk = 2`（`Enums.cs:9`），前端判 `m.folder === 2`。

- [ ] **Step 3: 编译**

Run: `dotnet build src/WebMail`
Expected: 成功。

- [ ] **Step 4: 全量测试**

Run: `dotnet test tests/WebMail.Tests`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Pages/Supplier/Mail.cshtml src/WebMail/Resources/SharedResource.en.resx src/WebMail/Resources/SharedResource.zh-CN.resx
git commit -m "feat(mail): AJAX polling with toggle + refresh button"
```

---

### Task 7: appsettings 配置清理

**Files:**
- Modify: `src/WebMail/appsettings.json`

- [ ] **Step 1: 更新 MailSync 节**

把 `src/WebMail/appsettings.json` 的 `MailSync` 节改为：

```json
  "MailSync": {
    "InitialSyncDays": 30,
    "CacheTtlSeconds": 30
  },
```

（删除 `ActiveWindowMinutes`、`ActiveWindowIntervalMinutes`、`GlobalIntervalMinutes`。）

- [ ] **Step 2: 编译 + 测试**

Run: `dotnet build src/WebMail && dotnet test tests/WebMail.Tests`
Expected: 成功 + PASS。

- [ ] **Step 3: 提交**

```bash
git add src/WebMail/appsettings.json
git commit -m "chore(mail): tidy MailSync config for cache TTL"
```

---

## Self-Review

**1. Spec coverage:**
- 内存缓存最新 10 条 → Task 2 `ProjectLatest(limit:10)`、Task 3 缓存。✓
- 去掉后台定时同步 → Task 4 删 `MailSyncBackgroundService` 等。✓
- AJAX 轮询 + 开关 + 刷新按钮 → Task 6。✓
- 拉取失败返回旧数据 + 界面提示 → Task 3 `Degraded` + Task 6 `mailAlert`。✓
- `ProviderAuthorizationException` 置 `Abnormal` + 审计 → Task 3 `MarkBuyerAbnormalAsync`。✓
- 删除 `EmailMessage`/`SyncJob`/`ActiveSyncWindow` 实体与表 → Task 4。✓
- 配置 `CacheTtlSeconds` → Task 3 读 + Task 7 写。✓

**2. Placeholder scan:** 无 TBD/TODO；每步含完整代码或精确删除指令。✓

**3. Type consistency:**
- `MailMessageView(Id, Sender, Subject, SentAt, Folder)` — Task 1 定义，Task 2/3/5/6 一致使用。✓
- `MailCacheResult(Messages, Stale, Error)` — Task 2 定义，Task 3/5 一致。✓
- `IMailCacheService.GetOrFetchAsync(long, bool, CancellationToken)` — Task 3 定义，Task 5/6 一致。✓
- `MailModel` 构造：Task 5 改为 `(db, shipments, cache, loc)`，`SupplierMailModelTests.CreateModel` 同步更新。✓
- `OnGetPoll(long buyerId, bool force)` — Task 5 定义，Task 6 前端 `pollUrl` 一致。✓
- `AccountNotFoundException` — Task 3 内部抛出与捕获一致。✓
- `MailFolder.Junk = 2` — Task 6 前端硬编码 `=== 2` 与 `Enums.cs:9` 一致。✓

**注：** Task 4 的"编译失败循环"已在任务内通过"最小摘除 MailModel + 视图移除窗口行"化解，每任务结束均可编译。
