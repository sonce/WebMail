# WebMail MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the first working WebMail MVP: card-based buyer entry, Gmail authorization state management, role-scoped back office, allowed-sender mail snapshots, and background synchronization orchestration.

**Architecture:** Implement a single ASP.NET Core web application with Razor Pages, EF Core persistence, cookie-based back-office authentication, and hosted background services. Keep domain rules in services so page handlers and future APIs share the same authorization/status logic.

**Tech Stack:** .NET 8, ASP.NET Core Razor Pages, EF Core, SQLite for local development, xUnit, BackgroundService, Google OAuth/Gmail integration behind an `IEmailProvider` abstraction.

---

## File Structure

```text
src/WebMail/WebMail.csproj
src/WebMail/Program.cs
src/WebMail/appsettings.json
src/WebMail/Data/WebMailDbContext.cs
src/WebMail/Domain/Enums.cs
src/WebMail/Domain/Entities.cs
src/WebMail/Services/BuyerRuleService.cs
src/WebMail/Services/CardGenerationService.cs
src/WebMail/Services/MailSyncPlanner.cs
src/WebMail/Services/EmailProviders/IEmailProvider.cs
src/WebMail/Services/EmailProviders/GmailProvider.cs
src/WebMail/Services/Background/MailSyncBackgroundService.cs
src/WebMail/Pages/Buyer/Verify.cshtml(.cs)
src/WebMail/Pages/Buyer/Email.cshtml(.cs)
src/WebMail/Pages/Admin/Buyers.cshtml(.cs)
src/WebMail/Pages/Sales/Buyers.cshtml(.cs)
src/WebMail/Pages/Supplier/Buyers.cshtml(.cs)
src/WebMail/Pages/Supplier/Mail.cshtml(.cs)
tests/WebMail.Tests/BuyerRuleServiceTests.cs
tests/WebMail.Tests/CardGenerationServiceTests.cs
tests/WebMail.Tests/MailSyncPlannerTests.cs
WebMail.sln
```

Responsibilities:

- `Domain/*`: stable statuses and persistence entities only.
- `BuyerRuleService`: buyer/sales/supplier status and permission decisions.
- `CardGenerationService`: random card generation and uniqueness helper.
- `MailSyncPlanner`: allowed-sender query and message filtering decisions.
- `IEmailProvider`: provider boundary for Gmail now and Outlook later.
- Razor Page models: thin request handlers that call services and enforce current-user scope.

## Task 1: Scaffold Solution And Projects

**Files:**
- Create: `WebMail.sln`
- Create: `src/WebMail/WebMail.csproj`
- Create: `tests/WebMail.Tests/WebMail.Tests.csproj`

- [ ] **Step 1: Create projects**

Run:

```powershell
dotnet new sln -n WebMail
dotnet new webapp -n WebMail -o src/WebMail --framework net8.0
dotnet new xunit -n WebMail.Tests -o tests/WebMail.Tests --framework net8.0
dotnet sln WebMail.sln add src/WebMail/WebMail.csproj
dotnet sln WebMail.sln add tests/WebMail.Tests/WebMail.Tests.csproj
dotnet add tests/WebMail.Tests/WebMail.Tests.csproj reference src/WebMail/WebMail.csproj
```

- [ ] **Step 2: Add packages**

```powershell
dotnet add src/WebMail/WebMail.csproj package Microsoft.EntityFrameworkCore.Sqlite
dotnet add src/WebMail/WebMail.csproj package Microsoft.EntityFrameworkCore.Design
dotnet add src/WebMail/WebMail.csproj package Google.Apis.Gmail.v1
dotnet add tests/WebMail.Tests/WebMail.Tests.csproj package Microsoft.EntityFrameworkCore.InMemory
```

- [ ] **Step 3: Configure `src/WebMail/appsettings.json`**

