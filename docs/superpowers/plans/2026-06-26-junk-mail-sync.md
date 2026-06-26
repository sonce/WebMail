# Junk Mail Sync Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fetch supplier emails that landed in the Junk/Spam folder (in addition to the Inbox) for both Gmail and Outlook, by completing the currently-broken fetch pipeline end-to-end.

**Architecture:** Add a `MailFolder` tag to messages, change the provider boundary to take raw allowed-sender addresses and return folder-tagged messages, build the missing `SyncJob` consumer (`MailSyncProcessor`), wire it into the existing background tick, and implement the Gmail OAuth + fetch pipeline. Both providers scan Inbox and Junk with the same `from:<allowed sender>` filter.

**Tech Stack:** .NET 8, ASP.NET Core Razor Pages, EF Core (SQLite prod / InMemory tests), xUnit, Microsoft Graph (raw HTTP), Google.Apis.Gmail.v1 SDK.

## Global Constraints

- Target framework: `net8.0`.
- Tests: xUnit; DB-backed tests use `Microsoft.EntityFrameworkCore.InMemory` with a fresh `Guid`-named database per test (see existing `MailSyncJobQueueServiceTests`).
- Provider config keys: `GoogleOAuth:ClientId`, `GoogleOAuth:ClientSecret`, `GoogleOAuth:RedirectUri`, `OutlookOAuth:*`, `MailSync:InitialSyncDays` (default 30).
- Token encryption is OUT OF SCOPE: `EmailAccount.EncryptedRefreshToken` continues to hold the raw refresh token and is passed through unchanged.
- Junk messages are ordinary received messages from the allowed sender — no draft-style recipient filter or draft-id keying.
- Folders covered: Inbox + Junk only. Trash and others are out of scope.

---

## File Structure

- `src/WebMail/Domain/Enums.cs` — add `MailFolder` enum.
- `src/WebMail/Domain/Entities.cs` — add `EmailMessage.Folder`.
- `src/WebMail/Services/EmailProviders/IEmailProvider.cs` — change `FetchMessagesAsync` signature; add `ProviderMessage.Folder`.
- `src/WebMail/Services/EmailProviders/OutlookProvider.cs` — consume `allowedSenders`; fetch Inbox + Junk folders with folder tagging.
- `src/WebMail/Services/EmailProviders/GmailProvider.cs` — implement OAuth code exchange + Gmail fetch (Inbox + Spam) with MIME parsing.
- `src/WebMail/Services/Background/MailSyncProcessor.cs` — NEW: consume `SyncJob`, fetch, upsert messages, set job status.
- `src/WebMail/Services/Background/MailSyncBackgroundService.cs` — call the processor each tick.
- `src/WebMail/Program.cs` — register `MailSyncProcessor`; register `GmailProvider` via `HttpClient`.
- `src/WebMail/WebMail.csproj` — `InternalsVisibleTo` for testing internal helpers.
- `src/WebMail/Pages/Supplier/Mail.cshtml` — add a source column with a Junk badge.
- `tests/WebMail.Tests/*` — model test, processor tests, provider helper tests; update the existing fake provider signature.

---

## Task 1: Add MailFolder enum and EmailMessage.Folder

**Files:**
- Modify: `src/WebMail/Domain/Enums.cs`
- Modify: `src/WebMail/Domain/Entities.cs:37-52`
- Test: `tests/WebMail.Tests/EmailMessageTests.cs`

**Interfaces:**
- Produces: `enum MailFolder { Inbox = 1, Junk = 2 }`; `EmailMessage.Folder` (type `MailFolder`, default `MailFolder.Inbox`).

- [ ] **Step 1: Write the failing test**

Create `tests/WebMail.Tests/EmailMessageTests.cs`:

```csharp
using WebMail.Domain;
using Xunit;

namespace WebMail.Tests;

public sealed class EmailMessageTests
{
    [Fact]
    public void NewEmailMessageDefaultsToInboxFolder() =>
        Assert.Equal(MailFolder.Inbox, new EmailMessage().Folder);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter EmailMessageTests`
Expected: compile failure — `MailFolder` and `EmailMessage.Folder` do not exist yet.

- [ ] **Step 3: Add the enum**

In `src/WebMail/Domain/Enums.cs`, add:

```csharp
public enum MailFolder { Inbox = 1, Junk = 2 }
```

