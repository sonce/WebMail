# Buyer / Supplier Status-Driven Permissions — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the overloaded mailbox/review status into three independent status fields (mailbox health, buyer review, supplier processing), then drive buyer actions, sales deletion, supplier status-setting, admin review, and abnormal-token detection/recovery from them.

**Architecture:** Three stored enums on `Buyer` (`EmailStatus`, `BuyerStatus`, `SupplierStatus`), each owned by one actor. "What a buyer/sales/supplier may do" is **computed** in `BuyerRuleService`, not stored. Razor Pages render and gate on those computed decisions. The mail-sync pipeline classifies authorization failures into a dedicated exception so the processor can flip `EmailStatus → Abnormal`.

**Tech Stack:** .NET (ASP.NET Core Razor Pages), EF Core (InMemory provider in tests), xUnit. Chinese UI copy. Spec: `docs/superpowers/specs/2026-06-27-buyer-supplier-status-permissions-design.md`.

## Global Constraints

- Enum definitions are exact and stable:
  - `EmailAuthorizationStatus { NotAuthorized = 1, Authorized = 2, Abnormal = 3 }`
  - `BuyerStatus { NotSubmitted = 1, PendingReview = 2, Approved = 3, Rejected = 4 }`
  - `SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }` (unchanged)
- Deletes are soft deletes (`IsDeleted = true`); never hard-delete a `Buyer`.
- Buyer mailbox changes preserve `EmailMessage` rows (audit).
- UI copy is Chinese, matching existing pages.
- No live DB / migrations are configured; enum value changes and the new column have no historical-data impact. EF auto-maps the new property — no `OnModelCreating` change is required.
- Tests use `UseInMemoryDatabase(Guid.NewGuid().ToString("N"))`, matching existing helpers.
- Verify each task with `dotnet build` and `dotnet test` from the repo root `E:\Work\Wys\WebMail`.

---

## File Map

- `src/WebMail/Domain/Enums.cs` — split `EmailAuthorizationStatus`; add `BuyerStatus`. (Task 1)
- `src/WebMail/Domain/Entities.cs` — add `Buyer.BuyerStatus`. (Task 1)
- `src/WebMail/Services/BuyerRuleService.cs` — translate to new fields (Task 1), then computed model + new rules (Task 2).
- `src/WebMail/Pages/Buyer/Email.cshtml(.cs)` — compile-fix (Task 1); full action UI + handlers (Task 3).
- `src/WebMail/Pages/OAuth/Callback.cshtml.cs` — compile-fix (Task 1); recovery branches (Task 4).
- `src/WebMail/Pages/Supplier/Buyers.cshtml(.cs)` — view filter fix (Task 1); set-status action + columns (Task 5).
- `src/WebMail/Pages/Supplier/Mail.cshtml.cs` — view filter fix (Task 1).
- `src/WebMail/Pages/Sales/Buyers.cshtml` — status column (Task 6).
- `src/WebMail/Pages/Admin/Buyers.cshtml(.cs)` — approve/reject + columns (Task 6).
- `src/WebMail/Services/EmailProviders/*` — `ProviderAuthorizationException` + classification (Task 7).
- `src/WebMail/Services/Background/MailSyncProcessor.cs` — abnormal catch (Task 7).
- Tests under `tests/WebMail.Tests/`.

---

### Task 1: Split status enums and translate all references (pure refactor, behavior preserved)

**Files:**
- Modify: `src/WebMail/Domain/Enums.cs:5`
- Modify: `src/WebMail/Domain/Entities.cs:13-23`
- Modify: `src/WebMail/Services/BuyerRuleService.cs`
- Modify: `src/WebMail/Pages/Buyer/Email.cshtml.cs:59,66`
- Modify: `src/WebMail/Pages/OAuth/Callback.cshtml.cs:49,81`
- Modify: `src/WebMail/Pages/Supplier/Buyers.cshtml.cs:32`
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml.cs:36`
- Test: `tests/WebMail.Tests/BuyerRuleServiceTests.cs`, `tests/WebMail.Tests/BuyerPageModelTests.cs`

**Interfaces:**
- Produces: enums `EmailAuthorizationStatus { NotAuthorized=1, Authorized=2, Abnormal=3 }`, `BuyerStatus { NotSubmitted=1, PendingReview=2, Approved=3, Rejected=4 }`; `Buyer.BuyerStatus` property; `BuyerRuleService.CanBuyerUnlink(Buyer)`, `CanSalesDeleteBuyer(Buyer,long)`, `CanSupplierViewBuyer(Buyer,long?,long)`.

This task changes types only; no new behavior. The old combined value `Normal` maps to `(EmailStatus=Authorized, BuyerStatus=Approved)`; `PendingReview`→`(Authorized, PendingReview)`; `Rejected`→`(Authorized, Rejected)`; `NotAuthorized`→`(NotAuthorized, NotSubmitted)`.

- [ ] **Step 1: Update the enums**

In `src/WebMail/Domain/Enums.cs` replace line 5:

```csharp
public enum EmailAuthorizationStatus { NotAuthorized = 1, Authorized = 2, Abnormal = 3 }
public enum BuyerStatus { NotSubmitted = 1, PendingReview = 2, Approved = 3, Rejected = 4 }
```

(Leave `SupplierProcessingStatus` and the other enums unchanged.)

- [ ] **Step 2: Add the `BuyerStatus` property to `Buyer`**

In `src/WebMail/Domain/Entities.cs`, inside `Buyer` (after the `EmailStatus` line 19):

```csharp
    public BuyerStatus BuyerStatus { get; set; } = BuyerStatus.NotSubmitted;
```

- [ ] **Step 3: Rewrite `BuyerRuleService` against the new fields (behavior-preserving)**

Replace the whole body of `src/WebMail/Services/BuyerRuleService.cs`:

```csharp
using WebMail.Domain;

namespace WebMail.Services;

public sealed class BuyerRuleService
{
    private static readonly HashSet<BuyerStatus> PreApprovalStatuses =
        [BuyerStatus.NotSubmitted, BuyerStatus.PendingReview, BuyerStatus.Rejected];

