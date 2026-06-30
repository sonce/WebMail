# Buyer → Supplier Manual Assignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an administrator manually assign (and re-assign / unassign) a buyer to a supplier from the Admin Buyers page, creating the `BuyerSupplierAssignment` row that makes the buyer visible to that supplier.

**Architecture:** New `OnPostAssignSupplierAsync` handler on `Admin/Buyers.cshtml.cs` does upsert-by-`BuyerId` (or delete on null). `LoadAsync` additionally loads active suppliers and a per-buyer assignment map for rendering. The Razor page adds a "供应商" column with inline edit (display name + pencil → in-place `<select>` with an "未分配" option). No new entity, no migration.

**Tech Stack:** ASP.NET Core Razor Pages, EF Core (in-memory for tests), xUnit, .resx localization.

## Global Constraints

- `BuyerSupplierAssignment` has a **unique index on `BuyerId`** (`WebMailDbContext.cs:28`) — at most one row per buyer. Upsert by `BuyerId`; never `Add` a second row for the same buyer.
- `UserRole.Supplier == 3` (`Domain/Enums.cs:3`). Validate target user is `Role == Supplier && IsActive` before assigning.
- `DbSet<AppUser> Users` (`WebMailDbContext.cs:9`), `DbSet<BuyerSupplierAssignment> BuyerSupplierAssignments` (`WebMailDbContext.cs:14`).
- Localizer in page model is `IStringLocalizer<SharedResource> _loc`; tests use `TestLocalizer.Shared` (an echo localizer — returns the key as value).
- Page model reads admin id via `User.FindFirstValue(ClaimTypes.NameIdentifier)`; tests set it on `PageContext.HttpContext` (see `AdminBuyersModelTests.cs:116-125`).
- Resource keys: `Common.Cancel` already exists (en/zh). `Common.Save` does NOT exist — add it. Do not duplicate existing keys.
- Resource file format: `<data name="KEY" xml:space="preserve"><value>VALUE</value></data>` lines inside `<root>...</root>`, before `</root>`.
- This repo does feature work on `master` (not `main`) and has no remote; the user is not git-savvy. **Do NOT commit** unless the user explicitly asks. Steps that say "commit" are skipped — leave changes in the working tree.
- `BuyerSupplierAssignments.Add` must NOT appear in production code anywhere except the new handler. (It currently appears only in tests/docs.)

---

## File Structure

- **Modify** `src/WebMail/Pages/Admin/Buyers.cshtml.cs` — add view-model records, new properties, `OnPostAssignSupplierAsync` handler, extend `LoadAsync`.
- **Modify** `src/WebMail/Pages/Admin/Buyers.cshtml` — add "供应商" column with inline edit markup + script.
- **Modify** `src/WebMail/Resources/SharedResource.en.resx` — new keys.
- **Modify** `src/WebMail/Resources/SharedResource.zh-CN.resx` — new keys (zh-CN values).
- **Modify** `tests/WebMail.Tests/AdminBuyersModelTests.cs` — assignment handler + load tests.

---

### Task 1: Add localization keys (en + zh-CN)

**Files:**
- Modify: `src/WebMail/Resources/SharedResource.en.resx`
- Modify: `src/WebMail/Resources/SharedResource.zh-CN.resx`

**Interfaces:**
- Produces: resource keys `Table.Supplier`, `Action.Unassigned`, `Common.Save`, `Admin.Buyers.Assigned`, `Admin.Buyers.AssignFailed` (consumed by Tasks 2 and 3).

- [ ] **Step 1: Add keys to the English resx**

Insert these lines immediately before the final `</root>` line in `src/WebMail/Resources/SharedResource.en.resx`:

```xml
  <data name="Table.Supplier" xml:space="preserve"><value>Supplier</value></data>
  <data name="Action.Unassigned" xml:space="preserve"><value>Unassigned</value></data>
  <data name="Common.Save" xml:space="preserve"><value>Save</value></data>
  <data name="Admin.Buyers.Assigned" xml:space="preserve"><value>Supplier assigned.</value></data>
  <data name="Admin.Buyers.AssignFailed" xml:space="preserve"><value>Assignment failed.</value></data>
```

- [ ] **Step 2: Add keys to the Chinese resx**

