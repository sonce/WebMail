# Buyer / Supplier Status-Driven Permissions Design

**Date:** 2026-06-27
**Status:** Draft (design)

## Goal

Make what a **buyer** may do, and what a **sales** user may delete, depend on where the buyer sits in the supplier-processing lifecycle. Concretely:

- When a supplier marks a buyer **Failed**, the buyer may **change email** (re-bind a different mailbox).
- When a supplier marks a buyer **Completed**, the buyer may **only clear authorization** — and once cleared, this is terminal (no re-binding).
- A **sales** user may delete buyers that are **Failed** or **Completed**, plus buyers still in early, un-locked states; they may not delete buyers that are locked (admin-approved, supplier still processing).

This also requires building the **supplier "set status" action**, which does not exist today — `SupplierStatus` is currently read-only everywhere.

## Verified Current State (pre-work)

- `Buyer` (`Domain/Entities.cs`) has three stored status fields: `CardStatus`, `EmailStatus` (`EmailAuthorizationStatus`), `SupplierStatus` (`SupplierProcessingStatus`), plus `IsDeleted`.
- `SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }` is **displayed read-only** on the supplier and admin buyer lists. **No handler lets a supplier set it.**
- `EmailAuthorizationStatus { NotAuthorized, PendingReview, Normal, Rejected, Abnormal }`. `Abnormal` is set by the sync pipeline when a token refresh fails / permission is revoked / the provider returns an auth error — i.e. it is only ever reached **from `Normal`**, because sync runs only on `Normal` accounts.
- `BuyerRuleService.cs` keys all current rules off `EmailStatus` only:
  - `CanBuyerUnlink` allows unlink when `EmailStatus ∈ {NotAuthorized, PendingReview, Rejected}`.
  - `CanSalesDeleteBuyer` allows delete when the sales user owns the buyer and `EmailStatus ∈ {NotAuthorized, PendingReview, Rejected}`.
  - `CanSupplierViewBuyer` allows view when `EmailStatus == Normal` and the buyer is assigned to that supplier.
- Buyer's only mailbox action today is **解绑邮箱 (unlink)** on `Pages/Buyer/Email.cshtml`, handled by `OnPostUnlinkAsync`. There is no separate "change email"; changing happens implicitly via OAuth re-auth in `Pages/OAuth/Callback.cshtml.cs`.
- The OAuth callback blocks replacing an existing account unless `CanBuyerUnlink` permits it, and sets `EmailStatus = PendingReview` only for a new/changed account.

## Decision: Buyer Status Is a Derived Property

The buyer's lifecycle stage ("待授权 / 处理中 / 失败 / 完成 / …") is a **single source-of-truth concern**, not a third stored column. It is computed from `(EmailStatus, SupplierStatus, IsDeleted)` because each meaningful combination maps to exactly one buyer status. Storing it would create redundant state that must be re-synced on every email/supplier change and could drift.

`EmailStatus` and `SupplierStatus` remain the two **stored** fields, each with a clear owner:

- `EmailStatus` — owned by the buyer (authorize/clear) and the administrator (approve/reject).
- `SupplierStatus` — owned by the supplier (mark failed/completed).

`BuyerStatus` is exposed as a computed enum used by both the UI (a clear "买家状态" column) and the rule service (to gate actions).

## Buyer Status Derivation

```
ResolveBuyerStatus(buyer):
  IsDeleted                                   -> Deleted
  EmailStatus == Abnormal                     -> Abnormal
  EmailStatus == Normal & Supplier Unprocessed-> Processing      (locked)
  EmailStatus == Normal & Supplier Failed     -> Failed
  EmailStatus == Normal & Supplier Completed  -> Completed
  EmailStatus == NotAuthorized & Completed    -> ClearedTerminal (terminal)
  EmailStatus == NotAuthorized                -> NeedAuthorization
  EmailStatus == PendingReview                -> PendingReview
  EmailStatus == Rejected                     -> Rejected
```

`ClearedTerminal` is uniquely reachable: a supplier can set `Completed` only while `EmailStatus == Normal`, so `(NotAuthorized, Completed)` can arise **only** via the Completed → clear path. The `(NotAuthorized, Failed)` combination never occurs because "change email" resets `SupplierStatus` to `Unprocessed` (see below).

## Buyer Actions Per Status

| BuyerStatus | Buyer may | Backend behavior |
|---|---|---|
| NeedAuthorization | **Authorize** | Show provider buttons → OAuth → `EmailStatus = PendingReview` |
| PendingReview | manage (existing) | Pre-review: existing unlink/re-auth behavior preserved |
| Rejected | **Re-authorize** | Existing manage behavior |
| **Abnormal** | **Re-authorize (same mailbox)** or **Change email** | See Abnormal recovery below |
| **Processing** | **nothing (locked)** | Page shows "审核已通过，处理中"; no action buttons |
| **Failed** | **Change email** | Clear current account → reset to `NotAuthorized` + `Unprocessed` → provider buttons → re-auth → `PendingReview` |
| **Completed** | **Clear authorization** | Remove account → `EmailStatus = NotAuthorized`, **keep `SupplierStatus = Completed`** → becomes `ClearedTerminal` |
| **ClearedTerminal** | nothing | Page shows "已完成，授权已清空"; no authorize entry |
| Deleted | n/a | — |

### Change email (Failed, and Abnormal)

`OnPostChangeEmail`: removes the current `EmailAccount`, sets `EmailStatus = NotAuthorized` and `SupplierStatus = Unprocessed`, preserves `EmailMessage` rows for audit, redirects back to the Email page — which now shows provider buttons. The buyer then runs the normal OAuth flow, ending in `PendingReview`. Because the account is removed *before* re-auth, the existing callback replacement guard is never triggered, so no callback change is needed for this path.