    public bool CanBuyerUnlink(Buyer buyer) =>
        buyer.EmailStatus != EmailAuthorizationStatus.Abnormal
        && PreApprovalStatuses.Contains(buyer.BuyerStatus);

    public string BuyerUnlinkBlockedMessage => "正在处理中，无法删除";

    public bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId) =>
        !buyer.IsDeleted
        && buyer.SaleId == salesUserId
        && buyer.EmailStatus != EmailAuthorizationStatus.Abnormal
        && PreApprovalStatuses.Contains(buyer.BuyerStatus);

    public bool CanSupplierViewBuyer(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        !buyer.IsDeleted
        && buyer.BuyerStatus == BuyerStatus.Approved
        && buyer.EmailStatus == EmailAuthorizationStatus.Authorized
        && assignedSupplierId == currentSupplierId;
}
```

- [ ] **Step 4: Fix the buyer Email page call sites**

In `src/WebMail/Pages/Buyer/Email.cshtml.cs`:
- Line 59: change `if (!_ruleService.CanBuyerUnlink(buyer.EmailStatus))` to `if (!_ruleService.CanBuyerUnlink(buyer))`.
- Lines 66: after `buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;` add `buyer.BuyerStatus = BuyerStatus.NotSubmitted;`.

- [ ] **Step 5: Fix the OAuth callback call sites**

In `src/WebMail/Pages/OAuth/Callback.cshtml.cs`:
- Line 49: change `&& !ruleService.CanBuyerUnlink(buyer.EmailStatus))` to `&& !ruleService.CanBuyerUnlink(buyer))`.
- Replace the block at lines 78-82:

```csharp
        buyer.CardStatus = CardStatus.Authorized;
        if (isNewOrChangedAccount)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
            buyer.BuyerStatus = BuyerStatus.PendingReview;
        }
```

- [ ] **Step 6: Fix the supplier query filters**

In `src/WebMail/Pages/Supplier/Buyers.cshtml.cs:32`, replace `x.Buyer.EmailStatus == EmailAuthorizationStatus.Normal` with:

```csharp
                x.Buyer.BuyerStatus == BuyerStatus.Approved && x.Buyer.EmailStatus == EmailAuthorizationStatus.Authorized
```

In `src/WebMail/Pages/Supplier/Mail.cshtml.cs:36`, replace `&& x.Buyer.EmailStatus == EmailAuthorizationStatus.Normal);` with:

```csharp
                && x.Buyer.BuyerStatus == BuyerStatus.Approved
                && x.Buyer.EmailStatus == EmailAuthorizationStatus.Authorized);