Insert these lines immediately before the final `</root>` line in `src/WebMail/Resources/SharedResource.zh-CN.resx`:

```xml
  <data name="Table.Supplier" xml:space="preserve"><value>供应商</value></data>
  <data name="Action.Unassigned" xml:space="preserve"><value>未分配</value></data>
  <data name="Common.Save" xml:space="preserve"><value>保存</value></data>
  <data name="Admin.Buyers.Assigned" xml:space="preserve"><value>已分配供应商。</value></data>
  <data name="Admin.Buyers.AssignFailed" xml:space="preserve"><value>分配失败。</value></data>
```

- [ ] **Step 3: Verify build**

Run: `dotnet build src/WebMail/WebMail.csproj`
Expected: build succeeds (resx compiles).

- [ ] **Step 4: Commit** *(skip — user does not commit; leave in working tree)*

---

### Task 2: Page model — view-model types, properties, load, and assign handler

**Files:**
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml.cs` (whole file)
- Test: `tests/WebMail.Tests/AdminBuyersModelTests.cs`

**Interfaces:**
- Consumes: resource keys from Task 1; entities `Buyer`, `BuyerSupplierAssignment`, `AppUser`; `UserRole.Supplier`.
- Produces:
  - `public IReadOnlyList<SupplierOption> Suppliers { get; private set; }`
  - `public IReadOnlyDictionary<long, SupplierAssignmentView> AssignmentByBuyer { get; private set; }`
  - `public sealed record SupplierOption(long Id, string DisplayName);`
  - `public sealed record SupplierAssignmentView(long? SupplierId, string DisplayName);`
  - `public async Task<IActionResult> OnPostAssignSupplierAsync(long buyerId, long? supplierId)`

- [ ] **Step 1: Write the failing tests**

Append to `tests/WebMail.Tests/AdminBuyersModelTests.cs`, inside the `AdminBuyersModelTests` class (before the `CreateModel` helper at line 116). First add a `using Microsoft.EntityFrameworkCore;` is already present; ensure `System.Linq` (implicit). Add these tests:

```csharp
    [Fact]
    public async Task AssignSupplierCreatesAssignmentWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved };
        var supplier = new AppUser { UserName = "sup", DisplayName = "Sup One", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(supplier);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, supplier.Id);

        var assignment = await db.BuyerSupplierAssignments.SingleOrDefaultAsync(x => x.BuyerId == buyer.Id);
        Assert.NotNull(assignment);
        Assert.Equal(supplier.Id, assignment!.SupplierId);
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal("AdminAssignSupplier", audit.Action);
    }

    [Fact]
    public async Task AssignSupplierUpdatesExistingRow()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var s1 = new AppUser { UserName = "s1", DisplayName = "S1", Role = UserRole.Supplier, IsActive = true };
        var s2 = new AppUser { UserName = "s2", DisplayName = "S2", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.AddRange(s1, s2);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = s1.Id });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, s2.Id);

        var rows = await db.BuyerSupplierAssignments.Where(x => x.BuyerId == buyer.Id).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(s2.Id, row.SupplierId);
    }

    [Fact]
    public async Task AssignSupplierNullRemovesAssignment()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var s1 = new AppUser { UserName = "s1", DisplayName = "S1", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(s1);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = s1.Id });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, null);

        Assert.Empty(await db.BuyerSupplierAssignments.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierNullWithNoRowIsNoop()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, null);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierRejectsNonSupplierUser()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var sales = new AppUser { UserName = "sale", DisplayName = "Sale", Role = UserRole.Sales, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(sales);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, sales.Id);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierRejectsInactiveSupplier()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var sup = new AppUser { UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier, IsActive = false };
        db.Buyers.Add(buyer);
        db.Users.Add(sup);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, sup.Id);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierRejectsDeletedBuyer()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, IsDeleted = true };
        var sup = new AppUser { UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(sup);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, sup.Id);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
    }

    [Fact]
    public async Task GetLoadsSuppliersAndAssignmentMap()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var sup = new AppUser { UserName = "sup", DisplayName = "Sup One", Role = UserRole.Supplier, IsActive = true };
        var inactive = new AppUser { UserName = "dead", DisplayName = "Dead", Role = UserRole.Supplier, IsActive = false };
        db.Buyers.Add(buyer);
        db.Users.AddRange(sup, inactive);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = sup.Id });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnGetAsync();

        Assert.Equal("Sup One", Assert.Single(model.Suppliers).DisplayName);
        Assert.True(model.AssignmentByBuyer.TryGetValue(buyer.Id, out var view));
        Assert.Equal(sup.Id, view.SupplierId);
        Assert.Equal("Sup One", view.DisplayName);
    }

    [Fact]
    public async Task GetAssignmentMapShowsUnassignedAsNull()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnGetAsync();

        Assert.True(model.AssignmentByBuyer.TryGetValue(buyer.Id, out var view));
        Assert.Null(view.SupplierId);
        Assert.Equal(string.Empty, view.DisplayName);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~AdminBuyersModelTests"`
