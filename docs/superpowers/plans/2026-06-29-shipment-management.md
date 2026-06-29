# 发货表管理（Shipment Management）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为每个买家记录发货（图片+描述+雪花发货单ID+关联买家），管理员与供应商可在买家邮箱页新增/删除，图片需登录鉴权才能查看。

**Architecture:** 新增 `Shipment` 实体（EF + SQLite）；雪花ID 生成器产出发货单号；`ShipmentService` 负责图片落盘（`wwwroot` 之外）、增删查与审计；图片经 `[Authorize]` 的 `/Shipment/Image/{id}` 接口按「管理员 或 分配供应商」鉴权后流式返回；复用 `/Supplier/Mail` 页（放宽授权 + 角色分支）作为增删入口，`Admin/Buyers` 行点击进入同页。

**Tech Stack:** ASP.NET Core 8 Razor Pages、EF Core 8 + SQLite、xUnit + EF InMemory、Bootstrap modal、resx 本地化。

## Global Constraints

- 目标框架 net8.0；不引入 EF 迁移框架、不引第三方上传/JS 组件（用现有 Bootstrap modal）。
- 数据库经 `Program.cs:77` 的 `EnsureCreatedAsync()` 建库；新增表需额外幂等 `CREATE TABLE IF NOT EXISTS` 兼容已存在的本地库。
- 所有 `DateTimeOffset` 由 `WebMailDbContext` 全局值转换器存为 UTC ticks（`INTEGER`）；新实体的 `CreatedAt` 自动适用，原始建表 SQL 中对应列为 `INTEGER`。
- 图片**不得**放入 `wwwroot`；存储根默认 `storage/shipments`（相对 `ContentRootPath`），可由配置 `Shipments:StoragePath` 覆盖。
- 图片白名单：`image/jpeg`、`image/png`、`image/webp`、`image/gif`；大小上限 5MB（`5 * 1024 * 1024`）。
- 雪花ID：`epoch = 2024-01-01T00:00:00Z`、`workerId = 1`；`ShipmentNo` 为 `long`。
- 角色字符串：`"Administrator"`、`"Supplier"`（与现有 `UserRole` 及策略一致）。
- 删除为硬删除（连同磁盘文件），并写 `AuditLog`。
- 本地化键同时加入 `Resources/SharedResource.zh-CN.resx` 与 `SharedResource.en.resx`。
- 测试约定：服务用 `UseInMemoryDatabase(Guid.NewGuid().ToString("N"))`；页面测试用 `DefaultHttpContext` 注入 `ClaimsPrincipal`；本地化用 `TestLocalizer.Shared`。

---

### Task 1: Shipment 实体 + DbSet + 索引 + 启动幂等建表

**Files:**
- Modify: `src/WebMail/Domain/Entities.cs`（文件末尾，`AuditLog` 之后追加）
- Modify: `src/WebMail/Data/WebMailDbContext.cs`（加 `DbSet` 与索引）
- Modify: `src/WebMail/Program.cs`（`EnsureCreatedAsync()` 之后加幂等建表）
- Test: `tests/WebMail.Tests/ShipmentEntityTests.cs`（Create）

**Interfaces:**
- Produces: `WebMail.Domain.Shipment`（字段：`Id:long`、`BuyerId:long`、`ShipmentNo:long`、`StoredFileName:string`、`ContentType:string`、`Description:string`、`CreatedByUserId:long?`、`CreatedAt:DateTimeOffset`）；`WebMailDbContext.Shipments : DbSet<Shipment>`。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/ShipmentEntityTests.cs`：

```csharp
using System;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using Xunit;

namespace WebMail.Tests;