### Clear authorization (Completed)

`OnPostClearAuth`: removes the current `EmailAccount`, sets `EmailStatus = NotAuthorized`, **leaves `SupplierStatus = Completed`**, preserves `EmailMessage` rows. The buyer becomes `ClearedTerminal`; the Email page renders a terminal message and offers **no** authorize/change entry.

### Abnormal recovery

In `Abnormal`, the buyer sees **two** actions:

- **Re-authorize (same mailbox):** re-run OAuth for the existing provider/email, refreshing tokens **without** clearing the binding. On success, `EmailStatus` returns to its pre-Abnormal state. **Invariant:** `Abnormal` is only ever entered from `Normal`, so "return to previous state" is implemented as **restore to `Normal`** — zero schema change. (If `Abnormal` ever becomes reachable from another state, a stored previous-status would be needed; out of scope now.)
- **Change email:** same as the Failed change-email flow (clear → choose provider → re-auth → `PendingReview`).

The OAuth callback gains a small branch: when the current account is `Abnormal` and the completing auth is the **same** provider+email, set `EmailStatus = Normal` (recovery) instead of `PendingReview`.

## Supplier Set Status (new)

On `Pages/Supplier/Buyers.cshtml`, each row gains two POST actions: **标记失败** and **标记完成**.

- Handler validates: buyer is assigned to the current supplier, `EmailStatus == Normal`, not deleted.
- Sets `SupplierStatus` to `Failed` or `Completed`.
- **Can set and can switch** between `Failed` ↔ `Completed` while the buyer is still viewable (`EmailStatus == Normal`, i.e. the buyer has not yet acted). Reverting to `Unprocessed` is out of scope.
- Writes an `AuditLog` row.
- New rule method `CanSupplierSetStatus(buyer, supplierId)` mirrors `CanSupplierViewBuyer`.

## Sales Delete Rule (changed)

`CanSalesDeleteBuyer(buyer, salesUserId)` becomes, expressed via derived status:

```
!IsDeleted
 && SaleId == salesUserId
 && BuyerStatus ∈ { NeedAuthorization, PendingReview, Rejected,
                    Failed, Completed, ClearedTerminal }
```

Equivalently: deletable unless **Processing** (admin-approved, supplier still working → locked) or **Abnormal** (recoverable broken state). Delete remains a soft delete (`IsDeleted = true`).

## Code Change Points

- **`Services/BuyerRuleService.cs`** — core change:
  - Add `BuyerStatus` enum + `ResolveBuyerStatus(Buyer)`.
  - Add `BuyerMailAction` enum `{ None, Authorize, ReAuthorize, ChangeEmail, ClearAuth, Terminal }` + `ResolveBuyerMailAction(Buyer, bool hasAccount)`. (Abnormal yields ReAuthorize + ChangeEmail, represented as a small set/flags.)
  - Rewrite `CanSalesDeleteBuyer` to the new rule.
  - Add `CanSupplierSetStatus(Buyer, long supplierId, long? assignedSupplierId)`.
- **`Pages/Buyer/Email.cshtml(.cs)`** — render buttons by `ResolveBuyerMailAction`; add `OnPostChangeEmail`, `OnPostClearAuth`, and `OnPostReAuthorize` (or reuse start-OAuth) handlers; keep existing pre-review behavior.
- **`Pages/OAuth/Callback.cshtml.cs`** — add the Abnormal same-mailbox recovery branch (→ `Normal`). No change needed for the change-email path.
- **`Pages/Supplier/Buyers.cshtml(.cs)`** — add 标记失败 / 标记完成 forms + `OnPostSetStatusAsync`; show derived buyer status.
- **`Pages/Sales/Buyers.cshtml(.cs)`** — delete goes through new `CanSalesDeleteBuyer`; show derived buyer status.
- **`Pages/Admin/Buyers.cshtml`** — show derived buyer status column (read-only).

## Testing

- `BuyerRuleServiceTests` — extend with:
  - `ResolveBuyerStatus` table covering every `(EmailStatus, SupplierStatus, IsDeleted)` combination → expected `BuyerStatus`.
  - `ResolveBuyerMailAction` per status (locked/processing yields none; failed → change; completed → clear; cleared-terminal → none; abnormal → reauth+change).
  - `CanSalesDeleteBuyer` new rule (deletable set vs locked Processing / Abnormal; ownership check).
  - `CanSupplierSetStatus` (assigned + Normal only).
- Page-model tests:
  - Supplier set-status: success sets `Failed`/`Completed`, switching works, blocked when not assigned / not `Normal`, audit row written.
  - Buyer change-email: clears account, resets to `NotAuthorized` + `Unprocessed`, preserves messages.
  - Buyer clear-auth (Completed): clears account, keeps `SupplierStatus = Completed`, results in `ClearedTerminal`, no authorize entry.
  - Abnormal re-auth same mailbox restores `Normal`; change-email goes to `PendingReview`.
  - Sales delete: allowed for Failed/Completed/ClearedTerminal/early states; blocked for Processing and Abnormal.

## Out of Scope

- Reverting `SupplierStatus` to `Unprocessed`.
- A stored "previous email status" for Abnormal (the Normal-only invariant makes it unnecessary now).
- Token encryption, incremental sync, and other prior deferrals remain deferred.
- `CardStatus` is left as-is (entry tracking); it is not folded into the derived buyer status.