Expected: FAIL — `OnPostAssignSupplierAsync` does not exist; `Suppliers` / `AssignmentByBuyer` do not exist; compile errors.

- [ ] **Step 3: Write the implementation**

Replace the entire contents of `src/WebMail/Pages/Admin/Buyers.cshtml.cs` with:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly IStringLocalizer<SharedResource> _loc;

    public BuyersModel(WebMailDbContext db, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _loc = loc;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public IReadOnlyList<SupplierOption> Suppliers { get; private set; } = Array.Empty<SupplierOption>();
    public IReadOnlyDictionary<long, SupplierAssignmentView> AssignmentByBuyer { get; private set; } = new Dictionary<long, SupplierAssignmentView>();
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public BuyerStage? StageFilter { get; set; }
    [BindProperty(SupportsGet = true)] public ReviewStatus? ReviewFilter { get; set; }
    [BindProperty(SupportsGet = true)] public EmailAuthorizationStatus? EmailFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? CardNo { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(long id) => await ReviewAsync(id, ReviewStatus.Approved);

    public async Task<IActionResult> OnPostRejectAsync(long id) => await ReviewAsync(id, ReviewStatus.Rejected);

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null)
        {
            buyer.IsDeleted = true;
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminDeleteBuyer",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={id}"
            });
            await _db.SaveChangesAsync();
            Message = _loc["Admin.Buyers.Deleted"];
        }
        else
        {
            Message = _loc["Admin.Buyers.DeleteFailed"];
        }

        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAssignSupplierAsync(long buyerId, long? supplierId)
    {
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);

        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == buyerId && !b.IsDeleted);
        if (buyer is null)
        {
            Message = _loc["Admin.Buyers.AssignFailed"];
            await LoadAsync();
            return Page();
        }

        if (supplierId is not null)
        {
            var supplier = await _db.Users.FirstOrDefaultAsync(u => u.Id == supplierId && u.Role == UserRole.Supplier && u.IsActive);
            if (supplier is null)
            {
                Message = _loc["Admin.Buyers.AssignFailed"];
                await LoadAsync();
                return Page();
            }
        }

        var existing = await _db.BuyerSupplierAssignments.FirstOrDefaultAsync(x => x.BuyerId == buyerId);
        if (supplierId is not null)
        {
            if (existing is null)
            {
                _db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyerId, SupplierId = supplierId.Value });
            }
            else
            {
                existing.SupplierId = supplierId.Value;
            }
        }
        else if (existing is not null)
        {
            _db.BuyerSupplierAssignments.Remove(existing);
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminAssignSupplier",
            UserId = adminId == 0 ? null : adminId,
            Details = $"buyer={buyerId};supplier={supplierId}"
        });
        await _db.SaveChangesAsync();
        Message = _loc["Admin.Buyers.Assigned"];

        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ReviewAsync(long id, ReviewStatus decision)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null && buyer.Stage == BuyerStage.Submitted && buyer.ReviewStatus == ReviewStatus.Pending)
        {
            buyer.ReviewStatus = decision;
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminReview",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={id};decision={decision}"
            });
            await _db.SaveChangesAsync();
            Message = _loc["Admin.Buyers.Reviewed"];
        }
        else
        {
            Message = _loc["Admin.Buyers.ReviewFailed"];
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        // Un-sent cards are still in inventory (visible only on the card-key page);
        // they only become "buyers" once distributed to a sales person.
        var query = _db.Buyers.Where(b => !b.IsDeleted && b.Stage != BuyerStage.NotSent);
        if (StageFilter is not null)
        {
            query = query.Where(b => b.Stage == StageFilter);
        }
        if (ReviewFilter is not null)
        {
            query = query.Where(b => b.ReviewStatus == ReviewFilter);
        }
        if (EmailFilter is not null)
        {
            query = query.Where(b => b.EmailStatus == EmailFilter);
        }
        if (!string.IsNullOrWhiteSpace(CardNo))
        {
            query = query.Where(b => b.CardNo.Contains(CardNo));
        }

        Buyers = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();

        Suppliers = await _db.Users
            .Where(u => u.Role == UserRole.Supplier && u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new SupplierOption(u.Id, u.DisplayName))
            .ToListAsync();

        var buyerIds = Buyers.Select(b => b.Id).ToList();
        var assigned = await (from a in _db.BuyerSupplierAssignments
                              join u in _db.Users on a.SupplierId equals u.Id into gj
                              from u in gj.DefaultIfEmpty()
                              where buyerIds.Contains(a.BuyerId)
                              select new { a.BuyerId, a.SupplierId, DisplayName = u != null ? u.DisplayName : string.Empty })
                              .ToDictionaryAsync(x => x.BuyerId, x => new SupplierAssignmentView(x.SupplierId, x.DisplayName));

        AssignmentByBuyer = assigned;
    }
}

