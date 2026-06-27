# Buyer / Supplier Status-Driven Permissions Design

**Date:** 2026-06-27
**Status:** Draft (design)

## Goal

Make what a **buyer** may do, and what a **sales** user may delete, depend on where the buyer sits in the review + supplier-processing lifecycle. Concretely:

- When a supplier marks a buyer **Failed**, the buyer may **change email** (re-bind a different mailbox).
- When a supplier marks a buyer **Completed**, the buyer may **only clear authorization**; once cleared, this is terminal (no re-binding).
- A **sales** user may delete buyers that are Failed or Completed, plus buyers still in early, un-locked states; they may not delete buyers that are locked (approved, supplier still processing) or whose mailbox is in an abnormal (broken-token) state.
- This requires building the supplier **"set status"** action, which does not exist today.

## Core Decision: Three Independent Status Fields

The current `EmailAuthorizationStatus { NotAuthorized, PendingReview, Normal, Rejected, Abnormal }` conflates **three different facts** into one field:

- `NotAuthorized` / authorized / `Abnormal` — about the **mailbox** (is it authorized; is the token still valid).
- `PendingReview` / `Normal` / `Rejected` — about the **administrator reviewing the buyer**, which has nothing to do with the mailbox.
- (supplier processing already lives separately in `SupplierStatus`.)

Mixing them is what forced the awkward "remember the previous status" workaround for token failures: when a token broke, `Abnormal` overwrote the review state, losing it.

We split these into **three independent, single-owner status fields**. They store three *different* facts, so this is not redundant state — each is the single source of truth for its own concern, and the buyer's review position is never disturbed by a mailbox problem.

| Field | Owner | Concern | Values |
|---|---|---|---|
| **`EmailStatus`** (邮箱状态) | buyer (authorize) + system (token health) | mailbox authorization & health | `NotAuthorized` / `Authorized` / `Abnormal` |
| **`BuyerStatus`** (买家状态) | administrator (review) | review of the buyer | `NotSubmitted` / `PendingReview` / `Approved` / `Rejected` |
| **`SupplierStatus`** (供应商状态) | supplier | processing decision | `Unprocessed` / `Failed` / `Completed` |

Because mailbox health is now its own field, a broken token only sets `EmailStatus = Abnormal`; `BuyerStatus` (`PendingReview` or `Approved`) is untouched, and re-authorizing the same mailbox simply sets `EmailStatus = Authorized` again. **No `PreAbnormalEmailStatus` field is needed.** The earlier "待审核 buyer whose token breaks" case is correct by construction: its `BuyerStatus` stays `PendingReview` throughout.

"What the buyer/sales/supplier may do" is **computed** from these three fields (see rules below); it is not a fourth stored status.

## Verified Current State (pre-work)

- `Buyer` (`Domain/Entities.cs`) currently has `CardStatus`, `EmailStatus` (`EmailAuthorizationStatus`), `SupplierStatus` (`SupplierProcessingStatus`), `IsDeleted`.
- `SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }` is **displayed read-only**; no handler lets a supplier set it.
- The old `EmailAuthorizationStatus.Abnormal` is **never assigned anywhere in source** — defined in the enum, referenced only in a test. Splitting the enum is therefore low-risk.
- The administrator **review** transition (`PendingReview → Normal/Rejected`) is **also unimplemented** — `Admin/Buyers.cshtml` is a read-only list and nothing sets `Normal`. Because `BuyerStatus = Approved` is the precondition for the supplier action, a minimal admin **approve/reject** handler is included in scope (see below).
- `BuyerRuleService.cs` rules currently key off the old `EmailStatus` only.
- Buyer's only mailbox action today is **解绑邮箱 (unlink)** (`OnPostUnlinkAsync`). Email change happens implicitly via OAuth re-auth in `OAuth/Callback.cshtml.cs`.
- DB init is deferred (no live schema/migrations), so the enum split and new field have no historical-data impact.

## Enum Changes

Replace the single combined enum with two:

```csharp
// 邮箱状态 — mailbox authorization & health
public enum EmailAuthorizationStatus { NotAuthorized = 1, Authorized = 2, Abnormal = 3 }

// 买家状态 — administrator review of the buyer
public enum BuyerStatus { NotSubmitted = 1, PendingReview = 2, Approved = 3, Rejected = 4 }

// 供应商状态 — unchanged
public enum SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }
```

