# Buyer → Supplier Manual Assignment — Design

Date: 2026-06-30
Status: Draft

## Problem

A buyer can reach `EmailStatus = Authorized`, `Stage = Submitted`, `ReviewStatus = Approved`,
yet no supplier sees it in their buyer list. Root cause:

- The supplier buyer list (`src/WebMail/Pages/Supplier/Buyers.cshtml.cs:83-90`) is sourced
  from `BuyerSupplierAssignments`, not from `Buyers` directly. It requires a row
  `BuyerId == buyer && SupplierId == supplier` to exist.
- No production code ever inserts a `BuyerSupplierAssignment`. `BuyerSupplierAssignments.Add`
  appears only in `tests/` and `docs/`. `CardKeyService` (card generation) and
  `Admin/Buyers` (review) never create assignments.
- `Buyer.SupplierStatus` (default `Unprocessed`) — the "供应商：未处理" the user sees — is a
  status flag on `Buyer` itself, unrelated to `BuyerSupplierAssignment`. It does **not** imply
  an assignment exists.

Result: buyers meet all visibility conditions but have no assignment row, so suppliers see an
empty list.

## Goal

Let an administrator manually assign (and re-assign) a buyer to a supplier, creating the
`BuyerSupplierAssignment` row that makes the buyer visible to that supplier.

## Decisions (from brainstorming)

1. **Timing:** Assignable at any `BuyerStage`; re-assignment allowed. Decoupled from review
   state. (The supplier list still gates on `Approved + Authorized`, so early assignment only
   pre-creates the row — the buyer stays invisible until those conditions hold. Intended.)
2. **Admin list:** Add a "供应商" column showing the assigned supplier's name (or `—`); no
   filter column.
3. **UI:** Inline edit. The column shows the supplier name + a pencil edit button. Clicking
   edit transforms the cell in place into a `<select>` (active suppliers + an "未分配" option)
   with submit/cancel. Cancel restores display state.
4. **Clearing allowed:** The dropdown includes "未分配" (null). Selecting it removes the
   assignment row.

## Data Model

`BuyerSupplierAssignment` (`Domain/Entities.cs:67`) is unchanged — it already has
`BuyerId`, `SupplierId`, navigation `Buyer`, and `CreatedAt`. `BuyerId` has a unique index
(`WebMailDbContext.cs:28`), so each buyer has at most one assignment. Assignment = upsert on
`BuyerId`; unassign = delete the row.

No new entity. No migration (table already exists).

## Components

### 1. Page model — `Pages/Admin/Buyers.cshtml.cs`

New handler:

```
public async Task<IActionResult> OnPostAssignSupplierAsync(long buyerId, long? supplierId)
```

Behavior:
- Load `buyer` by `Id && !IsDeleted`. If missing → `Message = Admin.Buyers.AssignFailed`,
  reload, return Page.
- If `supplierId` is not null: validate the target user exists with
  `Role == Supplier && IsActive`. If invalid → `Admin.Buyers.AssignFailed`, reload, return Page.
- Upsert: load existing assignment by `BuyerId`.
  - `supplierId` not null + no row → `Add(new BuyerSupplierAssignment { BuyerId, SupplierId })`.
  - `supplierId` not null + row exists → `row.SupplierId = supplierId`.
  - `supplierId` null + row exists → `Remove(row)`.
  - `supplierId` null + no row → no-op.
- `AuditLog`: `Action = "AdminAssignSupplier"`, `UserId = adminId`,
  `Details = $"buyer={buyerId};supplier={supplierId}"`.
- `SaveChangesAsync()`. `Message = Admin.Buyers.Assigned`.
- `await LoadAsync(); return Page();`.

New view-model types (records, in the page model file):

```
public sealed record SupplierOption(long Id, string DisplayName);
public sealed record SupplierAssignmentView(long? SupplierId, string DisplayName); // DisplayName = "—"/empty when null
```

`LoadAsync` additions:
- `Suppliers` (property): all `Role == Supplier && IsActive` users, ordered by `DisplayName`,
  projected to `SupplierOption`.
