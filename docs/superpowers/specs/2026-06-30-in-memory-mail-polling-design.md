# 内存邮件缓存 + AJAX 轮询

## 背景与动机

当前邮件同步链路为后台定时拉取并持久化到 `EmailMessages` 表：

- `MailSyncBackgroundService` 每分钟跑一次 → `MailSyncJobQueueService` 根据 `ActiveSyncWindow` 入队 → `MailSyncProcessor` 拉取并 upsert 进 `EmailMessages`。
- `Supplier/Mail` 页面在 `OnGet` 时从 `EmailMessages` 表读取，无任何前端轮询。

问题：

1. 后台空转体感差——授权后打开页面，需等下一轮同步（最多 1 分钟）并手动刷新才能看到邮件。
2. 邮件全量入库，长期增长，无清理。
3. 仅需"看最新几封"的场景被过度工程化。

## 目标

- 邮件不再持久化到数据库，改为内存缓存，每个买家仅保留最新 10 条。
- 去掉后台定时同步服务。
- `Supplier/Mail` 页面通过 AJAX 轮询获取邮件，带"自动轮询开关"与"手动刷新"按钮。
- 拉取失败时返回上一次缓存的旧数据，并在界面给出提示。

## 非目标

- 不做邮件正文/附件的展示（列表仅显示发件人、主题、时间、来源）。
- 不做邮件分页或历史检索。
- 不引入新的前端框架，继续使用已加载的 jQuery。

## 移除的旧链路

以下生产代码与实体全部删除：

- `Services/Background/MailSyncBackgroundService.cs`
- `Services/Background/MailSyncJobQueueService.cs`
- `Services/Background/MailSyncProcessor.cs`
- `Services/MailSyncPlanner.cs`（已是无引用死代码）
- `Domain/Entities.cs` 中的 `SyncJob`、`ActiveSyncWindow`、`EmailMessage` 实体
- `Data/WebMailDbContext.cs` 中对应的 `DbSet` 与模型配置（`SyncJobs`、`ActiveSyncWindows`、`EmailMessages` 及其索引）
- `Program.cs` 中 `MailSyncPlanner`、`MailSyncJobQueueService`、`MailSyncProcessor`、`MailSyncBackgroundService` 的注册

`Supplier/Mail.cshtml.cs` 的 `OnGet` 中写入 `ActiveSyncWindow` 的逻辑一并移除。

> 注意：`Program.cs` 使用 `EnsureCreatedAsync` 初始化数据库，不会自动删除已存在的旧表。开发库 `webmail.dev.db` 中 `EmailMessages`/`SyncJobs`/`ActiveSyncWindows` 会残留（无害）。如需彻底干净，删除该 db 文件让其重建。

测试侧删除对应已失效的测试文件：

- `tests/WebMail.Tests/MailSyncProcessorTests.cs`
- `tests/WebMail.Tests/MailSyncJobQueueServiceTests.cs`

## 新增组件

### `MailMessageView`（record）

列表视图模型，仅暴露界面所需字段：

```csharp
public sealed record MailMessageView(
    string Id,
    string Sender,
    string Subject,
    DateTimeOffset SentAt,
    MailFolder Folder);
```

放在 `WebMail.Domain`（与 `MailFolder` 同命名空间，因 record 引用 `MailFolder`）。

### `MailCacheResult`（record）

`MailCacheService` 的返回类型，携带降级状态：

```csharp
public sealed record MailCacheResult(
    IReadOnlyList<MailMessageView> Messages,
    bool Stale,
    string? Error);
```

- `Stale = true`：返回的是过期缓存（上次拉取失败）。
- `Error` 非空：错误简述。

### `IMailCacheService` / `MailCacheService`（单例）

按 `buyerId` 缓存最新 10 条 + 上次拉取时间。

```csharp
public interface IMailCacheService
{
    Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken cancellationToken);
}
```

**内部结构**

- `ConcurrentDictionary<long, CacheEntry>`，`CacheEntry = { IReadOnlyList<MailMessageView> Messages, DateTimeOffset FetchedAt }`。
- 依赖 `IServiceScopeFactory`（单例不能直接注入 scoped 的 `WebMailDbContext`，按需开 scope）。

**`GetOrFetchAsync` 行为**

1. 不强制（`force=false`）且缓存存在且 `now - FetchedAt < Ttl`（默认 30s，读 `MailSync:CacheTtlSeconds`，缺省 30）→ 返回 `MailCacheResult(缓存, Stale=false, Error=null)`。
2. 否则触发拉取：
   - 开 scope，查 `EmailAccounts.FirstOrDefault(a => a.BuyerId == buyerId)`；若 account 不存在 → 返回空列表 + `Error="无邮箱账号"`，`Stale=false`。
   - `tokenProtector.Unprotect(account.EncryptedRefreshToken)` → `providers.Resolve(account.Provider).FetchMessagesAsync(refreshToken, [], since, ct)`。
     - `allowedSenders` 传空集合（沿用上一轮改动：空白名单时拉全部）。
     - `since = now - InitialSyncDays`（仍读 `MailSync:InitialSyncDays`，缺省 30）。
   - 成功：映射为 `MailMessageView`，按 `SentAt` 降序取前 10，更新缓存，返回 `MailCacheResult(新数据, Stale=false, Error=null)`。
   - 失败（捕获异常）：
     - 若为 `ProviderAuthorizationException`：在 scope 内将买家 `EmailStatus` 置 `Abnormal` 并写 `AuditLog`（沿用 `MailSyncProcessor` 原逻辑），`SaveChangesAsync`。
     - 若缓存有旧数据 → 返回 `MailCacheResult(旧数据, Stale=true, Error="邮件刷新失败，以下为上次结果")`。
     - 若缓存空 → 返回 `MailCacheResult(空, Stale=false, Error="邮件刷新失败，且无历史数据")`。
   - 拉取的"映射 + 截断 10 条"纯逻辑抽成 `internal static` 方法，便于单测。

