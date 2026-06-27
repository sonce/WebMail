# 多语言支持设计（英文 / 简体中文）

日期：2026-06-28
状态：已批准设计，待实现

## 目标

为 WebMail（ASP.NET Core 8.0 Razor Pages 应用）添加多语言支持，覆盖英文（`en`）与简体中文（`zh-CN`）。当前界面文字为中英混杂的硬编码，无任何本地化基础设施。

## 关键决策

1. **语言选择方式**：自动检测浏览器语言（Accept-Language）+ 导航栏手动切换，选择存入 Cookie 持久化。
2. **翻译范围**：界面框架文字（导航、按钮、菜单、表单标签、页面标题）+ 校验/系统消息。**不翻译**邮件主题/正文、买家名称、邮箱地址等业务数据。
3. **语言与回退**：支持 `en` 与 `zh-CN`，**默认回退语言为 `en`**。
4. **技术方案**：ASP.NET Core 官方本地化（`IStringLocalizer` / `IViewLocalizer` + `.resx`）+ **单一共享资源**（Shared Resource）。

## 架构

### 中间件与服务配置（`Program.cs`）

- `builder.Services.AddLocalization(o => o.ResourcesPath = "Resources")`
- `builder.Services.AddRazorPages().AddViewLocalization().AddDataAnnotationsLocalization()`
- 配置 `RequestLocalizationOptions`：
  - 支持语言列表：`en`、`zh-CN`
  - `DefaultRequestCulture = new RequestCulture("en")`
  - `RequestCultureProviders` 顺序：
    1. `CookieRequestCultureProvider`（手动切换写入的 `.AspNetCore.Culture` Cookie，最高优先级）
    2. `AcceptLanguageHeaderRequestCultureProvider`（浏览器自动检测）
    3. 都无匹配时回退默认 `en`
- 管道中调用 `app.UseRequestLocalization(...)`，放在 `app.UseRouting()` 之前。

### 资源文件组织

- 新建目录 `src/WebMail/Resources/`。
- 新建空标记类 `SharedResource`（命名空间 `WebMail`，文件 `src/WebMail/SharedResource.cs`），作为 `IStringLocalizer<SharedResource>` 的资源锚点。
- 资源文件：
  - `src/WebMail/Resources/SharedResource.en.resx`
  - `src/WebMail/Resources/SharedResource.zh-CN.resx`
- 两个文件的 key 集合必须完全一致。

### Key 命名约定

- 语义化、按区域用点号分组。示例：
  - 导航：`Nav.Home`、`Nav.AdminConsole`、`Nav.SalesConsole`、`Nav.SupplierConsole`、`Nav.Logout`、`Nav.Login`
  - 通用：`Common.Save`、`Common.Cancel`、`Common.Success`、`Common.Failed`
  - 页面专属：`Login.Title`、`Admin.Suppliers.Title` 等
- 不使用中文原文作为 key（key 用语义英文标识符），便于代码可读与维护。

### 语言切换器（UI）

- 在 `Pages/Shared/_Layout.cshtml` 导航栏右侧增加一个下拉：**中文 / English**，高亮当前语言。
- 新建轻量页面 `Pages/Culture/Set.cshtml`（仅 `OnGet` 处理器，`[AllowAnonymous]`）：
  - 入参 `culture`（`en` 或 `zh-CN`）与 `returnUrl`。
  - 校验 `culture` 在支持列表内；校验 `returnUrl` 为本地 URL（防开放重定向），否则回退到 `/`。
  - 写入 `CookieRequestCultureProvider.DefaultCookieName`（`.AspNetCore.Culture`）Cookie，重定向回 `returnUrl`。
- `_Layout.cshtml` 中 `<html lang="en">` 改为输出当前请求语言（`CultureInfo.CurrentUICulture.Name`）。

## 数据流

1. 请求进入 → `RequestLocalizationMiddleware` 依序询问 providers，确定 `CurrentUICulture`。
2. 视图/页面模型通过注入的 `IStringLocalizer<SharedResource>` 按 key 取当前语言文本；缺失时回退英文，再缺失返回 key 本身。
3. 用户点击切换器 → `GET /Culture/Set?culture=zh-CN&returnUrl=...` → 写 Cookie → 重定向 → 下次请求 Cookie provider 命中。

## 现有硬编码文字迁移

涉及约 25 个含中文的源文件（视图与页面模型/服务，排除 `obj/` 构建产物）：

- 视图 `.cshtml`：`@inject IStringLocalizer<SharedResource> L`，将硬编码文字替换为 `@L["key"]`。
- 页面模型/服务 `.cs`：构造函数注入 `IStringLocalizer<SharedResource>`，将返回给用户的消息替换为 `L["key"]`。
- `zh-CN.resx` 填入现有中文原文；`en.resx` 填入对应英文翻译。
- 现存少量纯英文 UI 文字（`Home`、`Privacy`、`Login` 等）同样纳入 key，并补充中文翻译。
- 表单数据注解校验消息通过 `AddDataAnnotationsLocalization` 解析到共享资源。

### 明确不在范围内

- 邮件主题、正文等邮箱业务内容。
- 买家/供应商名称、邮箱地址等用户业务数据。
- 日志文本、异常堆栈等面向开发者的内容（可保留英文）。

## 错误处理

- 切换器收到不支持的 `culture` → 忽略，保持当前语言。
- `returnUrl` 非本地 URL → 重定向到 `/`。
- 资源 key 缺失 → 官方 localizer 自动回退默认语言；最终回退返回 key 字符串（不会抛异常）。

## 测试（`tests/WebMail.Tests`）

1. **语言解析**：
   - 带 `.AspNetCore.Culture` Cookie 时按 Cookie 解析。
   - 无 Cookie、带 `Accept-Language: zh-CN` 时解析为 `zh-CN`。
   - 两者皆无时回退 `en`。
2. **切换处理器**：
   - 合法 `culture` + 本地 `returnUrl` → 写对 Cookie 并重定向到 `returnUrl`。
   - 非本地 `returnUrl` → 重定向到 `/`。
   - 不支持的 `culture` → 不切换。
3. **资源一致性**：`SharedResource.en.resx` 与 `SharedResource.zh-CN.resx` 的 key 集合完全相等（防漏翻）。
4. 全程以 `dotnet build` 与 `dotnet test` 验证通过。

## 不做的事（YAGNI）

- 不支持繁体中文或其他语言（仅 `en` + `zh-CN`）。
- 不做按用户账号存储语言偏好（仅 Cookie + 浏览器检测）。
- 不翻译邮件等业务数据，不接入机器翻译。
- 不做每页独立 `.resx`（统一共享资源）。