```

- [ ] **Step 7: Update the existing tests to the new types**

Replace `tests/WebMail.Tests/BuyerRuleServiceTests.cs` body:

```csharp
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class BuyerRuleServiceTests
{
    private readonly BuyerRuleService _service = new();

    [Theory]
    [InlineData(BuyerStatus.NotSubmitted, EmailAuthorizationStatus.NotAuthorized, true)]
    [InlineData(BuyerStatus.PendingReview, EmailAuthorizationStatus.Authorized, true)]
    [InlineData(BuyerStatus.Rejected, EmailAuthorizationStatus.Authorized, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, false)]
    [InlineData(BuyerStatus.PendingReview, EmailAuthorizationStatus.Abnormal, false)]
    public void BuyerCanUnlinkOnlyBeforeApproval(BuyerStatus buyerStatus, EmailAuthorizationStatus emailStatus, bool expected)
    {
        var buyer = new Buyer { BuyerStatus = buyerStatus, EmailStatus = emailStatus };
        Assert.Equal(expected, _service.CanBuyerUnlink(buyer));
    }

    [Fact]
    public void SalesCannotDeleteOtherSalesBuyer()
    {
        var buyer = new Buyer { SaleId = 10, BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.False(_service.CanSalesDeleteBuyer(buyer, 99));
    }

    [Fact]
    public void SupplierCanSeeOnlyAssignedApprovedAuthorizedBuyer()
    {
        var buyer = new Buyer { BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.True(_service.CanSupplierViewBuyer(buyer, 7, 7));
    }
}
```

In `tests/WebMail.Tests/BuyerPageModelTests.cs`:
- Line 30: change `new Buyer { CardNo = "card-2", EmailStatus = EmailAuthorizationStatus.Normal }` to `new Buyer { CardNo = "card-2", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized }`.
- Line 50: change `new Buyer { CardNo = "card-3", EmailStatus = EmailAuthorizationStatus.PendingReview }` to `new Buyer { CardNo = "card-3", BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized }`.

- [ ] **Step 8: Build and run the full suite**

Run: `dotnet build`
Expected: build succeeds, no references to `EmailAuthorizationStatus.Normal/PendingReview/Rejected` remain.
Run: `dotnet test`
Expected: PASS (all existing tests, updated).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "refactor: split email status into mailbox health + buyer review status"
```

### Task 2: Computed buyer rules (`ResolveBuyerMailAction`, final delete/supplier rules)

**Files:**
- Modify: `src/WebMail/Services/BuyerRuleService.cs`
- Test: `tests/WebMail.Tests/BuyerRuleServiceTests.cs`

**Interfaces:**
- Consumes: `Buyer` with `EmailStatus`, `BuyerStatus`, `SupplierStatus`.
- Produces:
  - `[Flags] enum BuyerMailAction { None=0, Authorize=1, ReAuthorize=2, ChangeEmail=4, ClearAuth=8 }`
  - `BuyerMailAction ResolveBuyerMailAction(Buyer buyer)`
  - `bool CanSupplierSetStatus(Buyer buyer, long? assignedSupplierId, long currentSupplierId)`
  - updated `bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId)` (now also deletable when `Approved` + `Failed`/`Completed`)

This task adds the computed action model and finalizes the sales/supplier rules. `CanBuyerUnlink` stays for now (still called by the Email page and callback until Tasks 3–4).

- [ ] **Step 1: Write failing tests for `ResolveBuyerMailAction`**

Append to `tests/WebMail.Tests/BuyerRuleServiceTests.cs` (inside the class):

```csharp
    private static Buyer B(EmailAuthorizationStatus email, BuyerStatus buyer, SupplierProcessingStatus supplier = SupplierProcessingStatus.Unprocessed) =>
        new() { EmailStatus = email, BuyerStatus = buyer, SupplierStatus = supplier };

    [Fact]
    public void Action_NotSubmitted_AllowsAuthorize() =>
        Assert.Equal(BuyerMailAction.Authorize,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.NotAuthorized, BuyerStatus.NotSubmitted)));

    [Theory]
    [InlineData(BuyerStatus.PendingReview)]
    [InlineData(BuyerStatus.Rejected)]
    public void Action_PreApproval_AllowsChangeAndClear(BuyerStatus status) =>
        Assert.Equal(BuyerMailAction.ChangeEmail | BuyerMailAction.ClearAuth,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, status)));

    [Fact]
    public void Action_Abnormal_AllowsReauthAndChange() =>
        Assert.Equal(BuyerMailAction.ReAuthorize | BuyerMailAction.ChangeEmail,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Abnormal, BuyerStatus.Approved, SupplierProcessingStatus.Failed)));

    [Fact]
    public void Action_ApprovedUnprocessed_IsLocked() =>
        Assert.Equal(BuyerMailAction.None,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStatus.Approved, SupplierProcessingStatus.Unprocessed)));

    [Fact]
    public void Action_ApprovedFailed_AllowsChangeEmail() =>
        Assert.Equal(BuyerMailAction.ChangeEmail,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStatus.Approved, SupplierProcessingStatus.Failed)));

    [Fact]
    public void Action_ApprovedCompleted_AllowsClearThenTerminal()
    {
        Assert.Equal(BuyerMailAction.ClearAuth,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStatus.Approved, SupplierProcessingStatus.Completed)));
        Assert.Equal(BuyerMailAction.None,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.NotAuthorized, BuyerStatus.Approved, SupplierProcessingStatus.Completed)));
    }

    [Theory]
    [InlineData(BuyerStatus.NotSubmitted, EmailAuthorizationStatus.NotAuthorized, SupplierProcessingStatus.Unprocessed, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Failed, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Completed, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Unprocessed, false)]
    [InlineData(BuyerStatus.PendingReview, EmailAuthorizationStatus.Abnormal, SupplierProcessingStatus.Unprocessed, false)]
    public void SalesDelete_FollowsLifecycle(BuyerStatus bs, EmailAuthorizationStatus es, SupplierProcessingStatus ss, bool expected)
    {
        var buyer = new Buyer { SaleId = 5, BuyerStatus = bs, EmailStatus = es, SupplierStatus = ss };
        Assert.Equal(expected, _service.CanSalesDeleteBuyer(buyer, 5));
    }

    [Fact]
    public void SupplierSetStatus_OnlyApprovedAuthorizedAssigned()
    {
        var ok = new Buyer { BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.True(_service.CanSupplierSetStatus(ok, 3, 3));
        var notApproved = new Buyer { BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.False(_service.CanSupplierSetStatus(notApproved, 3, 3));
    }
```

- [ ] **Step 2: Run the new tests to confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~BuyerRuleServiceTests"`
Expected: FAIL — `ResolveBuyerMailAction`, `CanSupplierSetStatus`, and `BuyerMailAction` do not exist; some `CanSalesDeleteBuyer` cases fail.

- [ ] **Step 3: Implement the computed model**

In `src/WebMail/Services/BuyerRuleService.cs`, add at the top of the namespace (above the class):

```csharp
[Flags]
public enum BuyerMailAction
{
    None = 0,
    Authorize = 1,
    ReAuthorize = 2,
    ChangeEmail = 4,
    ClearAuth = 8
}
```

Add these members to `BuyerRuleService`:

```csharp
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

        return buyer.BuyerStatus switch
        {
            BuyerStatus.NotSubmitted => BuyerMailAction.Authorize,
            BuyerStatus.PendingReview or BuyerStatus.Rejected => BuyerMailAction.ChangeEmail | BuyerMailAction.ClearAuth,
            BuyerStatus.Approved => buyer.SupplierStatus switch
            {
                SupplierProcessingStatus.Failed => BuyerMailAction.ChangeEmail,
                SupplierProcessingStatus.Completed => buyer.EmailStatus == EmailAuthorizationStatus.Authorized
                    ? BuyerMailAction.ClearAuth
                    : BuyerMailAction.None,
                _ => BuyerMailAction.None
            },
            _ => BuyerMailAction.None
        };
    }

    public bool CanSupplierSetStatus(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        CanSupplierViewBuyer(buyer, assignedSupplierId, currentSupplierId);
```

Replace `CanSalesDeleteBuyer` with the final rule:

```csharp
    public bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId) =>
        !buyer.IsDeleted
        && buyer.SaleId == salesUserId
        && buyer.EmailStatus != EmailAuthorizationStatus.Abnormal
        && !(buyer.BuyerStatus == BuyerStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Unprocessed);
```

- [ ] **Step 4: Run tests to confirm pass**

Run: `dotnet test --filter "FullyQualifiedName~BuyerRuleServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: computed buyer mail actions and lifecycle-based sales/supplier rules"
```

### Task 3: Buyer Email page — change-email / clear-auth / re-authorize actions

**Files:**
- Modify: `src/WebMail/Pages/Buyer/Email.cshtml.cs`
- Modify: `src/WebMail/Pages/Buyer/Email.cshtml`
- Test: `tests/WebMail.Tests/BuyerPageModelTests.cs`

**Interfaces:**
- Consumes: `BuyerRuleService.ResolveBuyerMailAction(Buyer)`, `BuyerMailAction`.
- Produces: handlers `OnPostChangeEmailAsync(string card)`, `OnPostClearAuthAsync(string card)`; page property `BuyerMailAction Actions`. (Replaces `OnPostUnlinkAsync`.)

Behavior: **Change email** clears the binding and resets to a fresh cycle (`NotAuthorized` + `NotSubmitted` + `Unprocessed`) so the page then shows provider buttons. **Clear auth** clears the binding; if the buyer was `Approved`+`Completed` it stays that way (terminal), otherwise it resets like change-email. **Re-authorize** is a link to OAuth start with the existing provider (recovery handled in Task 4). All preserve `EmailMessage` rows.

- [ ] **Step 1: Write failing page-model tests**

Replace the `UnlinkKeepsMessagesAuditableByBuyer` test in `tests/WebMail.Tests/BuyerPageModelTests.cs` with these (and keep the other two tests):

```csharp
    [Fact]
    public async Task ChangeEmailClearsBindingAndResetsToFreshCycle()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-3", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Failed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        var account = new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        db.EmailMessages.Add(new EmailMessage { BuyerId = buyer.Id, EmailAccountId = account.Id, ProviderMessageId = "m-1", Sender = "s@example.com", Subject = "audit", SentAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());
        await page.OnPostChangeEmailAsync("card-3");

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.NotAuthorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStatus.NotSubmitted, reloaded.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Unprocessed, reloaded.SupplierStatus);
        Assert.Empty(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
        Assert.Single(await db.EmailMessages.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    [Fact]
    public async Task ClearAuthFromCompletedIsTerminal()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-4", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Completed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());
        await page.OnPostClearAuthAsync("card-4");

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.NotAuthorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStatus.Approved, reloaded.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Completed, reloaded.SupplierStatus);
        Assert.Equal(BuyerMailAction.None, new BuyerRuleService().ResolveBuyerMailAction(reloaded));
    }

    [Fact]
    public async Task ClearAuthBlockedWhileProcessing()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-5", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Unprocessed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());
        await page.OnPostClearAuthAsync("card-5");

        Assert.Single(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }
```

Add `using WebMail.Domain;` is already present; ensure `BuyerMailAction` resolves (it is in `WebMail.Services`, already imported).

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~BuyerPageModelTests"`
Expected: FAIL — `OnPostChangeEmailAsync` / `OnPostClearAuthAsync` do not exist.

- [ ] **Step 3: Rewrite the Email page model**

Replace the whole `src/WebMail/Pages/Buyer/Email.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Buyer;

public class EmailModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly BuyerRuleService _ruleService;

    public EmailModel(WebMailDbContext db, BuyerRuleService ruleService)
    {
        _db = db;
        _ruleService = ruleService;
    }

    public string? Card { get; private set; }
    public string? ErrorMessage { get; private set; }
    public CardStatus CardStatus { get; private set; }
    public EmailAuthorizationStatus EmailStatus { get; private set; }
    public BuyerStatus BuyerStatus { get; private set; }
    public SupplierProcessingStatus SupplierStatus { get; private set; }
    public BuyerMailAction Actions { get; private set; }
    public EmailAccount? EmailAccount { get; private set; }
    public IReadOnlyList<EmailMessage> Messages { get; private set; } = Array.Empty<EmailMessage>();

    public async Task<IActionResult> OnGetAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);
        return Render(buyer, account);
    }

    public async Task<IActionResult> OnPostChangeEmailAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);

        if (!_ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ChangeEmail))
        {
            ErrorMessage = _ruleService.BuyerUnlinkBlockedMessage;
            return Render(buyer, account);
        }

        if (account is not null)
        {
            _db.EmailAccounts.Remove(account);
        }
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        buyer.BuyerStatus = BuyerStatus.NotSubmitted;
        buyer.SupplierStatus = SupplierProcessingStatus.Unprocessed;
        await _db.SaveChangesAsync();

        return Render(buyer, null);
    }

    public async Task<IActionResult> OnPostClearAuthAsync(string card)
    {
        var buyer = await LoadBuyerAsync(card);
        if (buyer is null)
        {
            ErrorMessage = "链接无效或已失效";
            return Page();
        }

        Card = card;
        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyer.Id);

        if (!_ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ClearAuth))
        {
            ErrorMessage = _ruleService.BuyerUnlinkBlockedMessage;
            return Render(buyer, account);
        }

        if (account is not null)
        {
            _db.EmailAccounts.Remove(account);
        }
        buyer.EmailStatus = EmailAuthorizationStatus.NotAuthorized;
        // Keep Approved+Completed as the terminal "cleared" state; otherwise reset to a fresh cycle.
        if (!(buyer.BuyerStatus == BuyerStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Completed))
        {
            buyer.BuyerStatus = BuyerStatus.NotSubmitted;
            buyer.SupplierStatus = SupplierProcessingStatus.Unprocessed;
        }
        await _db.SaveChangesAsync();

        return Render(buyer, null);
    }

    private async Task<Domain.Buyer?> LoadBuyerAsync(string card)
    {
        if (string.IsNullOrWhiteSpace(card))
        {
            return null;
        }

        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.CardNo == card && !b.IsDeleted);
        if (buyer is null || buyer.CardStatus == CardStatus.DeletedOrDisabled)
        {
            return null;
        }

        return buyer;
    }

    private IActionResult Render(Domain.Buyer buyer, EmailAccount? account)
    {
        CardStatus = buyer.CardStatus;
        EmailStatus = buyer.EmailStatus;
        BuyerStatus = buyer.BuyerStatus;
        SupplierStatus = buyer.SupplierStatus;
        Actions = _ruleService.ResolveBuyerMailAction(buyer);
        EmailAccount = account;
        Messages = Array.Empty<EmailMessage>();
        return Page();
    }
}
```

- [ ] **Step 4: Rewrite the Email view**

Replace the entire `@if (!string.IsNullOrEmpty(Model.Card)) { ... }` block (original lines 15-42, including its braces) of `src/WebMail/Pages/Buyer/Email.cshtml` with:

```cshtml
@if (!string.IsNullOrEmpty(Model.Card))
{
    <dl class="row">
        <dt class="col-sm-3">卡密状态</dt>
        <dd class="col-sm-9">@Model.CardStatus</dd>
        <dt class="col-sm-3">邮箱状态</dt>
        <dd class="col-sm-9">@Model.EmailStatus</dd>
        <dt class="col-sm-3">买家状态</dt>
        <dd class="col-sm-9">@Model.BuyerStatus</dd>
        <dt class="col-sm-3">供应商状态</dt>
        <dd class="col-sm-9">@Model.SupplierStatus</dd>
    </dl>

    @if (Model.EmailAccount is not null)
    {
        <p><strong>已绑定邮箱：</strong>@Model.EmailAccount.Email</p>
    }

    <div class="d-flex gap-2">
        @if (Model.Actions.HasFlag(BuyerMailAction.Authorize))
        {
            <a class="btn btn-primary" asp-page="/OAuth/Start" asp-route-provider="Gmail" asp-route-card="@Model.Card">授权 Gmail</a>
            <a class="btn btn-outline-primary" asp-page="/OAuth/Start" asp-route-provider="Outlook" asp-route-card="@Model.Card">授权 Outlook</a>
        }
        @if (Model.Actions.HasFlag(BuyerMailAction.ReAuthorize) && Model.EmailAccount is not null)
        {
            <a class="btn btn-warning" asp-page="/OAuth/Start" asp-route-provider="@Model.EmailAccount.Provider" asp-route-card="@Model.Card">重新授权</a>
        }
        @if (Model.Actions.HasFlag(BuyerMailAction.ChangeEmail))
        {
            <form method="post" asp-page-handler="ChangeEmail">
                <input type="hidden" name="card" value="@Model.Card" />
                <button type="submit" class="btn btn-secondary">更改邮箱</button>
            </form>
        }
        @if (Model.Actions.HasFlag(BuyerMailAction.ClearAuth))
        {
            <form method="post" asp-page-handler="ClearAuth">
                <input type="hidden" name="card" value="@Model.Card" />
                <button type="submit" class="btn btn-danger">清空授权</button>
            </form>
        }
    </div>

    @if (Model.Actions == BuyerMailAction.None)
    {
        <div class="alert alert-info mt-2" role="alert">当前状态暂无可执行的操作。</div>
    }
}
```

Also add `@using WebMail.Services` near the top of the view (after `@using WebMail.Domain`) so `BuyerMailAction` resolves.

- [ ] **Step 5: Run tests to confirm pass**

Run: `dotnet test --filter "FullyQualifiedName~BuyerPageModelTests"`
Expected: PASS.

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build`
Expected: succeeds (the old `Unlink` handler/button are gone; nothing references them).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: buyer change-email / clear-auth / re-authorize actions"
```

### Task 4: OAuth callback — new binding, abnormal recovery, token refresh

**Files:**
- Modify: `src/WebMail/Pages/OAuth/Callback.cshtml.cs`
- Modify: `src/WebMail/Services/BuyerRuleService.cs` (remove now-unused `CanBuyerUnlink`)
- Test: `tests/WebMail.Tests/OAuthCallbackModelTests.cs` (create)

**Interfaces:**
- Consumes: `BuyerRuleService.ResolveBuyerMailAction(Buyer)`, `BuyerMailAction`, `IEmailProviderResolver`.
- Behavior: new/changed binding → `Authorized` + `PendingReview`; same provider+email while `Abnormal` → `Authorized` (recovery, review/supplier untouched); same provider+email otherwise → token update only.

- [ ] **Step 1: Write failing callback tests**

Create `tests/WebMail.Tests/OAuthCallbackModelTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages.OAuth;
using WebMail.Services;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class OAuthCallbackModelTests
{
    [Fact]
    public async Task NewBindingGoesToAuthorizedPendingReview()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "c1", CardStatus = CardStatus.Entered });
        await db.SaveChangesAsync();

        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"));
        await model.OnGetAsync("Gmail", "code", "c1", null, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "c1");
        Assert.Equal(EmailAuthorizationStatus.Authorized, buyer.EmailStatus);
        Assert.Equal(BuyerStatus.PendingReview, buyer.BuyerStatus);
        Assert.Single(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    [Fact]
    public async Task AbnormalSameMailboxRecoversWithoutTouchingReviewOrSupplier()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", CardStatus = CardStatus.Authorized, EmailStatus = EmailAuthorizationStatus.Abnormal, BuyerStatus = BuyerStatus.Approved, SupplierStatus = SupplierProcessingStatus.Failed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Provider = "Gmail", Email = "same@example.com", ProviderUserId = "u", EncryptedRefreshToken = "old" });
        await db.SaveChangesAsync();

        var model = CreateModel(db, new FakeAuthProvider("Gmail", "same@example.com"));
        await model.OnGetAsync("Gmail", "code", "c2", null, CancellationToken.None);

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.Authorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStatus.Approved, reloaded.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Failed, reloaded.SupplierStatus);
    }

    private static CallbackModel CreateModel(WebMailDbContext db, IEmailProvider provider) =>
        new(db, new BuyerRuleService(), new EmailProviderResolver([provider]));

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeAuthProvider(string name, string email) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthCallbackResult(email, "provider-user", "refresh"));
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~OAuthCallbackModelTests"`
Expected: FAIL — recovery branch not implemented (Abnormal stays Abnormal).

- [ ] **Step 3: Update the callback guard and status logic**

In `src/WebMail/Pages/OAuth/Callback.cshtml.cs`, replace the guard block (lines 46-53) with:

```csharp
        var isNewOrChangedAccount = existing is null
            || !string.Equals(existing.Provider, emailProvider.Name, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(existing.Email, authorization.Email, StringComparison.OrdinalIgnoreCase);

        if (existing is not null && isNewOrChangedAccount
            && !ruleService.ResolveBuyerMailAction(buyer).HasFlag(BuyerMailAction.ChangeEmail))
        {
            ErrorMessage = ruleService.BuyerUnlinkBlockedMessage;
            return Page();
        }
```

Then delete the now-duplicate `isNewOrChangedAccount` declaration that followed (old lines 55-57).

Replace the status block (old lines 78-82) with:

```csharp
        buyer.CardStatus = CardStatus.Authorized;
        if (isNewOrChangedAccount)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
            buyer.BuyerStatus = BuyerStatus.PendingReview;
        }
        else if (buyer.EmailStatus == EmailAuthorizationStatus.Abnormal)
        {
            // Same mailbox re-authorized: token refreshed; restore health, leave review/supplier intact.
            buyer.EmailStatus = EmailAuthorizationStatus.Authorized;
        }
```

- [ ] **Step 4: Remove the unused `CanBuyerUnlink`**

In `src/WebMail/Services/BuyerRuleService.cs`, delete the `CanBuyerUnlink` method (the `PreApprovalStatuses` set and `BuyerUnlinkBlockedMessage` are still used — keep them). Confirm no other references:

Run: `grep -rn "CanBuyerUnlink" src tests`
Expected: no matches.

- [ ] **Step 5: Run tests to confirm pass**

Run: `dotnet test --filter "FullyQualifiedName~OAuthCallbackModelTests"`
Expected: PASS.
Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: oauth callback handles new binding and abnormal recovery"
```

### Task 5: Supplier set-status action

**Files:**
- Modify: `src/WebMail/Pages/Supplier/Buyers.cshtml.cs`
- Modify: `src/WebMail/Pages/Supplier/Buyers.cshtml`
- Test: `tests/WebMail.Tests/SupplierBuyersModelTests.cs` (create)

**Interfaces:**
- Consumes: `BuyerRuleService.CanSupplierSetStatus(Buyer, long?, long)`.
- Produces: handler `OnPostSetStatusAsync(long buyerId, SupplierProcessingStatus status)`.

- [ ] **Step 1: Write failing tests**

Create `tests/WebMail.Tests/SupplierBuyersModelTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using SupplierBuyers = WebMail.Pages.Supplier.BuyersModel;
using Xunit;

namespace WebMail.Tests;

public sealed class SupplierBuyersModelTests
{
    [Fact]
    public async Task SetStatusMarksFailedAndWritesAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = 3 });
        await db.SaveChangesAsync();

        var model = CreateModel(db, supplierId: 3);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Failed);

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(SupplierProcessingStatus.Failed, reloaded.SupplierStatus);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SetStatusBlockedWhenNotApproved()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = 3 });
        await db.SaveChangesAsync();

        var model = CreateModel(db, supplierId: 3);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Completed);

        Assert.Equal(SupplierProcessingStatus.Unprocessed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).SupplierStatus);
    }

    [Fact]
    public async Task SetStatusBlockedForOtherSupplier()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c3", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = 99 });
        await db.SaveChangesAsync();

        var model = CreateModel(db, supplierId: 3);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Failed);

        Assert.Equal(SupplierProcessingStatus.Unprocessed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).SupplierStatus);
    }

    private static SupplierBuyers CreateModel(WebMailDbContext db, long supplierId)
    {
        var model = new SupplierBuyers(db, new BuyerRuleService());
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, supplierId.ToString())], "test"))
        };
        model.PageContext = new PageContext { HttpContext = httpContext };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~SupplierBuyersModelTests"`
