# 买家流程线 / 审核状态拆分 + 按卡免审核 设计

**日期：** 2026-06-29
**状态：** 草案（设计）

## 目标

1. 把现在分散在多个字段里的「买家走到哪一步」收敛成**一条流程线**，把「审核结论」独立成一列，让两者解耦。
2. 在此基础上支持**按卡密免审核**：管理员生成卡密时可勾选「免审核」，用这种卡的买家完成邮箱授权后**直接「通过」**，无需管理员手动审核。

不涉及历史数据迁移——程序尚未上线，DB 通过 `EnsureCreated` 重建即可（开发环境删库重建）。

## 背景：现有状态字段

`Buyer`（`Domain/Entities.cs`）当前承载五个状态相关字段：

| 字段 | 枚举值 | 关注点 |
|---|---|---|
| `CardStatus` | `Unused / Entered / Authorized / DeletedOrDisabled` | 买家用卡进度 |
| `CardSendStatus` | `NotSent / Sent` | 卡密是否分发给销售 |
| `EmailStatus` | `NotAuthorized / Authorized / Abnormal` | 邮箱授权与健康 |
| `BuyerStatus` | `NotSubmitted / PendingReview / Approved / Rejected` | 管理员审核 |
| `SupplierStatus` | `Unprocessed / Failed / Completed` | 供应商处理 |

问题：`CardSendStatus`、`CardStatus`、以及 `BuyerStatus` 的 `NotSubmitted` 三者共同描述「买家在管线上的位置」，分散在三个字段里；而 `BuyerStatus` 又把「位置（NotSubmitted）」和「审核结论（Pending/Approved/Rejected）」混在一起，导致无法把审核结论预置成「通过」。

补充事实（已核对）：

- `CardStatus.DeletedOrDisabled` 在全部源码中**从未被赋值**，仅在 `Verify.cshtml.cs:34` 有一处恒为 false 的死守卫。软删除由 `IsDeleted` 承担。删除 `CardStatus` 不损失任何在用语义。
- `EmailStatus.Abnormal` 是当初为「令牌失效不冲掉审核状态」而特意独立出来的，**保留不动**。
- `CardUsedAt`（首次授权时间）保留，卡密页的「是否使用」改由 `Stage==Submitted` 推导。

## 核心决策：流程线 + 审核 两个独立字段

### 新枚举

```csharp
// 流程线 — 吸收 CardSendStatus + CardStatus，描述买家在管线上的位置
public enum BuyerStage
{
    NotSent = 1,       // 未发送：卡密在库存，未分发给销售
    Sent = 2,          // 已发送：已分发给销售，买家还没录入卡密
    NotSubmitted = 3,  // 未提交：买家已录入卡密，但邮箱未授权
    Submitted = 4      // 已提交：买家完成邮箱授权
}

// 审核 — 由旧 BuyerStatus 去掉 NotSubmitted 而来，纯审核结论
public enum ReviewStatus
{
    Pending = 1,   // 待审核
    Approved = 2,  // 通过
    Rejected = 3   // 拒绝
}

// 邮箱授权与健康 — 不变
public enum EmailAuthorizationStatus { NotAuthorized = 1, Authorized = 2, Abnormal = 3 }

// 供应商 — 不变
public enum SupplierProcessingStatus { Unprocessed = 1, Failed = 2, Completed = 3 }
```

### `Buyer` 字段变化

- **删除** `CardStatus`、`CardSendStatus`。
- **新增** `BuyerStage Stage`（默认 `NotSent`）。
- **改名** `BuyerStatus BuyerStatus` → `ReviewStatus ReviewStatus`（默认 `Pending`）。
- **新增** `bool AutoApprove`（默认 `false`）——按卡免审核标记。
- 保留：`EmailStatus`、`SupplierStatus`、`CardNo`、`SaleId`、`CardUsedAt`、`CardSentAt`、`IsDeleted`、`CreatedAt`。

> `Stage` 与 `EmailStatus` 的关系：`Stage` 管「位置」，`EmailStatus` 管「邮箱健康」。授权前 `EmailStatus=NotAuthorized`；授权成功 `Stage→Submitted` 且 `EmailStatus=Authorized`；令牌失效仅 `EmailStatus→Abnormal`，`Stage`/`ReviewStatus` 不动。三者各管一件事，互不冲掉。

## 状态流转