public sealed class ShipmentEntityTests
{
    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    [Fact]
    public async Task PersistsAndReloadsShipment()
    {
        await using var db = CreateDb();
        db.Shipments.Add(new Shipment
        {
            BuyerId = 7,
            ShipmentNo = 123456789012345,
            StoredFileName = "20260629T120000000_abc123.jpg",
            ContentType = "image/jpeg",
            Description = "box photo",
            CreatedByUserId = 2
        });
        await db.SaveChangesAsync();

        var loaded = await db.Shipments.SingleAsync();
        Assert.Equal(7, loaded.BuyerId);
        Assert.Equal(123456789012345, loaded.ShipmentNo);
        Assert.Equal("image/jpeg", loaded.ContentType);
        Assert.Equal("box photo", loaded.Description);
        Assert.Equal(2, loaded.CreatedByUserId);
        Assert.True(loaded.CreatedAt <= DateTimeOffset.UtcNow);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter ShipmentEntityTests`
Expected: 编译失败 —「找不到 `Shipment`」「`WebMailDbContext` 不含 `Shipments`」。

- [ ] **Step 3: 加实体**

在 `src/WebMail/Domain/Entities.cs` 末尾追加：

```csharp
public sealed class Shipment
{
    public long Id { get; set; }
    public long BuyerId { get; set; }
    public long ShipmentNo { get; set; }
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 4: 加 DbSet 与索引**

`src/WebMail/Data/WebMailDbContext.cs`：在 `AuditLogs` 之后加 DbSet：

```csharp
    public DbSet<Shipment> Shipments => Set<Shipment>();
```

在 `OnModelCreating` 内 `BuyerSupplierAssignment` 索引附近加：

```csharp
        modelBuilder.Entity<Shipment>().HasIndex(x => x.BuyerId);
        modelBuilder.Entity<Shipment>().HasIndex(x => x.ShipmentNo).IsUnique();
```

- [ ] **Step 5: 加启动幂等建表**

`src/WebMail/Program.cs`：紧接 `await db.Database.EnsureCreatedAsync();` 之后加（兼容已存在的本地库；列定义须与 EF 模型一致，`CreatedAt` 为 ticks `INTEGER`）：

```csharp
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "Shipments" (
          "Id" INTEGER NOT NULL CONSTRAINT "PK_Shipments" PRIMARY KEY AUTOINCREMENT,
          "BuyerId" INTEGER NOT NULL,
          "ShipmentNo" INTEGER NOT NULL,
          "StoredFileName" TEXT NOT NULL,
          "ContentType" TEXT NOT NULL,
          "Description" TEXT NOT NULL,
          "CreatedByUserId" INTEGER NULL,
          "CreatedAt" INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_Shipments_BuyerId" ON "Shipments" ("BuyerId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Shipments_ShipmentNo" ON "Shipments" ("ShipmentNo");
        """);
```

确认 `Program.cs` 顶部已 `using Microsoft.EntityFrameworkCore;`（`ExecuteSqlRawAsync` 扩展所需）。若 `db` 变量作用域不同，按现有 `EnsureCreatedAsync` 所在的 `using var scope` 块内同一 `db` 添加。

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter ShipmentEntityTests`
Expected: PASS。

- [ ] **Step 7: 提交**

```bash
git add src/WebMail/Domain/Entities.cs src/WebMail/Data/WebMailDbContext.cs src/WebMail/Program.cs tests/WebMail.Tests/ShipmentEntityTests.cs
git commit -m "feat(shipment): add Shipment entity, DbSet, indexes, idempotent table guard"
```

---

### Task 2: SnowflakeIdGenerator + 注册

**Files:**
- Create: `src/WebMail/Services/SnowflakeIdGenerator.cs`
- Modify: `src/WebMail/Program.cs`（注册单例）
- Test: `tests/WebMail.Tests/SnowflakeIdGeneratorTests.cs`

**Interfaces:**
- Produces: `WebMail.Services.SnowflakeIdGenerator`，构造 `SnowflakeIdGenerator(DateTimeOffset? epoch = null, int workerId = 1)`；方法 `long NextId()`。默认 epoch 2024-01-01Z、workerId 1。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/SnowflakeIdGeneratorTests.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class SnowflakeIdGeneratorTests
{
    [Fact]
    public void GeneratesUniqueMonotonicIds()
    {
        var gen = new SnowflakeIdGenerator();
        var ids = new List<long>();
        for (var i = 0; i < 5000; i++) ids.Add(gen.NextId());

        Assert.Equal(ids.Count, ids.Distinct().Count());          // 全部唯一
        for (var i = 1; i < ids.Count; i++) Assert.True(ids[i] > ids[i - 1]); // 单调递增
        Assert.All(ids, id => Assert.True(id > 0));
    }

    [Fact]
    public void EmbeddedTimestampIsRecent()
    {
        var epoch = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gen = new SnowflakeIdGenerator(epoch, workerId: 1);
        var before = DateTimeOffset.UtcNow;

        var id = gen.NextId();

        var ms = (id >> 22) + epoch.ToUnixTimeMilliseconds();
        var decoded = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        Assert.True(decoded >= before.AddSeconds(-2));
        Assert.True(decoded <= DateTimeOffset.UtcNow.AddSeconds(2));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter SnowflakeIdGeneratorTests`
Expected: 编译失败 —「找不到 `SnowflakeIdGenerator`」。

- [ ] **Step 3: 实现生成器**

创建 `src/WebMail/Services/SnowflakeIdGenerator.cs`：

```csharp
namespace WebMail.Services;

/// <summary>
/// Twitter 风格雪花ID：[时间戳(42bit) | workerId(10bit) | sequence(12bit)]。
/// 时间有序、可解出毫秒时间戳。线程安全。
/// </summary>
public sealed class SnowflakeIdGenerator
{
    private const int WorkerBits = 10;
    private const int SequenceBits = 12;
    private const long MaxSequence = (1L << SequenceBits) - 1;

    private readonly long _epochMs;
    private readonly long _workerId;
    private readonly object _lock = new();
    private long _lastMs = -1L;
    private long _sequence;

    public SnowflakeIdGenerator(DateTimeOffset? epoch = null, int workerId = 1)
    {
        var e = epoch ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _epochMs = e.ToUnixTimeMilliseconds();
        if (workerId < 0 || workerId >= (1 << WorkerBits))
            throw new ArgumentOutOfRangeException(nameof(workerId));
        _workerId = workerId;
    }

    public long NextId()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < _lastMs) now = _lastMs; // 时钟回拨：钳到上次，避免倒退

            if (now == _lastMs)
            {
                _sequence = (_sequence + 1) & MaxSequence;
                if (_sequence == 0) // 当前毫秒序列用尽，自旋到下一毫秒
                {
                    do { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
                    while (now <= _lastMs);
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastMs = now;
            return ((now - _epochMs) << (WorkerBits + SequenceBits))
                 | (_workerId << SequenceBits)
                 | _sequence;
        }
    }
}
```

> 说明：测试里 `id >> 22` 对应 `WorkerBits + SequenceBits = 22`，与此实现一致。

- [ ] **Step 4: 注册单例**

`src/WebMail/Program.cs`：在 `AddSingleton<CardGenerationService>();` 附近加：

```csharp
builder.Services.AddSingleton<SnowflakeIdGenerator>();
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter SnowflakeIdGeneratorTests`
Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Services/SnowflakeIdGenerator.cs src/WebMail/Program.cs tests/WebMail.Tests/SnowflakeIdGeneratorTests.cs
git commit -m "feat(shipment): add thread-safe snowflake id generator"
```

---

### Task 3: ShipmentService（落盘+增删查+审计）+ 注册

**Files:**
- Create: `src/WebMail/Services/ShipmentService.cs`
- Modify: `src/WebMail/Program.cs`（注册 scoped + 存储根）
- Modify: `src/WebMail/appsettings.json`（加 `Shipments:StoragePath` 默认值，可选）
- Test: `tests/WebMail.Tests/ShipmentServiceTests.cs`

**Interfaces:**
- Consumes: `WebMailDbContext`、`SnowflakeIdGenerator`（Task 2）、`Shipment`（Task 1）。
- Produces:
  - `record ShipmentImageInput(Stream Content, string ContentType, long Length)`
  - `record ShipmentResult(bool Success, string MessageKey, long? ShipmentId = null)`
  - `ShipmentService(WebMailDbContext db, SnowflakeIdGenerator snowflake, string storageRoot)`
  - `Task<ShipmentResult> CreateAsync(long buyerId, string? description, ShipmentImageInput? image, long? userId)`
  - `Task<bool> DeleteAsync(long shipmentId, long? userId)`
  - `Task<IReadOnlyList<Shipment>> GetForBuyerAsync(long buyerId)`
  - `Task<Shipment?> GetByIdAsync(long shipmentId)`
  - `string GetFilePath(Shipment s)`（存储根 + 文件名）

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/ShipmentServiceTests.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class ShipmentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shiptest_" + Guid.NewGuid().ToString("N"));

    private WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private ShipmentService CreateService(WebMailDbContext db) =>
        new(db, new SnowflakeIdGenerator(), _root);

    private static ShipmentImageInput Img(string contentType = "image/jpeg", int size = 64)
        => new(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', size))), contentType, size);

    [Fact]
    public async Task CreateWritesRecordAndFile()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(buyerId: 9, description: "hello", image: Img(), userId: 3);

        Assert.True(result.Success);
        var s = await db.Shipments.SingleAsync();
        Assert.Equal(9, s.BuyerId);
        Assert.True(s.ShipmentNo > 0);
        Assert.Equal("hello", s.Description);
        Assert.Equal(3, s.CreatedByUserId);
        Assert.True(File.Exists(svc.GetFilePath(s)));
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "CreateShipment").ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsMissingImage()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(9, "x", image: null, userId: 3);

        Assert.False(result.Success);
        Assert.Equal("Shipment.InvalidImage", result.MessageKey);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsWrongType()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(9, "x", Img(contentType: "application/pdf"), userId: 3);

        Assert.False(result.Success);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsTooLarge()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(9, "x", new ShipmentImageInput(new MemoryStream(new byte[16]), "image/png", 5 * 1024 * 1024 + 1), userId: 3);

        Assert.False(result.Success);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task DeleteRemovesRecordAndFile()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var created = await svc.CreateAsync(9, "x", Img(), 3);
        var s = await db.Shipments.SingleAsync();
        var path = svc.GetFilePath(s);

        var ok = await svc.DeleteAsync(s.Id, userId: 3);

        Assert.True(ok);
        Assert.Empty(await db.Shipments.ToListAsync());
        Assert.False(File.Exists(path));
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "DeleteShipment").ToListAsync());
    }

    [Fact]
    public async Task GetForBuyerReturnsOnlyThatBuyerNewestFirst()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        await svc.CreateAsync(1, "a", Img(), 3);
        await svc.CreateAsync(2, "b", Img(), 3);
        await svc.CreateAsync(1, "c", Img(), 3);

        var list = await svc.GetForBuyerAsync(1);

        Assert.Equal(2, list.Count);
        Assert.All(list, x => Assert.Equal(1, x.BuyerId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter ShipmentServiceTests`
Expected: 编译失败 —「找不到 `ShipmentService` / `ShipmentImageInput` / `ShipmentResult`」。

- [ ] **Step 3: 实现服务**

创建 `src/WebMail/Services/ShipmentService.cs`：

```csharp
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services;

public sealed record ShipmentImageInput(Stream Content, string ContentType, long Length);

public sealed record ShipmentResult(bool Success, string MessageKey, long? ShipmentId = null);

public sealed class ShipmentService
{
    private const long MaxBytes = 5L * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    private readonly WebMailDbContext _db;
    private readonly SnowflakeIdGenerator _snowflake;
    private readonly string _storageRoot;

    public ShipmentService(WebMailDbContext db, SnowflakeIdGenerator snowflake, string storageRoot)
    {
        _db = db;
        _snowflake = snowflake;
        _storageRoot = storageRoot;
    }

    public string GetFilePath(Shipment s) => Path.Combine(_storageRoot, s.StoredFileName);

    public Task<Shipment?> GetByIdAsync(long shipmentId) =>
        _db.Shipments.FirstOrDefaultAsync(x => x.Id == shipmentId);

    public async Task<IReadOnlyList<Shipment>> GetForBuyerAsync(long buyerId) =>
        await _db.Shipments.Where(x => x.BuyerId == buyerId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .ToListAsync();

    public async Task<ShipmentResult> CreateAsync(long buyerId, string? description, ShipmentImageInput? image, long? userId)
    {
        if (image is null || image.Length <= 0 || image.Length > MaxBytes
            || !AllowedTypes.TryGetValue(image.ContentType, out var ext))
        {
            return new ShipmentResult(false, "Shipment.InvalidImage");
        }

        Directory.CreateDirectory(_storageRoot);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff");
        var rand = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var fileName = $"{stamp}_{rand}{ext}";
        var fullPath = Path.Combine(_storageRoot, fileName);

        await using (var dest = File.Create(fullPath))
        {
            image.Content.Position = 0;
            await image.Content.CopyToAsync(dest);
        }

        var shipment = new Shipment
        {
            BuyerId = buyerId,
            ShipmentNo = _snowflake.NextId(),
            StoredFileName = fileName,
            ContentType = image.ContentType,
            Description = description?.Trim() ?? string.Empty,
            CreatedByUserId = userId,
        };
        _db.Shipments.Add(shipment);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "CreateShipment",
            UserId = userId,
            Details = $"buyer={buyerId};shipment={shipment.ShipmentNo}"
        });
        await _db.SaveChangesAsync();

        return new ShipmentResult(true, "Shipment.Added", shipment.Id);
    }

    public async Task<bool> DeleteAsync(long shipmentId, long? userId)
    {
        var shipment = await _db.Shipments.FirstOrDefaultAsync(x => x.Id == shipmentId);
        if (shipment is null) return false;

        var path = GetFilePath(shipment);
        if (File.Exists(path)) File.Delete(path);

        _db.Shipments.Remove(shipment);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "DeleteShipment",
            UserId = userId,
            Details = $"buyer={shipment.BuyerId};shipment={shipment.ShipmentNo}"
        });
        await _db.SaveChangesAsync();
        return true;
    }
}
```

- [ ] **Step 4: 注册服务 + 存储根**

`src/WebMail/Program.cs`：在 `AddScoped<CardKeyService>();` 附近加（用工厂解析存储根的绝对路径）：

```csharp
builder.Services.AddScoped<ShipmentService>(sp =>
{
    var db = sp.GetRequiredService<WebMailDbContext>();
    var snowflake = sp.GetRequiredService<SnowflakeIdGenerator>();
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var cfg = sp.GetRequiredService<IConfiguration>();
    var rel = cfg["Shipments:StoragePath"] ?? "storage/shipments";
    var root = Path.IsPathRooted(rel) ? rel : Path.Combine(env.ContentRootPath, rel);
    return new ShipmentService(db, snowflake, root);
});
```

确认 `Program.cs` 已有 `using WebMail.Services;` 与 `using WebMail.Data;`（现有代码已引用这些服务，通常已具备）。

- [ ] **Step 5: appsettings 默认值（可选）**

`src/WebMail/appsettings.json` 顶层加一项（不加则用代码默认 `storage/shipments`）：

```json
  "Shipments": { "StoragePath": "storage/shipments" }
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter ShipmentServiceTests`
Expected: 6 个用例 PASS。

- [ ] **Step 7: 提交**

```bash
git add src/WebMail/Services/ShipmentService.cs src/WebMail/Program.cs src/WebMail/appsettings.json tests/WebMail.Tests/ShipmentServiceTests.cs
git commit -m "feat(shipment): add ShipmentService for disk storage, CRUD, and audit"
```

---

### Task 4: 共享授权辅助 + 图片鉴权接口 `/Shipment/Image/{id}`

**Files:**
- Create: `src/WebMail/Services/Security/ShipmentAccess.cs`
- Create: `src/WebMail/Pages/Shipment/Image.cshtml`
- Create: `src/WebMail/Pages/Shipment/Image.cshtml.cs`
- Test: `tests/WebMail.Tests/ShipmentImageModelTests.cs`

**Interfaces:**
- Consumes: `WebMailDbContext`、`ShipmentService`（Task 3）。
- Produces: `static class ShipmentAccess { Task<bool> CanAccessBuyerAsync(WebMailDbContext db, ClaimsPrincipal user, long buyerId) }`；Razor Page `ImageModel`（路由 `/Shipment/Image/{id:long}`），`OnGetAsync(long id)` 返回文件/`Forbid`/`NotFound`。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/ShipmentImageModelTests.cs`：

```csharp
using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using ImageModel = WebMail.Pages.Shipment.ImageModel;
using Xunit;

namespace WebMail.Tests;

public sealed class ShipmentImageModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shipimg_" + Guid.NewGuid().ToString("N"));

    private WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private ShipmentService Svc(WebMailDbContext db) => new(db, new SnowflakeIdGenerator(), _root);

    private static ImageModel CreateModel(WebMailDbContext db, ShipmentService svc, long userId, string role)
    {
        var model = new ImageModel(db, svc);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "test"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return model;
    }

    private async Task<long> SeedShipment(WebMailDbContext db, ShipmentService svc, long buyerId)
    {
        await svc.CreateAsync(buyerId, "x",
            new ShipmentImageInput(new MemoryStream(new byte[] { 1, 2, 3 }), "image/png", 3), userId: 1);
        return (await db.Shipments.SingleAsync(s => s.BuyerId == buyerId)).Id;
    }

    [Fact]
    public async Task AdminGetsAnyImage()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var id = await SeedShipment(db, svc, buyerId: 50);
        var model = CreateModel(db, svc, userId: 1, role: "Administrator");

        var result = await model.OnGetAsync(id);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task AssignedSupplierGetsImage()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var id = await SeedShipment(db, svc, buyerId: 50);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = 50, SupplierId = 7 });
        await db.SaveChangesAsync();
        var model = CreateModel(db, svc, userId: 7, role: "Supplier");