Expected: FAIL — constructor takes one arg; `OnPostSetStatusAsync` missing.

- [ ] **Step 3: Add the rule dependency and handler**

Replace `src/WebMail/Pages/Supplier/Buyers.cshtml.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Supplier;

[Authorize(Policy = "SupplierOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly BuyerRuleService _ruleService;

    public BuyersModel(WebMailDbContext db, BuyerRuleService ruleService)
    {
        _db = db;
        _ruleService = ruleService;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var supplierId))
        {
            return Forbid();
        }

        await LoadBuyersAsync(supplierId);
        return Page();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(long buyerId, SupplierProcessingStatus status)
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var supplierId))
        {
            return Forbid();
        }

        if (status is not (SupplierProcessingStatus.Failed or SupplierProcessingStatus.Completed))
        {
            Message = "无效的状态。";
            await LoadBuyersAsync(supplierId);
            return Page();
        }

        var assignment = await _db.BuyerSupplierAssignments
            .Include(x => x.Buyer)
            .FirstOrDefaultAsync(x => x.BuyerId == buyerId && x.SupplierId == supplierId);

        if (assignment is not null && _ruleService.CanSupplierSetStatus(assignment.Buyer, assignment.SupplierId, supplierId))
        {
            assignment.Buyer.SupplierStatus = status;
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "SupplierSetStatus",
                UserId = supplierId,
                Details = $"buyer={buyerId};status={status}"
            });
            await _db.SaveChangesAsync();
            Message = "已更新处理状态。";
        }
        else
        {
            Message = "无法更新该买家的处理状态。";
        }

        await LoadBuyersAsync(supplierId);
        return Page();
    }

    private async Task LoadBuyersAsync(long supplierId)
    {
        Buyers = await _db.BuyerSupplierAssignments
            .Include(x => x.Buyer)
            .Where(x => x.SupplierId == supplierId
                && !x.Buyer.IsDeleted
                && x.Buyer.BuyerStatus == BuyerStatus.Approved
                && x.Buyer.EmailStatus == EmailAuthorizationStatus.Authorized)
            .Select(x => x.Buyer)
            .ToListAsync();
    }
}
```