public sealed record SupplierOption(long Id, string DisplayName);
public sealed record SupplierAssignmentView(long? SupplierId, string DisplayName);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter "FullyQualifiedName~AdminBuyersModelTests"`
Expected: PASS — all 16 tests (8 original + 8 new) green.

- [ ] **Step 5: Commit** *(skip — user does not commit; leave in working tree)*

---

### Task 3: Razor page — "供应商" column with inline edit

**Files:**
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml`

**Interfaces:**
- Consumes: `Model.Suppliers` (`IReadOnlyList<SupplierOption>`), `Model.AssignmentByBuyer` (`IReadOnlyDictionary<long, SupplierAssignmentView>`), resource keys `Table.Supplier`, `Action.Unassigned`, `Common.Save`, `Common.Cancel`.

- [ ] **Step 1: Add the "供应商" column header**

In `src/WebMail/Pages/Admin/Buyers.cshtml`, in the `<thead><tr>` block, the current SupplierStatus header is at line 57:

```html
                <th>@L["Table.SupplierStatus"]</th>
```

Add a new header immediately **before** it:

```html
                <th>@L["Table.Supplier"]</th>
```

So the two adjacent lines read:

```html
                <th>@L["Table.Supplier"]</th>
                <th>@L["Table.SupplierStatus"]</th>
```

- [ ] **Step 2: Add the supplier cell to each row**

In the same file, the per-row `<td>` for SupplierStatus is at line 86:

```html
                    <td data-label="@L["Table.SupplierStatus"]"><partial name="_SupplierStatusBadge" model="buyer.SupplierStatus" /></td>
```

Insert a new `<td>` immediately **before** it. The new cell resolves the current assignment, then renders display state + an inline edit form:

```html
                    @{
                        var assignment = Model.AssignmentByBuyer.TryGetValue(buyer.Id, out var a) ? a : null;
                        var supplierName = string.IsNullOrEmpty(a?.DisplayName) ? "—" : a.DisplayName;
                    }
                    <td data-label="@L["Table.Supplier"]" class="td-supplier">
                        <span class="supplier-display">
                            <span class="supplier-name">@supplierName</span>
                            <button type="button" class="btn btn-sm btn-link p-0 supplier-edit" aria-label="@L["Action.AssignSupplier"]">✎</button>
                        </span>
                        <form method="post" asp-page-handler="AssignSupplier" class="supplier-form d-none align-items-center gap-1">
                            <input type="hidden" name="buyerId" value="@buyer.Id" />
                            <select name="supplierId" class="form-select form-select-sm" style="width:auto;">
                                <option value="">@L["Action.Unassigned"]</option>
                                @foreach (var s in Model.Suppliers)
                                {
                                    if (a is not null && a.SupplierId == s.Id)
                                    {
                                        <option value="@s.Id" selected>@s.DisplayName</option>
                                    }
                                    else
                                    {
                                        <option value="@s.Id">@s.DisplayName</option>
                                    }
                                }
                            </select>
                            <button type="submit" class="btn btn-sm btn-primary">@L["Common.Save"]</button>
                            <button type="button" class="btn btn-sm btn-outline-secondary supplier-cancel">@L["Common.Cancel"]</button>
                        </form>
                    </td>
```