`Buyer` gains a `BuyerStatus BuyerStatus` property; `EmailStatus` keeps its name with the new value set. Old-value mapping:

| Old `EmailAuthorizationStatus` | New `EmailStatus` | New `BuyerStatus` |
|---|---|---|
| NotAuthorized | NotAuthorized | NotSubmitted |
| PendingReview | Authorized | PendingReview |
| Normal | Authorized | Approved |
| Rejected | Authorized | Rejected |
| Abnormal | Abnormal | (preserved, e.g. PendingReview / Approved) |

## Lifecycle Transitions

- **Card created:** `EmailStatus=NotAuthorized`, `BuyerStatus=NotSubmitted`, `SupplierStatus=Unprocessed`.
- **Buyer authorizes a mailbox (OAuth success, new binding):** `EmailStatus=Authorized`, `BuyerStatus=PendingReview`.
- **Admin reviews:** approve → `BuyerStatus=Approved`; reject → `BuyerStatus=Rejected`.
- **Supplier (only when `Approved` + `Authorized`) sets:** `SupplierStatus=Failed` or `Completed`; may switch between the two.
- **Token breaks (future detector):** `EmailStatus=Abnormal` (other two fields untouched).

## Buyer Actions (computed)

`ResolveBuyerMailAction(buyer)` returns the allowed action set:

| Condition | Buyer may | Backend behavior |
|---|---|---|
| `EmailStatus=NotAuthorized` & `BuyerStatus=NotSubmitted` | **Authorize** | provider buttons → OAuth → `Authorized` + `PendingReview` |
| `EmailStatus=Authorized` & `BuyerStatus=PendingReview` | **Change email** / **Clear auth** (审核前可操作) | as below |
| `EmailStatus=Authorized` & `BuyerStatus=Rejected` | **Change email** / **Clear auth** | as below |
| `EmailStatus=Abnormal` | **Re-authorize (same mailbox)** or **Change email** | see below |
| `BuyerStatus=Approved` & `SupplierStatus=Unprocessed` | **nothing (locked)** | "审核已通过，处理中"; no buttons |
| `BuyerStatus=Approved` & `SupplierStatus=Failed` | **Change email** | as below |
| `BuyerStatus=Approved` & `SupplierStatus=Completed` & `EmailStatus=Authorized` | **Clear auth** | as below |
| `BuyerStatus=Approved` & `SupplierStatus=Completed` & `EmailStatus=NotAuthorized` | nothing (terminal "已完成并清空") | — |

### Change email

Remove the current `EmailAccount`; set `EmailStatus=NotAuthorized`, `BuyerStatus=NotSubmitted`, `SupplierStatus=Unprocessed`; preserve `EmailMessage` rows. Redirect to the Email page (now shows provider buttons) → normal OAuth → `Authorized` + `PendingReview`. Because the account is removed before re-auth, the OAuth callback replacement guard is never hit.

### Clear authorization

Remove the current `EmailAccount`; set `EmailStatus=NotAuthorized`; preserve messages. Two cases differ only in what was already there:

- From `Approved` + `Completed`: `BuyerStatus` stays `Approved`, `SupplierStatus` stays `Completed` → derived terminal "已完成并清空"; page offers no authorize/change entry.
- From `PendingReview` / `Rejected` (pre-review manage): also reset `BuyerStatus=NotSubmitted`, `SupplierStatus=Unprocessed` → back to start; buyer may authorize again.

### Re-authorize (same mailbox) — Abnormal recovery

Re-run OAuth for the existing provider+email, refreshing tokens **without** clearing the binding. On success `EmailStatus=Authorized`; `BuyerStatus` and `SupplierStatus` are untouched, so the buyer returns to exactly where it was (`PendingReview`→`PendingReview`, `Approved`→`Approved`, including any `SupplierStatus`). Whatever future code sets `Abnormal` only needs to flip `EmailStatus`.

## Supplier Set Status (new)

On `Pages/Supplier/Buyers.cshtml`, each row gains **标记失败** / **标记完成** POST actions.

- `CanSupplierSetStatus`: buyer assigned to the current supplier, `BuyerStatus=Approved`, `EmailStatus=Authorized`, not deleted.
- Sets `SupplierStatus` to `Failed`/`Completed`; may switch between the two while still eligible.
- Writes an `AuditLog` row.