```json
{
  "ConnectionStrings": { "Default": "Data Source=webmail.dev.db" },
  "MailSync": {
    "InitialSyncDays": 30,
    "ActiveWindowMinutes": 30,
    "ActiveWindowIntervalMinutes": 3,
    "GlobalIntervalMinutes": 60
  },
  "GoogleOAuth": { "ClientId": "", "ClientSecret": "", "RedirectUri": "https://localhost:5001/oauth/gmail/callback" },
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
```

- [ ] **Step 4: Verify**

Run `dotnet build WebMail.sln`. Expected: build succeeds.

- [ ] **Step 5: Commit**

Run `git add WebMail.sln src/WebMail tests/WebMail.Tests` and `git commit -m "chore: scaffold webmail solution"`. If git reports `fatal: not a git repository`, skip and record it.

## Task 2: Domain Model And DbContext

**Files:**
- Create: `src/WebMail/Domain/Enums.cs`
- Create: `src/WebMail/Domain/Entities.cs`
- Create: `src/WebMail/Data/WebMailDbContext.cs`
- Modify: `src/WebMail/Program.cs`

- [ ] **Step 1: Create `Enums.cs`**

```csharp
namespace WebMail.Domain;

public enum UserRole { Administrator = 1, Sales = 2, Supplier = 3 }
public enum CardStatus { Unused = 1, Entered = 2, Authorized = 3, DeletedOrDisabled = 4 }
public enum EmailAuthorizationStatus { NotAuthorized = 1, PendingReview = 2, Normal = 3, Rejected = 4, Abnormal = 5 }
public enum SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }
public enum SyncJobStatus { Pending = 1, Running = 2, Succeeded = 3, Failed = 4 }
```

- [ ] **Step 2: Create `Entities.cs`**

Create entities: `AppUser`, `Buyer`, `EmailAccount`, `EmailMessage`, `AllowedSender`, `BuyerSupplierAssignment`, `ActiveSyncWindow`, `SyncJob`, `AuditLog`. Required fields:

```csharp
public sealed class Buyer
{
    public long Id { get; set; }
    public string CardNo { get; set; } = string.Empty;
    public CardStatus CardStatus { get; set; } = CardStatus.Unused;
    public long? SaleId { get; set; }
    public EmailAuthorizationStatus EmailStatus { get; set; } = EmailAuthorizationStatus.NotAuthorized;
    public SupplierProcessingStatus SupplierStatus { get; set; } = SupplierProcessingStatus.Unprocessed;
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

Also include `EmailAccount.BuyerId`, `EmailAccount.Email`, encrypted token fields, `EmailMessage.ProviderMessageId`, sender/subject/body snapshot fields, and timestamps.

- [ ] **Step 3: Create `WebMailDbContext.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using WebMail.Domain;

namespace WebMail.Data;