- [ ] **Step 4: Add the property**

In `src/WebMail/Domain/Entities.cs`, inside `EmailMessage` (after `AttachmentMetadataJson`):

```csharp
    public MailFolder Folder { get; set; } = MailFolder.Inbox;
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter EmailMessageTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/WebMail/Domain/Enums.cs src/WebMail/Domain/Entities.cs tests/WebMail.Tests/EmailMessageTests.cs
git commit -m "feat: add MailFolder and EmailMessage.Folder"
```

---

## Task 2: Change provider boundary to allowed-senders + folder-tagged messages

This is a compile-breaking refactor: it changes the interface and every implementor and fake in the same task so the build stays green. Outlook keeps Inbox-only behavior here (Junk comes in Task 3); Gmail stays a stub with the new signature.

**Files:**
- Modify: `src/WebMail/Services/EmailProviders/IEmailProvider.cs:5-13`
- Modify: `src/WebMail/Services/EmailProviders/OutlookProvider.cs`
- Modify: `src/WebMail/Services/EmailProviders/GmailProvider.cs:18`
- Modify: `tests/WebMail.Tests/EmailProviderTests.cs:63`

**Interfaces:**
- Produces:
  - `record ProviderMessage(string ProviderMessageId, string? ProviderThreadId, string Sender, string Recipients, string Subject, DateTimeOffset SentAt, string? TextBody, string? HtmlBody, string? AttachmentMetadataJson, MailFolder Folder)`
  - `Task<IReadOnlyList<ProviderMessage>> IEmailProvider.FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken)`

- [ ] **Step 1: Update the contract**

Replace lines 5 and 12 of `src/WebMail/Services/EmailProviders/IEmailProvider.cs`:

```csharp
public sealed record ProviderMessage(string ProviderMessageId, string? ProviderThreadId, string Sender, string Recipients, string Subject, DateTimeOffset SentAt, string? TextBody, string? HtmlBody, string? AttachmentMetadataJson, WebMail.Domain.MailFolder Folder);
```

```csharp
    Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken);
```

- [ ] **Step 2: Update OutlookProvider to consume allowedSenders and tag folder**

In `src/WebMail/Services/EmailProviders/OutlookProvider.cs`:

Add `using WebMail.Domain;` at the top.

Replace `FetchMessagesAsync` (lines 67-94) signature and body to accept `allowedSenders`:

```csharp
    public async Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = RequiredConfig("OutlookOAuth:ClientId"),
            ["client_secret"] = RequiredConfig("OutlookOAuth:ClientSecret"),
            ["refresh_token"] = refreshToken,
            ["redirect_uri"] = RequiredConfig("OutlookOAuth:RedirectUri"),
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope
        }, cancellationToken);

        var url = BuildMessagesUrl(allowedSenders, since);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!payload.RootElement.TryGetProperty("value", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return messages.EnumerateArray().Select(m => MapMessage(m, MailFolder.Inbox)).ToArray();
    }
```

Replace `BuildMessagesUrl` (lines 96-123) to take `allowedSenders`:

```csharp
    private string BuildMessagesUrl(IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since)
    {
        var filters = new List<string>();
        var senderFilter = BuildSenderFilter(allowedSenders);
        if (!string.IsNullOrWhiteSpace(senderFilter))
        {
            filters.Add(senderFilter);
        }

        if (since is not null)
        {
            filters.Add($"receivedDateTime ge {since.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}");
        }

        var query = new Dictionary<string, string?>
        {
            ["$top"] = "50",
            ["$orderby"] = "receivedDateTime desc",
            ["$select"] = "id,conversationId,from,toRecipients,subject,receivedDateTime,body,hasAttachments"
        };

        if (filters.Count > 0)
        {
            query["$filter"] = string.Join(" and ", filters);
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{GraphEndpoint}/me/messages", query);
    }
```

Replace `BuildSenderFilter` (lines 125-136) to take addresses directly:

```csharp
    private static string BuildSenderFilter(IReadOnlyCollection<string> allowedSenders)
    {
        var senders = allowedSenders
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => $"from/emailAddress/address eq '{x.Replace("'", "''", StringComparison.Ordinal)}'")
            .ToArray();

        return senders.Length == 0 ? string.Empty : $"({string.Join(" or ", senders)})";
    }
```

Replace `MapMessage` (lines 156-175) to accept a folder and pass it through:

```csharp
    private static ProviderMessage MapMessage(JsonElement message, MailFolder folder)
    {
        var body = message.TryGetProperty("body", out var bodyElement) ? bodyElement : default;
        var bodyType = body.ValueKind == JsonValueKind.Object ? ReadString(body, "contentType") : string.Empty;
        var bodyContent = body.ValueKind == JsonValueKind.Object ? ReadString(body, "content") : null;
        var sentAt = DateTimeOffset.TryParse(ReadString(message, "receivedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedSentAt)
            ? parsedSentAt
            : DateTimeOffset.UtcNow;

        return new ProviderMessage(
            ReadString(message, "id") ?? string.Empty,
            ReadString(message, "conversationId"),
            ReadNestedString(message, "from", "emailAddress", "address") ?? string.Empty,
            string.Join(", ", ReadRecipients(message)),
            ReadString(message, "subject") ?? string.Empty,
            sentAt,
            string.Equals(bodyType, "text", StringComparison.OrdinalIgnoreCase) ? bodyContent : null,
            string.Equals(bodyType, "html", StringComparison.OrdinalIgnoreCase) ? bodyContent : null,
            message.TryGetProperty("hasAttachments", out var hasAttachments) && hasAttachments.GetBoolean() ? """{"hasAttachments":true}""" : null,
            folder);
    }
```

- [ ] **Step 3: Update GmailProvider stub signature**

In `src/WebMail/Services/EmailProviders/GmailProvider.cs`, replace line 18:

```csharp
    public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException("Gmail fetch is implemented in a later task.");
```

- [ ] **Step 4: Update the test fake signature**

In `tests/WebMail.Tests/EmailProviderTests.cs`, replace line 63:

```csharp
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException();
```

- [ ] **Step 5: Build and run existing provider tests**