- [ ] **Step 4: Update the supplier view**

Replace `src/WebMail/Pages/Supplier/Buyers.cshtml` body (from the `@if` at line 10 to end):

```cshtml
@if (!string.IsNullOrEmpty(Model.Message))
{
    <div class="alert alert-info" role="alert">@Model.Message</div>
}

@if (Model.Buyers.Count == 0)
{
    <p>暂无买家。</p>
}
else
{
    <table class="table table-striped">
        <thead>
            <tr>
                <th>卡密</th>
                <th>邮箱状态</th>
                <th>买家状态</th>
                <th>供应商状态</th>
                <th>操作</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var buyer in Model.Buyers)
            {
                <tr>
                    <td>@buyer.CardNo</td>
                    <td>@buyer.EmailStatus</td>
                    <td>@buyer.BuyerStatus</td>
                    <td>@buyer.SupplierStatus</td>
                    <td>
                        <div class="d-flex gap-2">
                            <a class="btn btn-sm btn-primary" asp-page="Mail" asp-route-buyerId="@buyer.Id">查看邮件</a>
                            <form method="post" asp-page-handler="SetStatus">
                                <input type="hidden" name="buyerId" value="@buyer.Id" />
                                <input type="hidden" name="status" value="@SupplierProcessingStatus.Failed" />
                                <button type="submit" class="btn btn-sm btn-outline-danger">标记失败</button>
                            </form>
                            <form method="post" asp-page-handler="SetStatus">
                                <input type="hidden" name="buyerId" value="@buyer.Id" />
                                <input type="hidden" name="status" value="@SupplierProcessingStatus.Completed" />
                                <button type="submit" class="btn btn-sm btn-outline-success">标记完成</button>
                            </form>
                        </div>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 5: Run tests + build**

Run: `dotnet test --filter "FullyQualifiedName~SupplierBuyersModelTests"`
Expected: PASS.
Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: supplier mark-failed / mark-completed action"
```