public sealed class WebMailDbContext(DbContextOptions<WebMailDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Buyer> Buyers => Set<Buyer>();
    public DbSet<EmailAccount> EmailAccounts => Set<EmailAccount>();
    public DbSet<EmailMessage> EmailMessages => Set<EmailMessage>();
    public DbSet<AllowedSender> AllowedSenders => Set<AllowedSender>();
    public DbSet<BuyerSupplierAssignment> BuyerSupplierAssignments => Set<BuyerSupplierAssignment>();
    public DbSet<ActiveSyncWindow> ActiveSyncWindows => Set<ActiveSyncWindow>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>().HasIndex(x => x.UserName).IsUnique();
        modelBuilder.Entity<Buyer>().HasIndex(x => x.CardNo).IsUnique();
        modelBuilder.Entity<EmailAccount>().HasIndex(x => x.BuyerId).IsUnique();
        modelBuilder.Entity<EmailMessage>().HasIndex(x => new { x.EmailAccountId, x.ProviderMessageId }).IsUnique();
        modelBuilder.Entity<AllowedSender>().HasIndex(x => x.EmailAddress).IsUnique();
        modelBuilder.Entity<BuyerSupplierAssignment>().HasIndex(x => x.BuyerId).IsUnique();
    }
}
```

- [ ] **Step 4: Register DbContext**

In `Program.cs`, add `builder.Services.AddDbContext<WebMailDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));` with the required usings.

- [ ] **Step 5: Verify and commit**

Run `dotnet build WebMail.sln`. Commit `feat: add webmail domain model` if git is available.

## Task 3: Buyer Rule Service

**Files:**
- Create: `src/WebMail/Services/BuyerRuleService.cs`
- Test: `tests/WebMail.Tests/BuyerRuleServiceTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class BuyerRuleServiceTests
{
    private readonly BuyerRuleService _service = new();

    [Theory]
    [InlineData(EmailAuthorizationStatus.NotAuthorized, true)]
    [InlineData(EmailAuthorizationStatus.PendingReview, true)]
    [InlineData(EmailAuthorizationStatus.Rejected, true)]
    [InlineData(EmailAuthorizationStatus.Normal, false)]
    [InlineData(EmailAuthorizationStatus.Abnormal, false)]
    public void BuyerCanUnlinkOnlyBeforeProcessing(EmailAuthorizationStatus status, bool expected) => Assert.Equal(expected, _service.CanBuyerUnlink(status));

    [Fact]
    public void SalesCannotDeleteOtherSalesBuyer()
    {
        var buyer = new Buyer { SaleId = 10, EmailStatus = EmailAuthorizationStatus.PendingReview };
        Assert.False(_service.CanSalesDeleteBuyer(buyer, 99));
    }

    [Fact]
    public void SupplierCanSeeOnlyAssignedNormalBuyer()
    {
        var buyer = new Buyer { EmailStatus = EmailAuthorizationStatus.Normal };
        Assert.True(_service.CanSupplierViewBuyer(buyer, 7, 7));
    }
}
```

- [ ] **Step 2: Run failing test**

Run `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter BuyerRuleServiceTests`. Expected: compile fails before implementation.

- [ ] **Step 3: Implement service**

```csharp
using WebMail.Domain;

namespace WebMail.Services;

public sealed class BuyerRuleService
{
    private static readonly HashSet<EmailAuthorizationStatus> BuyerUnlinkAllowed =
    [EmailAuthorizationStatus.NotAuthorized, EmailAuthorizationStatus.PendingReview, EmailAuthorizationStatus.Rejected];

    public bool CanBuyerUnlink(EmailAuthorizationStatus status) => BuyerUnlinkAllowed.Contains(status);
    public string BuyerUnlinkBlockedMessage => "正在处理中，无法删除";

    public bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId) =>
        !buyer.IsDeleted && buyer.SaleId == salesUserId && BuyerUnlinkAllowed.Contains(buyer.EmailStatus);

    public bool CanSupplierViewBuyer(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        !buyer.IsDeleted && buyer.EmailStatus == EmailAuthorizationStatus.Normal && assignedSupplierId == currentSupplierId;
}
```

- [ ] **Step 4: Verify and commit**

Run the same filtered test. Commit `feat: add buyer status rules` if git is available.

## Task 4: Card Generation Service

**Files:**
- Create: `src/WebMail/Services/CardGenerationService.cs`
- Test: `tests/WebMail.Tests/CardGenerationServiceTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class CardGenerationServiceTests
{
    [Fact] public void GenerateCardNoUsesConfiguredLength() => Assert.Equal(32, new CardGenerationService().GenerateCardNo(32).Length);
    [Fact] public void GenerateCardNoUsesUrlSafeCharacters() => Assert.Matches("^[A-Za-z0-9_-]+$", new CardGenerationService().GenerateCardNo(64));
    [Fact] public void GenerateCardNoRejectsTooShortLength() => Assert.Throws<ArgumentOutOfRangeException>(() => new CardGenerationService().GenerateCardNo(8));
}
```

- [ ] **Step 2: Run failing test**

Run `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter CardGenerationServiceTests`. Expected: compile fails before implementation.

- [ ] **Step 3: Implement service**

```csharp
using System.Security.Cryptography;

namespace WebMail.Services;