Run: `dotnet build WebMail.sln`
Expected: build succeeds.
Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter EmailProviderTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/WebMail/Services/EmailProviders/IEmailProvider.cs src/WebMail/Services/EmailProviders/OutlookProvider.cs src/WebMail/Services/EmailProviders/GmailProvider.cs tests/WebMail.Tests/EmailProviderTests.cs
git commit -m "refactor: provider fetch takes allowed senders and tags folder"
```

---

## Task 3: Outlook fetches Inbox + Junk folders

**Files:**
- Modify: `src/WebMail/Services/EmailProviders/OutlookProvider.cs`
- Modify: `src/WebMail/WebMail.csproj`
- Test: `tests/WebMail.Tests/OutlookProviderTests.cs`

**Interfaces:**
- Produces: `internal static string OutlookProvider.BuildFolderMessagesUrl(string folder, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since)` returning a Graph mailFolders URL.

- [ ] **Step 1: Expose internals to the test project**

In `src/WebMail/WebMail.csproj`, inside the top-level `<Project>` add a new item group:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="WebMail.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

Create `tests/WebMail.Tests/OutlookProviderTests.cs`:

```csharp
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class OutlookProviderTests
{
    [Fact]
    public void BuildFolderMessagesUrlTargetsJunkFolderWithSenderFilter()
    {
        var url = OutlookProvider.BuildFolderMessagesUrl("junkemail", ["orders@example.com"], null);
        var decoded = Uri.UnescapeDataString(url);

        Assert.Contains("/me/mailFolders/junkemail/messages", url);
        Assert.Contains("from/emailAddress/address eq 'orders@example.com'", decoded);
    }

    [Fact]
    public void BuildFolderMessagesUrlTargetsInboxFolder()
    {
        var url = OutlookProvider.BuildFolderMessagesUrl("inbox", [], null);
        Assert.Contains("/me/mailFolders/inbox/messages", url);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter OutlookProviderTests`
Expected: compile failure — `BuildFolderMessagesUrl` does not exist.

- [ ] **Step 4: Implement folder-scoped fetch**

In `src/WebMail/Services/EmailProviders/OutlookProvider.cs`, replace `FetchMessagesAsync` body to loop over both folders:

```csharp
    public async Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = RequiredConfig("OutlookOAuth:ClientId"),
            ["client_secret"] = RequiredConfig("OutlookOAuth:ClientSecret"),
            ["refresh_token"] = refreshToken,
            ["redirect_uri"] = RequiredConfig("OutlookOAuth:RedirectUri"),
            ["grant_type"] = "refresh_token",
            ["scope"] = Scope
        }, cancellationToken);

        var folders = new[] { ("inbox", MailFolder.Inbox), ("junkemail", MailFolder.Junk) };
        var results = new List<ProviderMessage>();
        foreach (var (folder, mailFolder) in folders)
        {
            results.AddRange(await FetchFolderAsync(token.AccessToken, folder, mailFolder, allowedSenders, since, cancellationToken));
        }

        return results;
    }

    private async Task<IReadOnlyList<ProviderMessage>> FetchFolderAsync(string accessToken, string folder, MailFolder mailFolder, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var url = BuildFolderMessagesUrl(folder, allowedSenders, since);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!payload.RootElement.TryGetProperty("value", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return messages.EnumerateArray().Select(m => MapMessage(m, mailFolder)).ToArray();
    }
```

Then replace the `BuildMessagesUrl` method (the one from Task 2) with `BuildFolderMessagesUrl`:

```csharp
    internal static string BuildFolderMessagesUrl(string folder, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since)
    {
        var filters = new List<string>();
        var senderFilter = BuildSenderFilter(allowedSenders);
        if (!string.IsNullOrWhiteSpace(senderFilter))
        {
            filters.Add(senderFilter);
        }

        if (since is not null)
        {
            filters.Add($"receivedDateTime ge {since.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}");
        }

        var query = new Dictionary<string, string?>
        {
            ["$top"] = "50",
            ["$orderby"] = "receivedDateTime desc",
            ["$select"] = "id,conversationId,from,toRecipients,subject,receivedDateTime,body,hasAttachments"
        };

        if (filters.Count > 0)
        {
            query["$filter"] = string.Join(" and ", filters);
        }

        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{GraphEndpoint}/me/mailFolders/{folder}/messages", query);
    }
```

Note: `BuildFolderMessagesUrl` is `static`, so `GraphEndpoint` (already `const`) is accessible.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter OutlookProviderTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Build the whole solution**

Run: `dotnet build WebMail.sln`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/WebMail/Services/EmailProviders/OutlookProvider.cs src/WebMail/WebMail.csproj tests/WebMail.Tests/OutlookProviderTests.cs
git commit -m "feat: outlook fetches inbox and junk folders"
```

---

## Task 4: MailSyncProcessor consumes SyncJobs and upserts messages

**Files:**
- Create: `src/WebMail/Services/Background/MailSyncProcessor.cs`
- Test: `tests/WebMail.Tests/MailSyncProcessorTests.cs`

**Interfaces:**
- Consumes: `IEmailProviderResolver`, `WebMailDbContext`, `ProviderMessage`.
- Produces: `MailSyncProcessor(IEmailProviderResolver providers, IConfiguration configuration)` with `Task<int> ProcessPendingAsync(WebMailDbContext db, DateTimeOffset now, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/WebMail.Tests/MailSyncProcessorTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.Background;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class MailSyncProcessorTests
{
    [Fact]
    public async Task ProcessPendingStoresMessagesAndMarksJobSucceeded()
    {
        await using var db = CreateDb();
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "Fake");
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeProvider("Fake",
        [
            Message("m-1", MailFolder.Inbox),
            Message("m-2", MailFolder.Junk)
        ]));

        var processed = await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(SyncJobStatus.Succeeded, (await db.SyncJobs.SingleAsync()).Status);
        var stored = await db.EmailMessages.OrderBy(x => x.ProviderMessageId).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(MailFolder.Junk, stored.Single(x => x.ProviderMessageId == "m-2").Folder);
    }

    [Fact]
    public async Task ProcessPendingUpsertsExistingMessageWithoutDuplicating()
    {
        await using var db = CreateDb();
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "Fake");
        db.EmailMessages.Add(new EmailMessage
        {
            BuyerId = 1, EmailAccountId = 1, ProviderMessageId = "m-1",
            Subject = "old", Folder = MailFolder.Junk
        });
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeProvider("Fake",
        [
            Message("m-1", MailFolder.Inbox, subject: "new")
        ]));

        await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        var stored = Assert.Single(await db.EmailMessages.ToListAsync());
        Assert.Equal("new", stored.Subject);
        Assert.Equal(MailFolder.Inbox, stored.Folder);
    }

    [Fact]
    public async Task ProcessPendingMarksJobFailedWhenProviderThrows()
    {
        await using var db = CreateDb();
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "Boom");
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new ThrowingProvider("Boom"));

        await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        var job = await db.SyncJobs.SingleAsync();
        Assert.Equal(SyncJobStatus.Failed, job.Status);
        Assert.Equal("boom", job.Error);
    }

    private static MailSyncProcessor CreateProcessor(IEmailProvider provider)
    {
        var resolver = new EmailProviderResolver([provider]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MailSync:InitialSyncDays"] = "30" })
            .Build();
        return new MailSyncProcessor(resolver, config);
    }

    private static void SeedAccount(WebMailDbContext db, long buyerId, long accountId, string provider)
    {
        db.AllowedSenders.Add(new AllowedSender { EmailAddress = "orders@example.com" });
        db.EmailAccounts.Add(new EmailAccount
        {
            Id = accountId, BuyerId = buyerId, Email = "buyer@example.com",
            Provider = provider, ProviderUserId = "p", EncryptedRefreshToken = "token"
        });
    }

    private static ProviderMessage Message(string id, MailFolder folder, string subject = "s") =>
        new(id, null, "orders@example.com", "buyer@example.com", subject, DateTimeOffset.UtcNow, "body", null, null, folder);

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeProvider(string name, IReadOnlyList<ProviderMessage> messages) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => Task.FromResult(messages);
    }

    private sealed class ThrowingProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter MailSyncProcessorTests`
Expected: compile failure — `MailSyncProcessor` does not exist.

- [ ] **Step 3: Implement the processor**

Create `src/WebMail/Services/Background/MailSyncProcessor.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;

namespace WebMail.Services.Background;

public sealed class MailSyncProcessor(IEmailProviderResolver providers, IConfiguration configuration)
{
    public async Task<int> ProcessPendingAsync(WebMailDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await db.SyncJobs
            .Where(x => x.Status == SyncJobStatus.Pending)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        var days = configuration.GetValue("MailSync:InitialSyncDays", 30);
        var since = now.AddDays(-days);
        var allowedSenders = await db.AllowedSenders.Select(x => x.EmailAddress).ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var job in pending)
        {
            job.Status = SyncJobStatus.Running;
            job.StartedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var account = await db.EmailAccounts.FirstOrDefaultAsync(x => x.BuyerId == job.BuyerId, cancellationToken);
                if (account is not null)
                {
                    var provider = providers.Resolve(account.Provider);
                    var messages = await provider.FetchMessagesAsync(account.EncryptedRefreshToken, allowedSenders, since, cancellationToken);
                    await UpsertMessagesAsync(db, account, messages, cancellationToken);
                }

                job.Status = SyncJobStatus.Succeeded;
                job.CompletedAt = now;
            }
            catch (Exception ex)
            {
                job.Status = SyncJobStatus.Failed;
                job.Error = ex.Message;
                job.CompletedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            processed++;
        }

        return processed;
    }

    private static async Task UpsertMessagesAsync(WebMailDbContext db, EmailAccount account, IReadOnlyList<ProviderMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var m in messages)
        {
            var existing = await db.EmailMessages.FirstOrDefaultAsync(
                x => x.EmailAccountId == account.Id && x.ProviderMessageId == m.ProviderMessageId,
                cancellationToken);

            if (existing is null)
            {
                db.EmailMessages.Add(new EmailMessage
                {
                    BuyerId = account.BuyerId,
                    EmailAccountId = account.Id,
                    ProviderMessageId = m.ProviderMessageId,
                    ProviderThreadId = m.ProviderThreadId,
                    Sender = m.Sender,
                    Recipients = m.Recipients,
                    Subject = m.Subject,
                    SentAt = m.SentAt,
                    TextBody = m.TextBody,
                    HtmlBody = m.HtmlBody,
                    AttachmentMetadataJson = m.AttachmentMetadataJson,
                    Folder = m.Folder
                });
            }
            else
            {
                existing.ProviderThreadId = m.ProviderThreadId;
                existing.Sender = m.Sender;
                existing.Recipients = m.Recipients;
                existing.Subject = m.Subject;
                existing.SentAt = m.SentAt;
                existing.TextBody = m.TextBody;
                existing.HtmlBody = m.HtmlBody;
                existing.AttachmentMetadataJson = m.AttachmentMetadataJson;
                existing.Folder = m.Folder;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter MailSyncProcessorTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/WebMail/Services/Background/MailSyncProcessor.cs tests/WebMail.Tests/MailSyncProcessorTests.cs
git commit -m "feat: add mail sync processor with message upsert"
```

---

## Task 5: Wire the processor into the background tick and DI

**Files:**
- Modify: `src/WebMail/Services/Background/MailSyncBackgroundService.cs`
- Modify: `src/WebMail/Program.cs:22-23`

**Interfaces:**
- Consumes: `MailSyncProcessor` (from Task 4), `MailSyncJobQueueService` (existing).

- [ ] **Step 1: Resolve and call the processor each tick**

Replace `TickAsync` in `src/WebMail/Services/Background/MailSyncBackgroundService.cs` (lines 16-30):

```csharp
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
            var processor = scope.ServiceProvider.GetRequiredService<MailSyncProcessor>();
            var now = DateTimeOffset.UtcNow;
            var queued = await queueService.QueueActiveWindowJobsAsync(db, now, cancellationToken);
            var processed = await processor.ProcessPendingAsync(db, now, cancellationToken);
            logger.LogInformation("Queued {Queued} and processed {Processed} sync jobs", queued, processed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mail sync tick failed");
        }
    }
```

- [ ] **Step 2: Register the processor**

In `src/WebMail/Program.cs`, after line 22 (`builder.Services.AddSingleton<MailSyncJobQueueService>();`) add:

```csharp
builder.Services.AddScoped<MailSyncProcessor>();
```

- [ ] **Step 3: Build the solution**

Run: `dotnet build WebMail.sln`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/WebMail/Services/Background/MailSyncBackgroundService.cs src/WebMail/Program.cs
git commit -m "feat: run mail sync processor in background tick"
```

---

## Task 6: Implement the Gmail pipeline (OAuth exchange + Inbox/Spam fetch)

**Files:**
- Modify: `src/WebMail/Services/EmailProviders/GmailProvider.cs`
- Modify: `src/WebMail/Program.cs:18`
- Test: `tests/WebMail.Tests/GmailProviderTests.cs`

**Interfaces:**
- Consumes: `Google.Apis.Gmail.v1` (`GmailService`, `Message`, `MessagePart`), `HttpClient`.
- Produces:
  - `GmailProvider(IConfiguration configuration, HttpClient httpClient)`
  - `internal static string GmailProvider.BuildGmailQuery(IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since)`
  - `internal static MailFolder GmailProvider.MapFolder(IList<string>? labelIds)`
  - `internal static string? GmailProvider.ExtractBody(Google.Apis.Gmail.v1.Data.MessagePart? payload, string mimeType)`

- [ ] **Step 1: Write the failing tests**

Create `tests/WebMail.Tests/GmailProviderTests.cs`:

```csharp
using Google.Apis.Gmail.v1.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class GmailProviderTests
{
    [Fact]
    public void BuildGmailQueryCombinesSendersAndSinceDate()
    {
        var since = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        var query = GmailProvider.BuildGmailQuery(["a@example.com", "b@example.com"], since);

        Assert.Equal("(from:a@example.com OR from:b@example.com) after:2026/05/27", query);
    }

    [Fact]
    public void MapFolderReturnsJunkWhenSpamLabelPresent() =>
        Assert.Equal(MailFolder.Junk, GmailProvider.MapFolder(["SPAM", "CATEGORY_PERSONAL"]));

    [Fact]
    public void MapFolderReturnsInboxOtherwise() =>
        Assert.Equal(MailFolder.Inbox, GmailProvider.MapFolder(["INBOX"]));

    [Fact]
    public void ExtractBodyFindsNestedPlainText()
    {
        // "hello" base64url-encoded is "aGVsbG8".
        var payload = new MessagePart
        {
            MimeType = "multipart/alternative",
            Parts =
            [
                new MessagePart { MimeType = "text/plain", Body = new MessagePartBody { Data = "aGVsbG8" } }
            ]
        };

        Assert.Equal("hello", GmailProvider.ExtractBody(payload, "text/plain"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter GmailProviderTests`
Expected: compile failure — the new members do not exist.

- [ ] **Step 3: Implement GmailProvider**

Replace the entire `src/WebMail/Services/EmailProviders/GmailProvider.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using WebMail.Domain;

namespace WebMail.Services.EmailProviders;

public sealed class GmailProvider(IConfiguration configuration, HttpClient httpClient) : IEmailProvider
{
    public string Name => "Gmail";

    public OAuthStartResult BuildAuthorizationUrl(string cardNo)
    {
        var clientId = configuration["GoogleOAuth:ClientId"] ?? string.Empty;
        var redirectUri = Uri.EscapeDataString(configuration["GoogleOAuth:RedirectUri"] ?? string.Empty);
        var state = cardNo;
        var scope = Uri.EscapeDataString("https://www.googleapis.com/auth/gmail.readonly https://www.googleapis.com/auth/userinfo.email");
        var url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={clientId}&redirect_uri={redirectUri}&response_type=code&scope={scope}&access_type=offline&prompt=consent&state={Uri.EscapeDataString(state)}";
        return new OAuthStartResult(url, state);
    }

    public async Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken)
    {
        using var tokenResponse = await httpClient.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = RequiredConfig("GoogleOAuth:ClientId"),
            ["client_secret"] = RequiredConfig("GoogleOAuth:ClientSecret"),
            ["redirect_uri"] = RequiredConfig("GoogleOAuth:RedirectUri"),
            ["grant_type"] = "authorization_code"
        }), cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();

        using var tokenPayload = await JsonDocument.ParseAsync(await tokenResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var tokenRoot = tokenPayload.RootElement;
        var accessToken = tokenRoot.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Google token response missing access_token.");
        var refreshToken = tokenRoot.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Google token response missing refresh_token.");
        }

        using var userRequest = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userResponse = await httpClient.SendAsync(userRequest, cancellationToken);
        userResponse.EnsureSuccessStatusCode();

        using var userPayload = await JsonDocument.ParseAsync(await userResponse.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var userRoot = userPayload.RootElement;
        var email = userRoot.TryGetProperty("email", out var e) ? e.GetString() : null;
        var sub = userRoot.TryGetProperty("sub", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Google userinfo did not return an email.");
        }

        return new OAuthCallbackResult(email, sub ?? string.Empty, refreshToken);
    }

    public async Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken)
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

    private GmailService CreateService(string refreshToken)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = RequiredConfig("GoogleOAuth:ClientId"),
                ClientSecret = RequiredConfig("GoogleOAuth:ClientSecret")
            }
        });
        var credential = new UserCredential(flow, "user", new TokenResponse { RefreshToken = refreshToken });
        return new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "WebMail"
        });
    }

    private static ProviderMessage MapMessage(Message message)
    {
        var headers = message.Payload?.Headers ?? new List<MessagePartHeader>();
        string Header(string name) => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;

        var sentAt = message.InternalDate is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.UtcNow;

        return new ProviderMessage(
            message.Id ?? string.Empty,
            message.ThreadId,
            Header("From"),
            Header("To"),
            Header("Subject"),
            sentAt,
            ExtractBody(message.Payload, "text/plain"),
            ExtractBody(message.Payload, "text/html"),
            null,
            MapFolder(message.LabelIds));
    }

    internal static string BuildGmailQuery(IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since)
    {
        var senders = allowedSenders
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var parts = new List<string>();
        if (senders.Length > 0)
        {
            parts.Add("(" + string.Join(" OR ", senders.Select(x => $"from:{x}")) + ")");
        }

        if (since is not null)
        {
            parts.Add($"after:{since.Value.UtcDateTime:yyyy/MM/dd}");
        }

        return string.Join(" ", parts);
    }

    internal static MailFolder MapFolder(IList<string>? labelIds) =>
        labelIds is not null && labelIds.Contains("SPAM") ? MailFolder.Junk : MailFolder.Inbox;

    internal static string? ExtractBody(MessagePart? payload, string mimeType)
    {
        if (payload is null)
        {
            return null;
        }

        if (string.Equals(payload.MimeType, mimeType, StringComparison.OrdinalIgnoreCase) && payload.Body?.Data is { Length: > 0 } data)
        {
            return DecodeBase64Url(data);
        }

        if (payload.Parts is null)
        {
            return null;
        }

        foreach (var part in payload.Parts)
        {
            var found = ExtractBody(part, mimeType);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string DecodeBase64Url(string data)
    {
        var normalized = data.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    private string RequiredConfig(string key) =>
        configuration[key] ?? throw new InvalidOperationException($"{key} is required for Gmail OAuth.");
}
```

- [ ] **Step 4: Register GmailProvider with an HttpClient**

In `src/WebMail/Program.cs`, replace line 18 (`builder.Services.AddScoped<IEmailProvider, GmailProvider>();`) with:

```csharp
builder.Services.AddHttpClient<GmailProvider>();
builder.Services.AddScoped<IEmailProvider>(sp => sp.GetRequiredService<GmailProvider>());
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/WebMail.Tests/WebMail.Tests.csproj --filter GmailProviderTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Build the solution**

Run: `dotnet build WebMail.sln`
Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/WebMail/Services/EmailProviders/GmailProvider.cs src/WebMail/Program.cs tests/WebMail.Tests/GmailProviderTests.cs
git commit -m "feat: implement gmail oauth and inbox/spam fetch"
```

---

## Task 7: Show a Junk badge on the supplier mail page

**Files:**
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml`

**Interfaces:**
- Consumes: `EmailMessage.Folder` (from Task 1), `MailFolder` enum.

- [ ] **Step 1: Add a source column with the Junk badge**

Replace the `<table>` block in `src/WebMail/Pages/Supplier/Mail.cshtml` (lines 20-38):

```html
    <table class="table table-striped">
        <thead>
            <tr>
                <th>来源</th>
                <th>发件人</th>
                <th>主题</th>
                <th>发送时间</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var message in Model.Messages)
            {
                <tr>
                    <td>
                        @if (message.Folder == MailFolder.Junk)
                        {
                            <span class="badge bg-warning text-dark">垃圾邮件</span>
                        }
                    </td>
                    <td>@message.Sender</td>
                    <td>@message.Subject</td>
                    <td>@message.SentAt</td>
                </tr>
            }
        </tbody>
    </table>
```

(The `@using WebMail.Domain` directive needed for `MailFolder` is already present at the top of the file.)

- [ ] **Step 2: Build the solution**

Run: `dotnet build WebMail.sln`
Expected: build succeeds.

- [ ] **Step 3: Manual verification**

Run: `dotnet run --project src/WebMail/WebMail.csproj` and confirm the app starts. (Full mail rendering requires DB init + provider credentials, both out of scope; this step just confirms the Razor page compiles and the app boots.)

- [ ] **Step 4: Commit**

```bash
git add src/WebMail/Pages/Supplier/Mail.cshtml
git commit -m "feat: badge junk mail on supplier mail page"
```

---

## Task 8: Full verification

**Files:**
- Modify only files needed to fix failures.

- [ ] **Step 1: Full test run**

Run: `dotnet test WebMail.sln`
Expected: all tests pass (existing + `EmailMessageTests`, `OutlookProviderTests`, `MailSyncProcessorTests`, `GmailProviderTests`).

- [ ] **Step 2: Full build**

Run: `dotnet build WebMail.sln`
Expected: build succeeds with no warnings introduced by these changes.

- [ ] **Step 3: Git status**

Run: `git status --short`
Expected: clean working tree (all changes committed).

---

## Self-Review

Spec coverage:

- Junk folder fetch (Outlook) — Task 3. Junk folder fetch (Gmail) — Task 6.
- `from:<allowed sender>` filter reused for both folders — Tasks 2, 3, 6.
- `MailFolder` field + upsert (incl. folder update on not-spam moves) — Tasks 1, 4.
- Missing `SyncJob` consumer (`MailSyncProcessor`) — Tasks 4, 5.
- Gmail full pipeline (OAuth exchange + fetch + MIME parse) — Task 6.
- Junk labeling on supplier page — Task 7.
- Out-of-scope items (token encryption, incremental sync, Trash, DB migrations) — intentionally not implemented; noted in Global Constraints.

Type consistency:

- `FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken)` — identical in interface (Task 2), Outlook (Tasks 2-3), Gmail (Task 6), processor call site (Task 4), and both test fakes (Tasks 2, 4).
- `ProviderMessage` 10-arg shape with trailing `MailFolder Folder` — consistent across Tasks 2, 4, 6.
- `MailSyncProcessor.ProcessPendingAsync(WebMailDbContext, DateTimeOffset, CancellationToken)` — defined Task 4, called Task 5.
- `BuildFolderMessagesUrl` / `BuildGmailQuery` / `MapFolder` / `ExtractBody` — names match between their tests and implementations.