| 事件 | 入口 | 结果 |
|---|---|---|
| 生成卡密（不选销售） | `CardKeyService.GenerateAsync` | `Stage=NotSent`, `ReviewStatus=Pending`, `EmailStatus=NotAuthorized`, `SupplierStatus=Unprocessed`, `AutoApprove=<勾选值>` |
| 生成卡密（选销售） | `CardKeyService.GenerateAsync` | 同上但 `Stage=Sent` |
| 发送给销售 | `CardKeyService`（发送逻辑，原回写 `CardSendStatus`） | `Stage=Sent`（仅当当前为 `NotSent`） |
| 买家录入卡密 | `Verify.OnGetAsync` | `Stage` `NotSent`/`Sent` → `NotSubmitted`（替代原 `CardStatus Unused→Entered`） |
| 买家完成邮箱授权（首次/换绑新邮箱） | `OAuth/Callback` | `Stage→Submitted`, `EmailStatus=Authorized`, **`ReviewStatus = AutoApprove ? Approved : Pending`**, `CardUsedAt ??= now` |
| 管理员审核 | `Admin/Buyers` | `ReviewStatus` `Pending → Approved/Rejected`（仅当 `Stage==Submitted && ReviewStatus==Pending`） |
| 供应商标记 | `Supplier/Buyers` | `SupplierStatus → Failed/Completed`（仅当 `ReviewStatus==Approved && EmailStatus==Authorized`，且分配给该供应商） |
| 令牌失效 | `MailSyncProcessor` | `EmailStatus Authorized → Abnormal`（`Stage`/`ReviewStatus`/`SupplierStatus` 不动） |
| 换邮箱 | `Buyer/Email` | `Stage→NotSubmitted`, `EmailStatus→NotAuthorized`, `ReviewStatus→Pending`, `SupplierStatus→Unprocessed`；移除 `EmailAccount`，保留 `EmailMessage`。重新授权后回到上表「完成授权」分支（免审核卡仍直接 `Approved`） |
| 清除授权（审核前：`Pending`/`Rejected`） | `Buyer/Email` | 回到起点：`Stage→NotSubmitted`, `EmailStatus→NotAuthorized`, `ReviewStatus→Pending`, `SupplierStatus→Unprocessed` |
| 清除授权（`Approved`+`Completed`） | `Buyer/Email` | `EmailStatus→NotAuthorized`，`Stage`/`ReviewStatus`/`SupplierStatus` 保持 → 终态「已完成并清空」 |
| 异常恢复（同邮箱重新授权） | `OAuth/Callback` | `EmailStatus→Authorized`，`Stage`/`ReviewStatus`/`SupplierStatus` 不动 |

### 免审核（AutoApprove）

- 是**按卡密**的属性，生成卡密时确定，存在 `Buyer.AutoApprove`。
- **粘性**：买家换邮箱、清授权后重新授权，只要还是这张卡，授权成功后 `ReviewStatus` 依旧直接 `Approved`。
- 管理员审核动作只对 `ReviewStatus==Pending` 生效。免审核卡跳过 `Pending`，管理员买家列表里不出现其审核按钮（它一进来就是「通过」）。

## 买家可执行动作（计算，`ResolveBuyerMailAction`）

输入由 `(IsDeleted, EmailStatus, Stage, ReviewStatus, SupplierStatus)` 决定：

| 条件 | 买家可 |
|---|---|
| `IsDeleted` | 无 |
| `EmailStatus==Abnormal` | 重新授权（同邮箱）/ 换邮箱 |
| `Stage==NotSubmitted` | 授权 |
| `Stage==Submitted && ReviewStatus∈{Pending, Rejected}` | 换邮箱 / 清授权 |
| `Stage==Submitted && ReviewStatus==Approved && SupplierStatus==Unprocessed` | 无（锁定「审核已通过，处理中」） |
| `Stage==Submitted && ReviewStatus==Approved && SupplierStatus==Failed` | 换邮箱 |
| `Stage==Submitted && ReviewStatus==Approved && SupplierStatus==Completed && EmailStatus==Authorized` | 清授权 |
| `Stage==Submitted && ReviewStatus==Approved && SupplierStatus==Completed && EmailStatus==NotAuthorized` | 无（终态「已完成并清空」） |
| `Stage∈{NotSent, Sent}` | 无（买家尚未录卡） |

## 权限规则（`BuyerRuleService`）

逻辑不变，仅替换判断字段：

```
CanSalesDeleteBuyer(buyer, salesUserId) =
    !IsDeleted
 && SaleId == salesUserId
 && EmailStatus != Abnormal
 && !(ReviewStatus == Approved && SupplierStatus == Unprocessed)   // 锁定/处理中不可删

CanSupplierViewBuyer(buyer, assignedSupplierId, currentSupplierId) =
    !IsDeleted
 && ReviewStatus == Approved
 && EmailStatus == Authorized
 && assignedSupplierId == currentSupplierId

CanSupplierSetStatus = CanSupplierViewBuyer
```

## 列表显示（两列）

Admin / Sales / Supplier 三个买家列表统一显示成两列：

- **流程列**（`Stage`）：未发送 / 已发送 / 未提交 / 已提交；当 `EmailStatus==Abnormal` 时在「已提交」旁加「邮箱异常」标记。
- **审核列**（`ReviewStatus`）：待审核 / 通过 / 拒绝；当 `Stage < Submitted` 时显示「—」。可选在免审核卡上加「免审」小标记。

筛选：

- `Admin/Buyers` 加载条件 `CardSendStatus==Sent` → 改为 `Stage != NotSent`（库存卡仍只在卡密页可见）。
- 状态筛选下拉由 `BuyerStatus` 改为按 `Stage` 与/或 `ReviewStatus` 过滤。
- 卡密页（`Admin/CardKeys`）：原按 `CardSendStatus`/`CardStatus` 的筛选与展示改用 `Stage`；「是否使用」= `Stage==Submitted`，「使用时间」= `CardUsedAt`。生成表单新增「免审核」勾选。