        var result = await model.OnGetAsync(id);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task UnassignedSupplierForbidden()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var id = await SeedShipment(db, svc, buyerId: 50);
        var model = CreateModel(db, svc, userId: 8, role: "Supplier");

        var result = await model.OnGetAsync(id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task MissingShipmentNotFound()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var model = CreateModel(db, svc, userId: 1, role: "Administrator");

        var result = await model.OnGetAsync(999999);

        Assert.IsType<NotFoundResult>(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter ShipmentImageModelTests`
Expected: 编译失败 —「找不到 `WebMail.Pages.Shipment.ImageModel`」。

- [ ] **Step 3: 加授权辅助**

创建 `src/WebMail/Services/Security/ShipmentAccess.cs`：

```csharp
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;

namespace WebMail.Services.Security;

public static class ShipmentAccess
{
    /// <summary>管理员可访问任意买家；供应商仅限分配给自己的买家。</summary>
    public static async Task<bool> CanAccessBuyerAsync(WebMailDbContext db, ClaimsPrincipal user, long buyerId)
    {
        if (user.IsInRole("Administrator")) return true;
        if (!long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)) return false;
        return await db.BuyerSupplierAssignments.AnyAsync(x => x.BuyerId == buyerId && x.SupplierId == uid);
    }
}
```

- [ ] **Step 4: 加图片页**

创建 `src/WebMail/Pages/Shipment/Image.cshtml`：

```html
@page "{id:long}"
@model WebMail.Pages.Shipment.ImageModel
```

创建 `src/WebMail/Pages/Shipment/Image.cshtml.cs`：

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Data;
using WebMail.Services;
using WebMail.Services.Security;

namespace WebMail.Pages.Shipment;

[Authorize]
public class ImageModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly ShipmentService _shipments;

    public ImageModel(WebMailDbContext db, ShipmentService shipments)
    {
        _db = db;
        _shipments = shipments;
    }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var shipment = await _shipments.GetByIdAsync(id);
        if (shipment is null) return NotFound();

        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, shipment.BuyerId))
            return Forbid();

        var path = _shipments.GetFilePath(shipment);
        if (!System.IO.File.Exists(path)) return NotFound();

        return PhysicalFile(path, shipment.ContentType);
    }
}
```

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter ShipmentImageModelTests`
Expected: 4 个用例 PASS。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Services/Security/ShipmentAccess.cs src/WebMail/Pages/Shipment/ tests/WebMail.Tests/ShipmentImageModelTests.cs
git commit -m "feat(shipment): add authorized image endpoint and shared access helper"
```

---

### Task 5: 邮箱页复用（放宽授权 + 角色分支 + 发货增删 handler）

**Files:**
- Modify: `src/WebMail/Program.cs`（加 `SupplierOrAdmin` 策略）
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml.cs`（授权、依赖、加载发货、增删 handler）
- Test: `tests/WebMail.Tests/SupplierMailModelTests.cs`

**Interfaces:**
- Consumes: `ShipmentService`（Task 3）、`ShipmentAccess`（Task 4）。
- Produces: `MailModel` 新增公开属性 `IReadOnlyList<Shipment> Shipments`、`string? Message`；handler `OnPostAddShipmentAsync(long buyerId, string? description, IFormFile? image)`、`OnPostDeleteShipmentAsync(long shipmentId, long buyerId)`；构造改为 `MailModel(WebMailDbContext db, ShipmentService shipments)`。

- [ ] **Step 1: 写失败测试**

创建 `tests/WebMail.Tests/SupplierMailModelTests.cs`：

```csharp
using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using MailModel = WebMail.Pages.Supplier.MailModel;
using Xunit;

namespace WebMail.Tests;

public sealed class SupplierMailModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shipmail_" + Guid.NewGuid().ToString("N"));

    private WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private ShipmentService Svc(WebMailDbContext db) => new(db, new SnowflakeIdGenerator(), _root);

    private MailModel CreateModel(WebMailDbContext db, long userId, string role)
    {
        var model = new MailModel(db, Svc(db));
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "test"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return model;
    }

    private static IFormFile Png()
    {
        var bytes = Encoding.ASCII.GetBytes("pngdata");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "a.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }

    private static Buyer SeedApprovedBuyer(WebMailDbContext db, long id) => new()
    {
        Id = id, CardNo = "c" + id, Stage = BuyerStage.Submitted,
        ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized
    };

    [Fact]
    public async Task AdminCanAddShipmentForAnyBuyer()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 40));
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 1, role: "Administrator");

        var result = await model.OnPostAddShipmentAsync(buyerId: 40, description: "d", image: Png());

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Single(await db.Shipments.Where(s => s.BuyerId == 40).ToListAsync());
    }

    [Fact]
    public async Task AssignedSupplierCanAddShipment()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 41));
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = 41, SupplierId = 7 });
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 7, role: "Supplier");

        var result = await model.OnPostAddShipmentAsync(41, "d", Png());

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Single(await db.Shipments.Where(s => s.BuyerId == 41).ToListAsync());
    }

    [Fact]
    public async Task UnassignedSupplierCannotAddShipment()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 42));
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 8, role: "Supplier");

        var result = await model.OnPostAddShipmentAsync(42, "d", Png());

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task SupplierCannotDeleteOtherBuyersShipment()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 43));
        await db.SaveChangesAsync();
        var svc = Svc(db);
        await svc.CreateAsync(43, "d", new ShipmentImageInput(new MemoryStream(new byte[]{1}), "image/png", 1), userId: 1);
        var shipmentId = (await db.Shipments.SingleAsync()).Id;
        var model = CreateModel(db, userId: 8, role: "Supplier"); // not assigned to buyer 43

        var result = await model.OnPostDeleteShipmentAsync(shipmentId, buyerId: 43);

        Assert.IsType<ForbidResult>(result);
        Assert.Single(await db.Shipments.ToListAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test tests/WebMail.Tests --filter SupplierMailModelTests`
Expected: 编译失败 —「`MailModel` 无 `OnPostAddShipmentAsync` / 构造签名不符」。

- [ ] **Step 3: 加 `SupplierOrAdmin` 策略**

`src/WebMail/Program.cs`：在策略定义处（`AddPolicy("SupplierOnly", ...)` 附近）加：

```csharp
    options.AddPolicy("SupplierOrAdmin", policy => policy.RequireRole("Administrator", "Supplier"));
```

- [ ] **Step 4: 改写 `MailModel`**

将 `src/WebMail/Pages/Supplier/Mail.cshtml.cs` 整体替换为：

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.Security;

namespace WebMail.Pages.Supplier;

[Authorize(Policy = "SupplierOrAdmin")]
public class MailModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly ShipmentService _shipments;

    public MailModel(WebMailDbContext db, ShipmentService shipments)
    {
        _db = db;
        _shipments = shipments;
    }

    public long BuyerId { get; private set; }
    public IReadOnlyList<EmailMessage> Messages { get; private set; } = Array.Empty<EmailMessage>();
    public IReadOnlyList<Shipment> Shipments { get; private set; } = Array.Empty<Shipment>();
    public DateTimeOffset ActiveWindowExpiresAt { get; private set; }
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(long buyerId)
    {
        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
        {
            return Forbid();
        }

        // 供应商额外要求买家审核通过且邮箱已授权（与原行为一致）；管理员不受限。
        if (!User.IsInRole("Administrator"))
        {
            var ready = await _db.Buyers.AnyAsync(b => b.Id == buyerId
                && !b.IsDeleted
                && b.ReviewStatus == ReviewStatus.Approved
                && b.EmailStatus == EmailAuthorizationStatus.Authorized);
            if (!ready) return Forbid();
        }

        BuyerId = buyerId;

        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyerId);
        if (account is not null)
        {
            Messages = await _db.EmailMessages
                .Where(m => m.EmailAccountId == account.Id)
                .OrderByDescending(m => m.SentAt)
                .ToListAsync();
        }

        Shipments = await _shipments.GetForBuyerAsync(buyerId);

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var window = await _db.ActiveSyncWindows.FirstOrDefaultAsync(w => w.BuyerId == buyerId);
        if (window is null)
        {
            _db.ActiveSyncWindows.Add(new ActiveSyncWindow { BuyerId = buyerId, ExpiresAt = expiresAt });
        }
        else
        {
            window.ExpiresAt = expiresAt;
        }

        await _db.SaveChangesAsync();
        ActiveWindowExpiresAt = expiresAt;

        return Page();
    }

    public async Task<IActionResult> OnPostAddShipmentAsync(long buyerId, string? description, IFormFile? image)
    {
        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
        {
            return Forbid();
        }

        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid);
        ShipmentImageInput? input = image is { Length: > 0 }
            ? new ShipmentImageInput(image.OpenReadStream(), image.ContentType, image.Length)
            : null;

        await _shipments.CreateAsync(buyerId, description, input, uid == 0 ? null : uid);
        return RedirectToPage(new { buyerId });
    }

    public async Task<IActionResult> OnPostDeleteShipmentAsync(long shipmentId, long buyerId)
    {
        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
        {
            return Forbid();
        }

        var shipment = await _shipments.GetByIdAsync(shipmentId);
        if (shipment is not null && shipment.BuyerId == buyerId)
        {
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid);
            await _shipments.DeleteAsync(shipmentId, uid == 0 ? null : uid);
        }

        return RedirectToPage(new { buyerId });
    }
}
```

> 安全要点：删除时同时按传入 `buyerId` 鉴权**并**校验 `shipment.BuyerId == buyerId`，防止用他人 buyerId 配合任意 shipmentId 越权删除。

- [ ] **Step 5: 运行测试确认通过**

Run: `dotnet test tests/WebMail.Tests --filter SupplierMailModelTests`
Expected: 4 个用例 PASS。

- [ ] **Step 6: 提交**

```bash
git add src/WebMail/Program.cs src/WebMail/Pages/Supplier/Mail.cshtml.cs tests/WebMail.Tests/SupplierMailModelTests.cs
git commit -m "feat(shipment): allow admin on mail page, add shipment add/delete handlers"
```

---

### Task 6: 本地化键（zh-CN + en）

**Files:**
- Modify: `src/WebMail/Resources/SharedResource.zh-CN.resx`
- Modify: `src/WebMail/Resources/SharedResource.en.resx`

**Interfaces:**
- Produces: 本地化键供 Task 7 视图使用（见下表）。

- [ ] **Step 1: 加 zh-CN 键**

在 `SharedResource.zh-CN.resx` 内加入下列 `<data>` 项（保持文件原有 `<data name=...>` 格式）：

| key | value(zh-CN) |
|-----|-----|
| `Action.AddShipment` | 增加发货 |
| `Shipment.SectionTitle` | 发货记录 |
| `Shipment.Image` | 图片 |
| `Shipment.Description` | 描述 |
| `Shipment.No` | 发货单ID |
| `Shipment.CreatedAt` | 发货时间 |
| `Shipment.Submit` | 提交发货 |
| `Shipment.Delete` | 删除 |
| `Shipment.DeleteConfirm` | 确定删除此发货记录？ |
| `Shipment.None` | 暂无发货记录 |

每项形如：

```xml
  <data name="Action.AddShipment" xml:space="preserve">
    <value>增加发货</value>
  </data>
```

- [ ] **Step 2: 加 en 键**

在 `SharedResource.en.resx` 内加入对应英文：

| key | value(en) |
|-----|-----|
| `Action.AddShipment` | Add Shipment |
| `Shipment.SectionTitle` | Shipments |
| `Shipment.Image` | Image |
| `Shipment.Description` | Description |
| `Shipment.No` | Shipment No. |
| `Shipment.CreatedAt` | Shipped At |
| `Shipment.Submit` | Submit |
| `Shipment.Delete` | Delete |
| `Shipment.DeleteConfirm` | Delete this shipment? |
| `Shipment.None` | No shipments yet |

- [ ] **Step 3: 构建确认无 resx 格式错误**

Run: `dotnet build src/WebMail/WebMail.csproj`
Expected: 构建成功（resx 语法正确）。

- [ ] **Step 4: 提交**

```bash
git add src/WebMail/Resources/SharedResource.zh-CN.resx src/WebMail/Resources/SharedResource.en.resx
git commit -m "i18n(shipment): add shipment localization keys (zh-CN, en)"
```

---

### Task 7: 视图（发货分区 partial + 邮箱页接入 + 管理员入口）

**Files:**
- Create: `src/WebMail/Pages/Shared/_ShipmentSection.cshtml`
- Modify: `src/WebMail/Pages/Supplier/Mail.cshtml`（工具栏按钮 + 提示 + 引入 partial）
- Modify: `src/WebMail/Pages/Admin/Buyers.cshtml`（行内「进入邮箱」按钮）

**Interfaces:**
- Consumes: `MailModel.BuyerId`、`MailModel.Shipments`、`MailModel.Message`（Task 5）；本地化键（Task 6）；图片接口 `/Shipment/Image/{id}`（Task 4）。

- [ ] **Step 1: 创建发货分区 partial**

创建 `src/WebMail/Pages/Shared/_ShipmentSection.cshtml`（模型用 `MailModel`，复用其 `BuyerId` 与 `Shipments`）：

```html
@using WebMail.Domain
@model WebMail.Pages.Supplier.MailModel

<div class="d-flex align-items-center gap-2 mt-4 mb-2">
    <h2 class="h5 mb-0">@L["Shipment.SectionTitle"]</h2>
    <button type="button" class="btn btn-sm btn-primary" data-bs-toggle="modal" data-bs-target="#addShipmentModal">
        @L["Action.AddShipment"]
    </button>
</div>

@if (Model.Shipments.Count == 0)
{
    <p class="text-muted">@L["Shipment.None"]</p>
}
else
{
    <div class="table-responsive">
    <table class="table table-striped table-cards">
        <thead>
            <tr>
                <th>@L["Shipment.Image"]</th>
                <th>@L["Shipment.No"]</th>
                <th>@L["Shipment.Description"]</th>
                <th>@L["Shipment.CreatedAt"]</th>
                <th></th>
            </tr>
        </thead>
        <tbody>
            @foreach (var s in Model.Shipments)
            {
                <tr>
                    <td data-label="@L["Shipment.Image"]">
                        <a href="/Shipment/Image/@s.Id" target="_blank">
                            <img src="/Shipment/Image/@s.Id" alt="" style="max-height:64px;max-width:96px;object-fit:cover;" />
                        </a>
                    </td>
                    <td data-label="@L["Shipment.No"]">@s.ShipmentNo</td>
                    <td data-label="@L["Shipment.Description"]">@s.Description</td>
                    <td data-label="@L["Shipment.CreatedAt"]">@s.CreatedAt</td>
                    <td class="td-actions">
                        <form method="post" asp-page-handler="DeleteShipment"
                              onsubmit="return confirm('@L["Shipment.DeleteConfirm"]');">
                            <input type="hidden" name="shipmentId" value="@s.Id" />
                            <input type="hidden" name="buyerId" value="@Model.BuyerId" />
                            <button type="submit" class="btn btn-sm btn-outline-danger">@L["Shipment.Delete"]</button>
                        </form>
                    </td>
                </tr>
            }
        </tbody>
    </table>
    </div>
}

<div class="modal fade" id="addShipmentModal" tabindex="-1" aria-hidden="true">
  <div class="modal-dialog">
    <div class="modal-content">
      <form method="post" enctype="multipart/form-data" asp-page-handler="AddShipment">
        <div class="modal-header">
          <h5 class="modal-title">@L["Action.AddShipment"]</h5>
          <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
        </div>
        <div class="modal-body">
          <input type="hidden" name="buyerId" value="@Model.BuyerId" />
          <div class="mb-3">
            <label class="form-label">@L["Shipment.Image"]</label>
            <input type="file" name="image" accept="image/*" class="form-control" required />
          </div>
          <div class="mb-3">
            <label class="form-label">@L["Shipment.Description"]</label>
            <textarea name="description" rows="3" class="form-control"></textarea>
          </div>
        </div>
        <div class="modal-footer">
          <button type="submit" class="btn btn-primary">@L["Shipment.Submit"]</button>
        </div>
      </form>
    </div>
  </div>
</div>
```

> 确认 Bootstrap JS 已在布局加载（现有页面用了 modal/Bootstrap 才有效）。若布局未引 Bootstrap bundle，则在 `_Layout.cshtml` 补 `bootstrap.bundle.min.js`——先检查再决定，不要重复引入。

- [ ] **Step 2: 邮箱页接入 partial + 提示**

`src/WebMail/Pages/Supplier/Mail.cshtml`：在「返回列表」段落之后加提示，在邮件表之后引入 partial。

在第 12 行 `<p><a ... BackToList ...></p>` 之后加：

```html
@if (!string.IsNullOrEmpty(Model.Message))
{
    <div class="alert alert-info">@Model.Message</div>
}
```

在文件**末尾**（邮件表 `}` 之后）加：

```html
<partial name="_ShipmentSection" model="Model" />
```

- [ ] **Step 3: 管理员入口按钮**

`src/WebMail/Pages/Admin/Buyers.cshtml`：在买家行的操作单元格内（与现有「删除/审核」按钮同组），加一个进入邮箱页的链接：

```html
<a class="btn btn-sm btn-primary" asp-page="/Supplier/Mail" asp-route-buyerId="@buyer.Id">@L["Action.ViewMail"]</a>
```

> `Action.ViewMail` 键已存在（供应商表在用）。放置位置：找到 `Admin/Buyers.cshtml` 中渲染每行操作的 `<td>`（含删除表单处），把上面链接加在该组按钮最前。若该页操作列结构与预期不同，按其现有按钮容器（如 `d-flex gap-2`）就近插入。

- [ ] **Step 4: 构建并手动验证**

Run: `dotnet build src/WebMail/WebMail.csproj`
Expected: 构建成功。

手动验证（PowerShell 启动）：

```
dotnet run --project src/WebMail
```

- 管理员登录 → Admin/Buyers → 点某买家「查看邮件」→ 进入邮箱页 → 点「增加发货」→ 选图+填描述+提交 → 列表出现该发货、缩略图可显示。
- 点缩略图新开图片正常；未登录直接访问 `/Shipment/Image/{id}` 应被重定向到登录。
- 供应商登录 → 仅能进入分配给自己的买家邮箱页并发货/删除。
- 点「删除」→ 确认 → 记录与图片消失。

- [ ] **Step 5: 提交**

```bash
git add src/WebMail/Pages/Shared/_ShipmentSection.cshtml src/WebMail/Pages/Supplier/Mail.cshtml src/WebMail/Pages/Admin/Buyers.cshtml
git commit -m "feat(shipment): add shipment UI section, modal, and admin mail entry"
```

---

### Task 8: 全量回归

- [ ] **Step 1: 跑全部测试**

Run: `dotnet test`
Expected: 全部 PASS（含新增 4 个测试类与既有测试）。

- [ ] **Step 2: 若有失败，定位并修复**

按失败信息修复；不得跳过或注释测试。修复后重跑直至全绿。

- [ ] **Step 3: 提交（如有修复）**

```bash
git add -A
git commit -m "test(shipment): fix regressions surfaced by full suite"
```

## Self-Review 记录

**Spec 覆盖核对：**
- 图片/描述/发货单ID/买家关联 → Task 1 实体；✓
- 图片存盘（wwwroot 外）+ 校验 → Task 3；✓
- 图片必须登录 + `/Shipment/Image/{id}` 鉴权 → Task 4；✓
- 雪花发货单ID（epoch 2024-01-01、worker 1）→ Task 2；✓
- 管理员+供应商增删、复用供应商邮箱页 → Task 5；✓
- 同页列出发货 → Task 7；✓
- 管理员行点击进入邮箱页 → Task 7 Step 3；✓
- 硬删除+审计 → Task 3；✓
- 既有库幂等建表 → Task 1 Step 5；✓
- 本地化 zh-CN/en → Task 6；✓
- 服务注册/策略/存储配置 → Task 2/3/5；✓
- 测试（雪花/服务/图片/页面授权）→ Task 2/3/4/5；✓

**类型一致性：** `ShipmentImageInput`/`ShipmentResult`/`Shipment` 字段、`MailModel` 构造与 handler 签名、`ShipmentAccess.CanAccessBuyerAsync` 在 Task 4/5 调用一致；图片接口路由 `/Shipment/Image/{id}` 与视图 `<img src>` 一致。

**无占位符：** 每步含可直接落地的代码/命令/预期输出。