## Sales Delete Rule (changed)

```
CanSalesDeleteBuyer(buyer, salesUserId) =
    !IsDeleted
 && SaleId == salesUserId
 && EmailStatus != Abnormal
 && !(BuyerStatus == Approved && SupplierStatus == Unprocessed)   // not locked/processing
```

Deletable: `NotSubmitted`, `PendingReview`, `Rejected`, and `Approved`+`Failed`/`Completed` (incl. the cleared-terminal). Blocked: `Approved`+`Unprocessed` (processing) and any `Abnormal`. Soft delete (`IsDeleted = true`).

## Supplier View Rule (updated)

`CanSupplierViewBuyer`: assigned to this supplier, not deleted, `BuyerStatus=Approved`, `EmailStatus=Authorized`. (Replaces the old `EmailStatus==Normal` check.)

## Admin Review (minimal, in scope — prerequisite for Approved)

On `Pages/Admin/Buyers.cshtml`, add **通过** / **拒绝** POST actions for buyers in `PendingReview`, setting `BuyerStatus=Approved`/`Rejected` and writing an `AuditLog`. (Flagged for review: included only because `Approved` is required to reach the supplier flow; can be split out if the user prefers.)

## Code Change Points

- **`Domain/Enums.cs`** — split `EmailAuthorizationStatus`; add `BuyerStatus`.
- **`Domain/Entities.cs`** — add `BuyerStatus BuyerStatus` to `Buyer`.
- **`Services/BuyerRuleService.cs`** — `ResolveBuyerMailAction(Buyer)` (+ `BuyerMailAction` enum `{ None, Authorize, ReAuthorize, ChangeEmail, ClearAuth }`, abnormal yields ReAuthorize+ChangeEmail); rewrite `CanSalesDeleteBuyer`; update `CanSupplierViewBuyer`; add `CanSupplierSetStatus`; optional derived display label.
- **`Pages/Buyer/Email.cshtml(.cs)`** — render by `ResolveBuyerMailAction`; handlers `OnPostChangeEmail`, `OnPostClearAuth`, `OnPostReAuthorize` (start OAuth same account).
- **`Pages/OAuth/Callback.cshtml.cs`** — new binding → `Authorized`+`PendingReview`; same provider+email while `Abnormal` → `Authorized` (recovery, no review reset); same while `Authorized` → token refresh only.
- **`Pages/Supplier/Buyers.cshtml(.cs)`** — 标记失败/完成 + `OnPostSetStatusAsync`; show three statuses.
- **`Pages/Sales/Buyers.cshtml(.cs)`** — delete via new `CanSalesDeleteBuyer`; show three statuses.
- **`Pages/Admin/Buyers.cshtml(.cs)`** — 通过/拒绝 handlers; show three statuses.
- **`Data/WebMailDbContext.cs`** — no relational change beyond the new column/enum mappings.

## Testing

- `BuyerRuleServiceTests`:
  - `ResolveBuyerMailAction` per condition (authorize; pre-review change/clear; rejected change/clear; abnormal → reauth+change; approved+unprocessed locked → none; approved+failed → change; approved+completed → clear; cleared-terminal → none).
  - `CanSalesDeleteBuyer` (deletable set vs blocked Processing / Abnormal; ownership).
  - `CanSupplierSetStatus` / `CanSupplierViewBuyer` (Approved + Authorized + assigned only).
- Page-model tests:
  - Supplier set-status: sets Failed/Completed, switching works, blocked when not assigned / not Approved / not Authorized, audit written.
  - Admin approve/reject: PendingReview → Approved/Rejected, audit written.
  - Buyer change-email: clears account, resets to NotAuthorized+NotSubmitted+Unprocessed, preserves messages.
  - Buyer clear-auth from Completed: clears account, keeps Approved+Completed → terminal, no authorize entry.
  - Abnormal re-auth same mailbox: `EmailStatus` → Authorized, BuyerStatus/SupplierStatus unchanged (covers PendingReview and Approved origins).

## Out of Scope

- Reverting `SupplierStatus` to `Unprocessed`.
- The detector that *sets* `EmailStatus = Abnormal` (token-failure detection). This spec defines only recovery.
- Token encryption, incremental sync, and other prior deferrals remain deferred.
- `CardStatus` is left as-is (entry tracking); not folded into the new fields.