## 代码改动点

- **`Domain/Enums.cs`** — 删 `CardStatus`、`CardSendStatus`；加 `BuyerStage`；`BuyerStatus` 改名 `ReviewStatus` 并去掉 `NotSubmitted`。
- **`Domain/Entities.cs`** — `Buyer` 删 `CardStatus`/`CardSendStatus`，加 `BuyerStage Stage`、`bool AutoApprove`，`BuyerStatus` → `ReviewStatus`。
- **`Services/CardKeyService.cs`** — `GenerateAsync` 增 `bool autoApprove` 参数；写 `Stage`（NotSent/Sent）+ `AutoApprove`；发送逻辑写 `Stage=Sent`；`ListAsync` 投影/筛选由 `CardStatus`/`CardSendStatus` 改为 `Stage`；`CardKeyListItem`/`record` 字段同步。
- **`Services/BuyerRuleService.cs`** — `ResolveBuyerMailAction` 改用 `Stage`+`ReviewStatus`；`CanSalesDeleteBuyer`/`CanSupplierViewBuyer`/`CanSupplierSetStatus` 把 `BuyerStatus==Approved` 改为 `ReviewStatus==Approved`。
- **`Pages/Buyer/Verify.cshtml.cs`** — 录卡 `Stage` `NotSent/Sent → NotSubmitted`；移除 `CardStatus.DeletedOrDisabled` 死守卫（保留 `IsDeleted` 校验）。
- **`Pages/OAuth/Callback.cshtml.cs`** — 新绑定：`Stage→Submitted`、`EmailStatus=Authorized`、`ReviewStatus = AutoApprove ? Approved : Pending`、`CardUsedAt ??= now`；异常恢复分支只改 `EmailStatus`。
- **`Pages/Buyer/Email.cshtml(.cs)`** — 按新 `ResolveBuyerMailAction` 渲染；换邮箱/清授权处理器按上表写 `Stage`/`ReviewStatus`/`EmailStatus`/`SupplierStatus`。
- **`Pages/Admin/Buyers.cshtml(.cs)`** — 加载 `Stage != NotSent`；审核动作针对 `ReviewStatus==Pending`；显示流程列 + 审核列；筛选改 `Stage`/`ReviewStatus`。
- **`Pages/Admin/CardKeys.cshtml(.cs)`** — 生成表单加「免审核」勾选并传入 `GenerateAsync`；列表筛选/展示改 `Stage`。
- **`Pages/Sales/Buyers.cshtml`、`Pages/Supplier/Mail.cshtml`/`_BuyerTable.cshtml`** — 显示两列；删除/标记规则用新字段。
- **`Services/Background/MailSyncProcessor.cs`** — 异常分支仍只翻 `EmailStatus Authorized→Abnormal`（字段名核对）。
- **`Data/WebMailDbContext.cs`** — 列/枚举映射随字段调整；无关系变化。

## 测试

- **`BuyerRuleServiceTests`**：`ResolveBuyerMailAction` 逐条件（NotSubmitted→授权；Submitted+Pending/Rejected→换/清；Submitted+Approved 各 Supplier 分支；Abnormal→重新授权+换邮箱；NotSent/Sent→无）；`CanSalesDeleteBuyer`（可删集合 vs 锁定/异常；归属）；`CanSupplierSetStatus`/`CanSupplierViewBuyer`（Approved+Authorized+分配）。
- **`CardKeyServiceTests`**：生成写 `Stage`（选/不选销售）；`autoApprove=true` 写 `AutoApprove`；发送写 `Stage=Sent`；`ListAsync` 按 `Stage` 筛选。
- **`OAuthCallbackModelTests`**：
  - 新绑定 → `Stage=Submitted` + `EmailStatus=Authorized` + `ReviewStatus=Pending`（非免审核卡）。
  - **免审核卡新绑定 → `ReviewStatus=Approved`**（新增用例）。
  - 异常同邮箱重新授权 → `EmailStatus=Authorized`，`Stage`/`ReviewStatus` 不变。
  - 首次授权写 `CardUsedAt`，二次不覆盖。
- **`AdminBuyersModelTests`**：审核 `Pending→Approved/Rejected`、写审计；加载只含 `Stage != NotSent`；免审核卡（已是 `Approved`）不可再被审核动作改。
- **买家页处理器测试**：换邮箱重置为 `NotSubmitted/NotAuthorized/Pending/Unprocessed` 并保留消息；从 `Completed` 清授权 → 终态。
- **`MailSyncProcessorTests`**：`ProviderAuthorizationException` 翻 `Authorized→Abnormal`、`Stage`/`ReviewStatus` 不动；普通异常不设 Abnormal。

## 不在范围内

- 历史数据迁移（程序未上线，删库重建）。
- 令牌加密、增量同步等既有延后项继续延后。
- `SupplierStatus` 回退到 `Unprocessed`。
- 「免审核」按销售/全局维度（本次仅按卡）。
- 管理员对已「通过」（含免审核）买家的事后驳回（本次审核动作仅作用于 `Pending`）。