public sealed class CardGenerationService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";

    public string GenerateCardNo(int length = 32)
    {
        if (length < 24) throw new ArgumentOutOfRangeException(nameof(length), "Card number length must be at least 24 characters.");
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        return new string(bytes.ToArray().Select(b => Alphabet[b % Alphabet.Length]).ToArray());
    }
}
```

- [ ] **Step 4: Verify and commit**

Run the filtered test. Commit `feat: add secure card generation` if git is available.

## Task 5: Mail Sync Planner

**Files:**
- Create: `src/WebMail/Services/MailSyncPlanner.cs`
- Test: `tests/WebMail.Tests/MailSyncPlannerTests.cs`

- [ ] **Step 1: Write tests**

```csharp
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class MailSyncPlannerTests
{
    [Fact] public void MatchesAllowedSenderCaseInsensitive() => Assert.True(new MailSyncPlanner().IsAllowedSender("Buyer <Orders@Example.com>", ["orders@example.com"]));
    [Fact] public void RejectsUnconfiguredSender() => Assert.False(new MailSyncPlanner().IsAllowedSender("spam@example.com", ["orders@example.com"]));
    [Fact] public void BuildsGmailQueryForAllowedSenders() => Assert.Equal("from:a@example.com OR from:b@example.com", new MailSyncPlanner().BuildGmailSenderQuery(["a@example.com", "b@example.com"]));
    [Fact] public void EmptyAllowedSenderListDisablesSyncQuery() => Assert.Equal(string.Empty, new MailSyncPlanner().BuildGmailSenderQuery([]));
}
```

- [ ] **Step 2: Run failing test**

Run `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter MailSyncPlannerTests`. Expected: compile fails before implementation.

- [ ] **Step 3: Implement planner**

```csharp
using System.Net.Mail;

namespace WebMail.Services;

public sealed class MailSyncPlanner
{
    public bool IsAllowedSender(string rawSender, IReadOnlyCollection<string> allowedSenders)
    {
        var sender = ExtractAddress(rawSender);
        return allowedSenders.Any(x => string.Equals(x.Trim(), sender, StringComparison.OrdinalIgnoreCase));
    }

    public string BuildGmailSenderQuery(IReadOnlyCollection<string> allowedSenders)
    {
        var senders = allowedSenders.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return senders.Length == 0 ? string.Empty : string.Join(" OR ", senders.Select(x => $"from:{x}"));
    }

