# Junk Mail Sync Design

**Date:** 2026-06-26
**Status:** Approved (design)

## Goal

Capture supplier emails that the provider misclassified into the **Junk/Spam** folder, in addition to the Inbox, so suppliers don't miss legitimate correspondence that was auto-filed as spam.

This requires building out the mail-fetch pipeline that is currently only stubbed, because no part of "scheduled fetch → store → display" is wired end-to-end today.

## Verified Current State (pre-work)

- `GmailProvider` is a full stub: both `CompleteAuthorizationAsync` and `FetchMessagesAsync` throw `NotImplementedException`.
- `OutlookProvider.FetchMessagesAsync` is implemented, **but nothing calls it.** A repo-wide search finds `FetchMessagesAsync` only in the interface, the two providers, and a test fake.
- **No `SyncJob` consumer exists.** `MailSyncJobQueueService` only *enqueues* `SyncJob` rows; no worker dequeues them to fetch and persist messages.
- Tokens are stored unencrypted: `EmailAccount.EncryptedRefreshToken` holds the raw refresh token (OAuth callback assigns it directly).
- DB initialization is deferred (`Program.cs` warns migrations/EnsureCreated are not set up), so there is no live schema to migrate — adding a column has no historical-data impact.

Conclusion: scanning the Junk folder is a small addition on top of a fetch pipeline that must first be made to work end-to-end for both providers.

## Why Junk Is Simpler Than Drafts (rejected premise)

The request was first understood as "Drafts", which would have been complex: drafts are authored by the buyer (not an allowed sender), so they would need a recipient-based filter, draft-id keying, and `SentAt`-from-last-modified handling because drafts are edited repeatedly.

Junk/Spam messages are **received messages from the supplier (the allowed sender)**. They carry a stable message id, a real sender, and a real received time. The existing `from: <allowed sender>` filter applies directly, and they map onto the existing `ProviderMessage` model unchanged. None of the draft-specific complexity is needed.

## Scope

In scope (all together, per user decision):

1. `MailSyncProcessor` — the missing `SyncJob` consumer.
2. Gmail full fetch pipeline (OAuth code exchange + message fetch + MIME body parsing).
3. Both providers fetch Inbox **and** Junk, filtered by allowed senders.
4. `EmailMessage.Folder` field + Junk labeling on the supplier mail page.

Out of scope (explicit deferrals):

- Token encryption — keep current plaintext pass-through (consistent with the original MVP plan's deferral).
- Incremental/delta sync — v1 uses a fixed `InitialSyncDays` time window.
- Trash (deleted items) and any folders other than Inbox + Junk.
- DB migrations — DB init is already deferred; the new `Folder` column is picked up whenever EnsureCreated/migrations are added.

## Design

### 1. Data model

`Domain/Enums.cs`:

```csharp
public enum MailFolder { Inbox = 1, Junk = 2 }
```

`Domain/Entities.cs` — `EmailMessage` gains:

```csharp
public MailFolder Folder { get; set; } = MailFolder.Inbox;
```

Dedupe / persistence:

- Junk messages are ordinary received messages with stable provider message ids. Keep the existing unique index `(EmailAccountId, ProviderMessageId)`.
- Persistence is upsert: insert when `(EmailAccountId, ProviderMessageId)` is absent; otherwise update mutable fields including `Folder`. Updating `Folder` covers the case where a user marks a message "not spam" and it moves Junk → Inbox on a later sync.

### 2. Provider interface (Approach A: single method + Folder tag)

`IEmailProvider.FetchMessagesAsync` signature changes:

```csharp
Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(
    string refreshToken,
    IReadOnlyCollection<string> allowedSenders,
    DateTimeOffset? since,
    CancellationToken cancellationToken);
```

`ProviderMessage` gains `MailFolder Folder`.

Each provider builds its own folder/sender queries internally (the Gmail-style query string is no longer passed across the boundary; `MailSyncPlanner.BuildGmailSenderQuery` is no longer used by the fetch path):

- **Outlook** — query `/me/mailFolders/inbox/messages` and `/me/mailFolders/junkemail/messages` separately, each with the `from` + `since` filter, tag each result's `Folder`, and return the merged list.
- **Gmail** — `messages.list` with `includeSpamTrash=true` and `q=(from:a OR from:b) after:<since>`; for each message `messages.get` and read `labelIds` → `SPAM` ⇒ `Junk`, otherwise `Inbox`.

### 3. Worker / persistence

New plain, unit-testable class `MailSyncProcessor`:

```
ProcessPendingAsync(db, now, ct):
  for each pending SyncJob:
    load EmailAccount + AllowedSenders
    resolve provider
    mark job Running
    messages = provider.FetchMessagesAsync(refreshToken, allowedSenders, since, ct)
    upsert EmailMessage rows (insert if absent; else update incl. Folder)
    mark job Succeeded; on exception mark Failed and record Error
```

`MailSyncBackgroundService` tick order: `QueueActiveWindowJobsAsync` (existing) → `ProcessPendingAsync` (new).

`since = now - MailSync:InitialSyncDays` (fixed window for v1).

Register `MailSyncProcessor` in DI (Scoped).

### 4. Gmail full pipeline

- `CompleteAuthorizationAsync`: exchange `code` at the Google token endpoint for access + refresh tokens, fetch userinfo for the email address, return `OAuthCallbackResult`. Requires `GoogleOAuth:ClientSecret`.
- `FetchMessagesAsync`: use the already-referenced `Google.Apis.Gmail.v1` SDK, building credentials from the stored refresh token + client id/secret. Parse MIME by walking `payload.parts`, extracting `text/plain` and `text/html` (base64url decode).

### 5. UI + DI + tests

- `Pages/Supplier/Mail.cshtml`: add a "来源" (source) column; Junk messages show a `垃圾邮件` badge, Inbox messages show nothing.
- `Program.cs`: register `MailSyncProcessor`.
- Tests:
  - Update the fake provider in `EmailProviderTests` to the new signature.
  - Add `MailSyncProcessor` tests (in-memory DB + fake provider) covering upsert behavior, `Folder` assignment, and job status transitions (Succeeded / Failed).

## Components & Boundaries

- `IEmailProvider` / `GmailProvider` / `OutlookProvider`: own provider-specific query syntax and folder→`MailFolder` mapping. Input: refresh token, allowed senders, since. Output: `ProviderMessage[]` tagged with `Folder`.
- `MailSyncProcessor`: owns job lifecycle and upsert persistence. Depends on `WebMailDbContext`, `IEmailProviderResolver`. No provider-specific knowledge.
- `MailSyncBackgroundService`: scheduling only; delegates to the queue service and the processor.

## Data Flow

active window → `MailSyncJobQueueService` enqueues `SyncJob` → `MailSyncProcessor` dequeues → provider fetches Inbox + Junk filtered by allowed senders → upsert `EmailMessage` (with `Folder`) → supplier mail page lists messages, Junk badged.

## Error Handling

- Per-job try/catch: a failing job is marked `Failed` with `Error`; other jobs continue.
- Provider/network errors surface as job failures, not crashes of the background loop (the existing tick already wraps work in try/catch).