Note: `Action.AssignSupplier` is used as an aria-label only — add it to both resx files (en: "Assign supplier" / zh: "分配供应商"). If you prefer to avoid a new key, reuse the existing `Table.Supplier` text instead; either is acceptable. **Use `Action.AssignSupplier` and add the key** to keep the label semantically correct. Add to en resx (before `</root>`):

```xml
  <data name="Action.AssignSupplier" xml:space="preserve"><value>Assign supplier</value></data>
```

Add to zh-CN resx (before `</root>`):

```xml
  <data name="Action.AssignSupplier" xml:space="preserve"><value>分配供应商</value></data>
```

- [ ] **Step 3: Add the inline-edit script**

In the same file, the `@section Scripts` block already contains a `<script>` for copy-link (lines 116-135). Append a second `<script>` block after the existing one (still inside `@section Scripts`), scoped per-cell via `closest('td')`:

```html
    <script>
        document.querySelectorAll('.supplier-edit').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var td = btn.closest('td');
                td.querySelector('.supplier-display').classList.add('d-none');
                td.querySelector('.supplier-form').classList.remove('d-none');
            });
        });
        document.querySelectorAll('.supplier-cancel').forEach(function (btn) {
            btn.addEventListener('click', function () {
                var td = btn.closest('td');
                td.querySelector('.supplier-form').classList.add('d-none');
                td.querySelector('.supplier-display').classList.remove('d-none');
            });
        });
    </script>
```

- [ ] **Step 4: Verify build**

Run: `dotnet build src/WebMail/WebMail.csproj`
Expected: build succeeds.

- [ ] **Step 5: Run the full test suite to confirm nothing regressed**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj`
Expected: all tests PASS (no behavioral change to existing tests; Razor compile is covered by build).

- [ ] **Step 6: Manual smoke test (optional but recommended)**

Run the app, log in as admin, open `/Admin/Buyers`, confirm: "供应商" column shows `—` for an unassigned buyer; clicking ✎ turns the cell into a dropdown; selecting a supplier + Save persists (refresh still shows the name); selecting "未分配" + Save clears it. Then log in as that supplier and confirm the buyer (if also Approved+Authorized) now appears in `/Supplier/Buyers`.

- [ ] **Step 7: Commit** *(skip — user does not commit; leave in working tree)*

---

## Self-Review Notes

- **Spec coverage:** Spec §1 (handler/upsert/audit) → Task 2 Step 3. §2 (visibility unchanged) → no code; documented in plan intro. §3 (page model props + load) → Task 2 Step 3 `LoadAsync`. §4 (column + inline edit) → Task 3. §5 (localization) → Task 1 (+ `Action.AssignSupplier` in Task 3 Step 2). §6 (tests) → Task 2 Step 1 (8 tests covering create/update/unassign/noop/non-supplier/inactive/deleted-buyer/load-map).
- **Type consistency:** `SupplierOption(long Id, string DisplayName)` and `SupplierAssignmentView(long? SupplierId, string DisplayName)` defined in Task 2 and consumed in Task 3 with matching property names. Handler signature `OnPostAssignSupplierAsync(long buyerId, long? supplierId)` matches the form's `name="buyerId"` / `name="supplierId"` and `asp-page-handler="AssignSupplier"`.
- **Placeholders:** none — every code step shows full code.
- **Note on `a` nullability in Task 3 Step 2:** the `@{ }` block reads `a` via `TryGetValue` with `out var a`; `a` may be null, so the display-name fallback uses `a?.DisplayName`. The `if (a is not null && a.SupplierId == s.Id)` guards the `selected` option. This compiles under Razor's nullable context.