    private static string ExtractAddress(string rawSender)
    {
        try { return new MailAddress(rawSender).Address; }
        catch (FormatException) { return rawSender.Trim(); }
    }
}
```

- [ ] **Step 4: Verify and commit**

Run the filtered test. Commit `feat: add allowed sender sync planner` if git is available.

## Task 6: Services And Auth Skeleton

**Files:**
- Modify: `src/WebMail/Program.cs`

- [ ] **Step 1: Register services and cookie auth**

Add usings: `Microsoft.AspNetCore.Authentication.Cookies`, `WebMail.Services`.

Add before `var app = builder.Build();`:

```csharp
builder.Services.AddScoped<BuyerRuleService>();
builder.Services.AddSingleton<CardGenerationService>();
builder.Services.AddSingleton<MailSyncPlanner>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.LoginPath = "/Login"; options.AccessDeniedPath = "/AccessDenied"; });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Administrator"));
    options.AddPolicy("SalesOnly", policy => policy.RequireRole("Sales"));
    options.AddPolicy("SupplierOnly", policy => policy.RequireRole("Supplier"));
});
```

Add after `app.UseRouting();`:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

- [ ] **Step 2: Verify and commit**

Run `dotnet build WebMail.sln`. Commit `feat: register services and auth policies` if git is available.

## Task 7: Buyer Entry Pages

**Files:**
- Create: `src/WebMail/Pages/Buyer/Verify.cshtml`
- Create: `src/WebMail/Pages/Buyer/Verify.cshtml.cs`
- Create: `src/WebMail/Pages/Buyer/Email.cshtml`
- Create: `src/WebMail/Pages/Buyer/Email.cshtml.cs`

- [ ] **Step 1: Implement verification handler**

`OnGetAsync(string card, long? saleid)` must:

1. Reject blank card with `链接无效`.
2. Load `Buyer` by `CardNo` and `!IsDeleted`.
3. Reject missing or disabled card with `链接无效或已失效`.
4. Store `saleid` only if `Buyer.SaleId` is currently null.
5. Change `CardStatus.Unused` to `Entered`.
6. Redirect to `/Buyer/Email?card=...`.

- [ ] **Step 2: Implement buyer email handler**

`OnPostUnlinkAsync(string card)` must:

1. Load buyer and email account.
2. Call `BuyerRuleService.CanBuyerUnlink`.
3. If blocked, show `BuyerRuleService.BuyerUnlinkBlockedMessage`.
4. If allowed, remove `EmailAccount`, set `EmailStatus = NotAuthorized`, keep historical `EmailMessage` rows, save changes.

- [ ] **Step 3: Verify and commit**

Run `dotnet build WebMail.sln`. Commit `feat: add buyer entry pages` if git is available.

## Task 8: Role-Scoped Backend Pages

**Files:**
- Create: `src/WebMail/Pages/Admin/Buyers.cshtml(.cs)`
- Create: `src/WebMail/Pages/Sales/Buyers.cshtml(.cs)`
- Create: `src/WebMail/Pages/Supplier/Buyers.cshtml(.cs)`
- Create: `src/WebMail/Pages/Supplier/Mail.cshtml(.cs)`

- [ ] **Step 1: Admin buyer list**

Create an `[Authorize(Policy = "AdminOnly")]` page that lists all non-deleted buyers with card, email status, supplier status, and created time.

- [ ] **Step 2: Sales buyer list and delete**

Create an `[Authorize(Policy = "SalesOnly")]` page. Query only `Buyer.SaleId == currentUserId`. Delete handler must use `BuyerRuleService.CanSalesDeleteBuyer`; allowed delete is soft delete only.

- [ ] **Step 3: Supplier buyer list**

Create an `[Authorize(Policy = "SupplierOnly")]` page. Use exactly this filter:

```csharp
db.BuyerSupplierAssignments
    .Include(x => x.Buyer)
    .Where(x => x.SupplierId == supplierId && !x.Buyer.IsDeleted && x.Buyer.EmailStatus == EmailAuthorizationStatus.Normal)
    .Select(x => x.Buyer)
```

- [ ] **Step 4: Supplier mail page active window**

`OnGetAsync(long buyerId)` must verify assignment and `Normal` email status, list stored `EmailMessages`, and upsert `ActiveSyncWindow.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)`.

- [ ] **Step 5: Verify and commit**

Run `dotnet build WebMail.sln`. Commit `feat: add role scoped backend pages` if git is available.

## Task 9: Email Provider Abstraction And Gmail Stub

**Files:**
- Create: `src/WebMail/Services/EmailProviders/IEmailProvider.cs`
- Create: `src/WebMail/Services/EmailProviders/GmailProvider.cs`
- Modify: `src/WebMail/Program.cs`

- [ ] **Step 1: Create provider contract**

```csharp
namespace WebMail.Services.EmailProviders;

public sealed record OAuthStartResult(string RedirectUrl, string State);
public sealed record OAuthCallbackResult(string Email, string ProviderUserId, string RefreshToken);
public sealed record ProviderMessage(string ProviderMessageId, string? ProviderThreadId, string Sender, string Recipients, string Subject, DateTimeOffset SentAt, string? TextBody, string? HtmlBody, string? AttachmentMetadataJson);