### `Supplier/Mail` 页面改动

- `OnGet`：
  - 移除 `ActiveSyncWindow` 写入逻辑。
  - 初始列表改为调用 `IMailCacheService.GetOrFetchAsync(buyerId, force:false)`（页面首次加载即触发一次拉取，缓存空时直接拉 Gmail）。
  - 保留 shipments 加载与授权/访问检查（`ShipmentAccess.CanAccessBuyerAsync`、供应商要求 `IsBuyerReadyAsync`、管理员不限）。
- `OnGetPoll`（新处理器）：
  - 参数 `bool force`（`?handler=poll&force=1`）。
  - 复用与 `OnGet` 相同的访问/授权检查；不通过 → `return Forbid()`。
  - 调 `IMailCacheService.GetOrFetchAsync(buyerId, force, ct)` → `new JsonResult(new { messages, stale, error })`。
- `Messages` 属性类型改为 `IReadOnlyList<MailMessageView>`。

### 前端 JS（`Mail.cshtml` 的 `@section Scripts`，jQuery）

- 自动轮询开关 checkbox（默认勾选）+ 手动刷新按钮。
- 勾选时 `setInterval` 每 15s 调 `?handler=poll&force=0`。
- 刷新按钮：单次调 `?handler=poll&force=1`。
- 返回后重绘 `<tbody>`（保留 junk 徽章逻辑），无邮件显示"暂无邮件"。
- `stale=true` 或 `error` 非空：照常重绘表格（旧数据），并在表格上方显示 `alert-warning` 提示；下次成功刷新自动清除。
- `beforeunload` 清 interval。

## 数据流

1. 页面加载 `OnGet` → 渲染初始列表（从缓存，可能触发一次 Gmail 拉取）。
2. JS 起轮询定时器（若开关开）→ 每 15s 调 `OnGetPoll?force=0`。
3. `OnGetPoll` → `MailCacheService.GetOrFetchAsync`：缓存新鲜直接返回，否则异步拉 Gmail。
4. 返回 JSON → JS 重绘表格 + 错误提示。

## 错误处理

| 场景 | 行为 |
|------|------|
| 拉取失败 + 缓存有旧数据 | 返回旧数据，`Stale=true`，界面显示告警 |
| 拉取失败 + 缓存空 | 返回空列表，`Error` 非空，界面显示告警 + "暂无邮件" |
| `ProviderAuthorizationException` | 置买家 `Abnormal` + 审计日志；前端层面表现为"刷新失败 + 返回旧数据" |
| 授权/访问不通过 | `OnGetPoll` 返回 403，前端显示无权限 |
| 页面离开 | `beforeunload` 清 interval |

## 测试策略

沿用仓库现有 xUnit 风格（in-memory DB + fake provider，参考原 `MailSyncProcessorTests`）。把"拉取 + 截断 + 映射"纯逻辑抽成 `internal static` 方法直接单测；缓存/TTL/降级用 fake provider + 真实 `MailCacheService` 测。

用例：

1. `TruncateKeepsLatest10BySentAtDescending` — 15 条输入，截断后是按 `SentAt` 倒序的前 10 条。
2. `GetOrFetchReturnsCachedWithinTtlWithoutRefetch` — TTL 内第二次调用不触发 provider（fake provider 计数）。
3. `GetOrFetchForcesRefreshWhenForced` — `force=true` 绕过 TTL，触发 provider。
4. `GetOrFetchReturnsStaleCacheWithErrorOnFetchFailure` — provider 抛异常、缓存有数据 → 返回旧数据，`Stale=true`，`Error` 非空。
5. `GetOrFetchReturnsEmptyWithErrorOnFirstFailure` — 缓存空 + provider 抛异常 → 空列表，`Error` 非空。
6. `GetOrFetchFlipsBuyerToAbnormalOnAuthFailure` — provider 抛 `ProviderAuthorizationException` → 买家 `EmailStatus = Abnormal`。
7. `OnGetPollReturnsJsonMessages` — Page 处理器返回 JSON（轻量测，mock `IMailCacheService`）。

## 配置

`appsettings.json` 的 `MailSync` 节保留并扩展：

```json
"MailSync": {
  "InitialSyncDays": 30,
  "CacheTtlSeconds": 30
}
```

（原 `ActiveWindowMinutes` / `ActiveWindowIntervalMinutes` / `GlobalIntervalMinutes` 随后台服务删除可一并清理。）