### Task 6: Admin approve/reject + three-status columns on admin & sales lists

**Files:**
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml.cs`
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml`
- Modify: `src/WebMail/Pages/Sales/Buyers.cshtml`
- Test: `tests/WebMail.Tests/AdminBuyersModelTests.cs` (create)

**Interfaces:**
- Produces: `OnPostApproveAsync(long id)`, `OnPostRejectAsync(long id)` — transition `PendingReview → Approved/Rejected` only.

Admin review is the prerequisite that produces `Approved` buyers (the supplier flow depends on it).

- [ ] **Step 1: Write failing tests**

Create `tests/WebMail.Tests/AdminBuyersModelTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using AdminBuyers = WebMail.Pages.Admin.BuyersModel;
using Xunit;

namespace WebMail.Tests;

public sealed class AdminBuyersModelTests
{
    [Fact]
    public async Task ApproveMovesPendingToApprovedWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostApproveAsync(buyer.Id);

        Assert.Equal(BuyerStatus.Approved, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).BuyerStatus);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task RejectMovesPendingToRejected()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostRejectAsync(buyer.Id);

        Assert.Equal(BuyerStatus.Rejected, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).BuyerStatus);
    }

    [Fact]
    public async Task ApproveIgnoredWhenNotPending()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c3", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostApproveAsync(buyer.Id);

        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    private static AdminBuyers CreateModel(WebMailDbContext db, long adminId)
    {
        var model = new AdminBuyers(db);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, adminId.ToString())], "test"))
        };
        model.PageContext = new PageContext { HttpContext = httpContext };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~AdminBuyersModelTests"`