public interface IEmailProvider
{
    string Name { get; }
    OAuthStartResult BuildAuthorizationUrl(string cardNo);
    Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string encryptedRefreshToken, string senderQuery, DateTimeOffset? since, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Create Gmail provider stub**

```csharp
namespace WebMail.Services.EmailProviders;

public sealed class GmailProvider(IConfiguration configuration) : IEmailProvider
{
    public string Name => "Gmail";

    public OAuthStartResult BuildAuthorizationUrl(string cardNo)
    {
        var clientId = configuration["GoogleOAuth:ClientId"] ?? string.Empty;
        var redirectUri = Uri.EscapeDataString(configuration["GoogleOAuth:RedirectUri"] ?? string.Empty);
        var state = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(cardNo));
        var scope = Uri.EscapeDataString("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/userinfo.email");
        var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={redirectUri}&response_type=code&scope={scope}&access_type=offline&prompt=consent&state={Uri.EscapeDataString(state)}";
        return new OAuthStartResult(url, state);
    }

    public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException("Configure Google OAuth credentials before enabling token exchange.");
    public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string encryptedRefreshToken, string senderQuery, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException("Token encryption and Gmail fetch are implemented after the skeleton is verified.");
}
```

- [ ] **Step 3: Register provider**

In `Program.cs`, add `builder.Services.AddScoped<IEmailProvider, GmailProvider>();` and the `WebMail.Services.EmailProviders` using.

- [ ] **Step 4: Verify and commit**

Run `dotnet build WebMail.sln`. Commit `feat: add email provider abstraction` if git is available.

## Task 10: Background Sync Skeleton

**Files:**
- Create: `src/WebMail/Services/Background/MailSyncBackgroundService.cs`
- Modify: `src/WebMail/Program.cs`

- [ ] **Step 1: Create background service**

```csharp
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services.Background;

public sealed class MailSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MailSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await TickAsync(stoppingToken);
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
        var now = DateTimeOffset.UtcNow;
        var activeBuyerIds = await db.ActiveSyncWindows.Where(x => x.ExpiresAt > now).Select(x => x.BuyerId).ToListAsync(cancellationToken);
        foreach (var buyerId in activeBuyerIds) db.SyncJobs.Add(new SyncJob { BuyerId = buyerId, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Queued {Count} active buyer sync jobs", activeBuyerIds.Count);
    }
}
```

- [ ] **Step 2: Register hosted service**

In `Program.cs`, add `builder.Services.AddHostedService<MailSyncBackgroundService>();` and the `WebMail.Services.Background` using.

- [ ] **Step 3: Verify and commit**

Run `dotnet build WebMail.sln`. Commit `feat: add active mail sync background skeleton` if git is available.

## Task 11: Full Verification

**Files:**
- Modify only files needed to fix compile or test failures.

- [ ] **Step 1: Full tests**

Run:

```powershell
dotnet test WebMail.sln
```

Expected: all tests pass.

- [ ] **Step 2: Full build**

Run:

```powershell
dotnet build WebMail.sln
```

Expected: build succeeds.

- [ ] **Step 3: Local smoke run**

Run:

```powershell
dotnet run --project src/WebMail/WebMail.csproj
```

Expected: app starts and prints a local URL. Open it and verify the Razor home page loads.

- [ ] **Step 4: Git status**

Run `git status --short`. Expected: only intentional implementation files are changed. If git is unavailable, record the exact error.

## Self-Review

Spec coverage:

- Card link entry is covered by Tasks 2, 4, and 7.
- `saleid` ownership is covered by Task 7 and stored in `Buyer.SaleId`.
- Buyer one-email rule is covered by Task 2 with a unique `EmailAccount.BuyerId` index.
- Buyer unlinking restrictions are covered by Task 3 and Task 7.
- Sales scoped visibility and deletion are covered by Task 3 and Task 8.
- Supplier scoped visibility and active sync window are covered by Task 3, Task 8, and Task 10.
- Allowed sender filtering is covered by Task 5.
- Provider abstraction for Gmail and later Outlook is covered by Task 9.
- Background synchronization orchestration is covered by Task 10.

Deferred after this MVP skeleton:

- Real Google token exchange.
- Token encryption implementation.
- Real Gmail message fetch and MIME body parsing.
- Admin batch link UI polish and production-grade login UI.
- Production database choice and migrations.