- `AssignmentByBuyer` (property): `Dictionary<long, SupplierAssignmentView>`. For the current
  page's buyers, left-join `BuyerSupplierAssignments` → `Users` to fetch
  `(SupplierId, DisplayName)`. Missing assignment → `(null, "")`.

Both are loaded in a single `LoadAsync` so GET and every POST path returns a consistent page.

### 2. Razor — `Pages/Admin/Buyers.cshtml`

- Table header: add `<th>@L["Table.Supplier"]</th>` adjacent to the SupplierStatus column.
- Per-row cell contains two siblings toggled by JS:
  - Display: `<span class="supplier-display">@name</span>` (or `—`) +
    `<button type="button" class="btn btn-sm btn-link p-0 supplier-edit">✎</button>`.
  - Form (hidden by default):
    ```
    <form method="post" asp-page-handler="AssignSupplier" class="supplier-form d-none">
        <input type="hidden" name="buyerId" value="@buyer.Id" />
        <select name="supplierId" class="form-select form-select-sm">
            <option value="">@L["Action.Unassigned"]</option>
            @foreach (var s in Model.Suppliers) {
                <option value="@s.Id" selected="@(current?.SupplierId == s.Id)">@s.DisplayName</option>
            }
        </select>
        <button type="submit" class="btn btn-sm btn-primary">@L["Common.Save"]</button>
        <button type="button" class="btn btn-sm btn-outline-secondary supplier-cancel">@L["Common.Cancel"]</button>
    </form>
    ```
- `current` = `Model.AssignmentByBuyer.TryGetValue(buyer.Id, out var v) ? v : null`.

### 3. Script (inline `@section Scripts`)

Vanilla JS, mirroring the existing copy-link pattern:
- On `.supplier-edit` click within a row: hide that cell's `.supplier-display` + edit button,
  show `.supplier-form`.
- On `.supplier-cancel` click: reverse — hide form, show display.

Scope each handler to its row's cell (closest `td`) so multiple rows don't interfere.

### 4. Localization — `Resources/SharedResource.{en,zh-CN}.resx`

New keys (en / zh-CN):
- `Table.Supplier` — "Supplier" / "供应商"
- `Action.Unassigned` — "Unassigned" / "未分配"
- `Common.Save` — "Save" / "保存" (add if missing)
- `Common.Cancel` — "Cancel" / "取消" (add if missing)
- `Admin.Buyers.Assigned` — "Supplier assigned." / "已分配供应商。"
- `Admin.Buyers.AssignFailed` — "Assignment failed." / "分配失败。"

### 5. Tests — `tests/WebMail.Tests/AdminBuyersModelTests.cs`

Following existing test patterns (in-memory `WebMailDbContext`, admin claims):
- Assign new supplier → assignment row created, `AuditLog` "AdminAssignSupplier" written,
  `Message` = assigned key, `AssignmentByBuyer` reflects it on reload.
- Re-assign existing → same row's `SupplierId` updates (count stays 1).
- `supplierId = null` → row removed; `supplierId = null` with no row → no-op, no throw.
- `supplierId` pointing to a non-Supplier / inactive / missing user → `AssignFailed`, no row
  written.
- `buyerId` for a soft-deleted buyer → `AssignFailed`.
- GET `OnGetAsync` → `Suppliers` lists only active Suppliers; `AssignmentByBuyer` populates
  assigned names and leaves missing as `(null, "")`.

## Out of Scope

- Auto-assignment on approval (decision #1: manual only).
- Supplier-side "unassign" or self-service — supplier never writes assignments.
- Bulk assignment / CSV.
- New migration or entity.
- Changes to supplier list query filters (already correct; just lacked data).

## Risk / Notes

- `BuyerId` unique index means concurrent upsert races are possible but unlikely in a
  single-admin UI; the load-then-modify pattern matches existing handlers
  (`Admin/Buyers.cshtml.cs:ReviewAsync`).
- Suppliers dropdown scope = active suppliers only, so disabling a supplier later leaves a
  stale assignment to a disabled user. Acceptable: the supplier simply can't log in. Out of
  scope to auto-clean.