Expected: FAIL — handlers missing.

- [ ] **Step 3: Add admin approve/reject handlers**

Replace `src/WebMail/Pages/Admin/Buyers.cshtml.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;

    public BuyersModel(WebMailDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public string? Message { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(long id) => await ReviewAsync(id, BuyerStatus.Approved);

    public async Task<IActionResult> OnPostRejectAsync(long id) => await ReviewAsync(id, BuyerStatus.Rejected);

    private async Task<IActionResult> ReviewAsync(long id, BuyerStatus decision)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null && buyer.BuyerStatus == BuyerStatus.PendingReview)
        {
            buyer.BuyerStatus = decision;
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminReview",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={id};decision={decision}"
            });
            await _db.SaveChangesAsync();
            Message = "已更新审核状态。";
        }
        else
        {
            Message = "无法审核该买家。";
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Buyers = await _db.Buyers
            .Where(b => !b.IsDeleted)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
    }
}
```

- [ ] **Step 4: Update the admin view**

Replace `src/WebMail/Pages/Admin/Buyers.cshtml` body (from line 10 `@if` to end):

```cshtml
@if (!string.IsNullOrEmpty(Model.Message))
{
    <div class="alert alert-info" role="alert">@Model.Message</div>
}

@if (Model.Buyers.Count == 0)
{
    <p>暂无买家。</p>
}
else
{
    <table class="table table-striped">
        <thead>
            <tr>
                <th>卡密</th>
                <th>邮箱状态</th>
                <th>买家状态</th>
                <th>供应商状态</th>
                <th>创建时间</th>
                <th>操作</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var buyer in Model.Buyers)
            {
                <tr>
                    <td>@buyer.CardNo</td>
                    <td>@buyer.EmailStatus</td>
                    <td>@buyer.BuyerStatus</td>
                    <td>@buyer.SupplierStatus</td>
                    <td>@buyer.CreatedAt</td>
                    <td>
                        @if (buyer.BuyerStatus == BuyerStatus.PendingReview)
                        {
                            <div class="d-flex gap-2">
                                <form method="post" asp-page-handler="Approve">
                                    <input type="hidden" name="id" value="@buyer.Id" />
                                    <button type="submit" class="btn btn-sm btn-success">通过</button>
                                </form>
                                <form method="post" asp-page-handler="Reject">
                                    <input type="hidden" name="id" value="@buyer.Id" />
                                    <button type="submit" class="btn btn-sm btn-danger">拒绝</button>
                                </form>
                            </div>
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 5: Add the buyer-status column to the sales list**

In `src/WebMail/Pages/Sales/Buyers.cshtml`:
- In `<thead>` after `<th>授权状态</th>` (line 26), insert `<th>买家状态</th>`. Rename `授权状态` to `邮箱状态` for consistency.
- In `<tbody>` after `<td>@buyer.EmailStatus</td>` (line 36), insert `<td>@buyer.BuyerStatus</td>`.

- [ ] **Step 6: Run tests + build**

Run: `dotnet test --filter "FullyQualifiedName~AdminBuyersModelTests"`
Expected: PASS.
Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: admin approve/reject review and three-status list columns"
```

### Task 7: Abnormal detection in the sync pipeline

**Files:**
- Create: `src/WebMail/Services/EmailProviders/ProviderAuthorizationException.cs`
- Modify: `src/WebMail/Services/Background/MailSyncProcessor.cs:47-56`
- Modify: `src/WebMail/Services/EmailProviders/GmailProvider.cs:67-92`
- Modify: `src/WebMail/Services/EmailProviders/OutlookProvider.cs` (`FetchFolderAsync` ~98, `RequestTokenAsync` ~150)
- Test: `tests/WebMail.Tests/MailSyncProcessorTests.cs`

**Interfaces:**
- Produces: `ProviderAuthorizationException` (thrown by providers on token/permission failure); processor flips buyer `Authorized → Abnormal` on it.

- [ ] **Step 1: Write failing processor tests**

In `tests/WebMail.Tests/MailSyncProcessorTests.cs`, add a seed helper and two tests:

```csharp
    private static void SeedBuyer(WebMailDbContext db, long id, EmailAuthorizationStatus email, BuyerStatus buyer = BuyerStatus.Approved, SupplierProcessingStatus supplier = SupplierProcessingStatus.Failed) =>
        db.Buyers.Add(new Buyer { Id = id, CardNo = $"card-{id}", EmailStatus = email, BuyerStatus = buyer, SupplierStatus = supplier });

    [Fact]
    public async Task ProcessPendingFlipsBuyerToAbnormalOnAuthFailure()
    {
        await using var db = CreateDb();
        SeedBuyer(db, id: 1, email: EmailAuthorizationStatus.Authorized);
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "AuthBoom");
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new AuthThrowingProvider("AuthBoom"));
        await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.Id == 1);
        Assert.Equal(EmailAuthorizationStatus.Abnormal, buyer.EmailStatus);
        Assert.Equal(BuyerStatus.Approved, buyer.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Failed, buyer.SupplierStatus);
        Assert.Equal(SyncJobStatus.Failed, (await db.SyncJobs.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProcessPendingDoesNotFlipOnGenericError()
    {
        await using var db = CreateDb();
        SeedBuyer(db, id: 1, email: EmailAuthorizationStatus.Authorized);
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "Boom");
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new ThrowingProvider("Boom"));
        await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(EmailAuthorizationStatus.Authorized, (await db.Buyers.SingleAsync(x => x.Id == 1)).EmailStatus);
    }
```

And add the provider fake (next to `ThrowingProvider`):

```csharp
    private sealed class AuthThrowingProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new ProviderAuthorizationException("auth failed");
    }
```

- [ ] **Step 2: Run tests to confirm they fail**

Run: `dotnet test --filter "FullyQualifiedName~MailSyncProcessorTests"`
Expected: FAIL — `ProviderAuthorizationException` does not exist; buyer not flipped.

- [ ] **Step 3: Add the exception type**

Create `src/WebMail/Services/EmailProviders/ProviderAuthorizationException.cs`:

```csharp
namespace WebMail.Services.EmailProviders;

/// <summary>
/// Thrown by a provider when the stored authorization (refresh token / granted permission)
/// is no longer usable: revoked, expired, or rejected with an authorization error.
/// </summary>
public sealed class ProviderAuthorizationException : Exception
{
    public ProviderAuthorizationException(string message) : base(message) { }
    public ProviderAuthorizationException(string message, Exception innerException) : base(message, innerException) { }
}
```

- [ ] **Step 4: Add the processor catch**

In `src/WebMail/Services/Background/MailSyncProcessor.cs`, insert a catch between the `OperationCanceledException` catch (line 47-50) and the generic `catch (Exception ex)` (line 51):

```csharp
            catch (ProviderAuthorizationException)
            {
                var buyer = await db.Buyers.FirstOrDefaultAsync(b => b.Id == job.BuyerId, cancellationToken);
                if (buyer is not null && buyer.EmailStatus == EmailAuthorizationStatus.Authorized)
                {
                    buyer.EmailStatus = EmailAuthorizationStatus.Abnormal;
                    db.AuditLogs.Add(new AuditLog { Action = "MailboxAbnormal", Details = $"buyer={job.BuyerId}" });
                }
                job.Status = SyncJobStatus.Failed;
                job.Error = "authorization";
                job.CompletedAt = now;
            }
```

- [ ] **Step 5: Classify auth failures in Gmail**

In `src/WebMail/Services/EmailProviders/GmailProvider.cs`, wrap the body of `FetchMessagesAsync` (lines 69-91) in a try/catch (keep the inner logic identical):

```csharp
        try
        {
            using var service = CreateService(refreshToken);

            var listRequest = service.Users.Messages.List("me");
            listRequest.Q = BuildGmailQuery(allowedSenders, since);
            listRequest.IncludeSpamTrash = true;
            listRequest.MaxResults = 50;

            var listResponse = await listRequest.ExecuteAsync(cancellationToken);
            if (listResponse.Messages is null)
            {
                return [];
            }

            var results = new List<ProviderMessage>();
            foreach (var reference in listResponse.Messages)
            {
                var getRequest = service.Users.Messages.Get("me", reference.Id);
                getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                var message = await getRequest.ExecuteAsync(cancellationToken);
                results.Add(MapMessage(message));
            }

            return results;
        }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException ex) when (ex.Error?.Error == "invalid_grant")
        {
            throw new ProviderAuthorizationException("Gmail refresh token rejected.", ex);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new ProviderAuthorizationException("Gmail authorization failed.", ex);
        }
```

- [ ] **Step 6: Classify auth failures in Outlook**

In `src/WebMail/Services/EmailProviders/OutlookProvider.cs`:

In `FetchFolderAsync`, replace `response.EnsureSuccessStatusCode();` (line 98) with:

```csharp
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new ProviderAuthorizationException($"Microsoft Graph fetch rejected: {(int)response.StatusCode}.");
        }
        response.EnsureSuccessStatusCode();
```

In `RequestTokenAsync`, replace `await EnsureSuccessOrThrowAsync(response, "Microsoft token request", cancellationToken);` (line 153) with:

```csharp
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                || (response.StatusCode == System.Net.HttpStatusCode.BadRequest && errorBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProviderAuthorizationException($"Microsoft token refresh rejected: {(int)response.StatusCode}. {errorBody}");
            }
            throw new InvalidOperationException($"Microsoft token request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {errorBody}");
        }
```

- [ ] **Step 7: Run tests + build**

Run: `dotnet test --filter "FullyQualifiedName~MailSyncProcessorTests"`
Expected: PASS.
Run: `dotnet build`
Expected: succeeds.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: detect token authorization failures and flip mailbox to abnormal"
```

### Task 8: Full-suite verification

- [ ] **Step 1: Build and run everything**

Run: `dotnet build`
Expected: succeeds with no warnings about missing enum members.
Run: `dotnet test`
Expected: all tests PASS.

- [ ] **Step 2: Confirm no stale references remain**

Run: `grep -rn "EmailAuthorizationStatus.Normal\|EmailAuthorizationStatus.PendingReview\|EmailAuthorizationStatus.Rejected\|CanBuyerUnlink" src tests`
Expected: no matches.

- [ ] **Step 3: Final commit (if anything pending)**

```bash
git add -A
git commit -m "chore: verify buyer/supplier status permissions feature green"
```
