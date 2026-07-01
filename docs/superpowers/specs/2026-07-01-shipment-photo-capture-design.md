# 发货拍照上传（Shipment Photo Capture）设计文档

- 日期：2026-07-01
- 状态：已与需求方确认，待实现
- 范围：在「增加发货」弹窗里把现有的单文件上传框，改为「拍照 / 选择图片」两个入口，使手机（自带浏览器）上可直接调起后置摄像头拍照上传。仅前端 + 本地化文案改动，**不改后端**。

## 1. 背景与目标

现状「增加发货」弹窗（`Pages/Shared/_ShipmentSection.cshtml:67`）只有一个：

```html
<input type="file" name="image" accept="image/*" class="form-control" required />
```

- 桌面：文件选择框 ✅
- 手机自带浏览器：通常会在选择菜单里给「拍照」选项，但不显眼；部分机型只显示「选择文件」，体验不佳。

需求方希望手机上能**直接拍照上传**。确认使用环境为**手机自带浏览器为主**（非微信内置浏览器 / 非 WebView），因此标准 HTML `capture` 属性可可靠调起摄像头，无需微信 JSSDK。

目标：在不改后端的前提下，给手机用户提供显式、可靠的「拍照」入口，同时保留「选择图片」（相册/文件）入口。

## 2. 关键决策

1. **方案 B：两个按钮并存**——「拍照」按钮带 `capture="environment"`（手机直开后置摄像头），「选择图片」按钮不带 `capture`（可从相册/文件选）。两个 `<input type="file">` 共用 `name="image"`：浏览器不提交未选文件的 input，故仅被选中的那个成为 `image` 提交给 `OnPostAddShipmentAsync(IFormFile? image)`，后端绑定零改动。
2. **`capture` 仅作用于「拍照」按钮**：iOS Safari 下带 `capture` 的 input 直进相机、不进相册，正是该按钮的预期行为；「选择图片」按钮不带 `capture` 以保留相册/文件入口。桌面浏览器忽略 `capture`，两按钮都退化为普通文件选择框，行为一致、互不影响。
3. **去掉原生 `required`，改 JS 提交校验**：两个同名 input 无法用原生 `required` 表达「至少选一个」。服务端本就要求图片（`ShipmentService.CreateAsync` 对 null 图片返回 `Shipment.InvalidImage`，见 `Services/ShipmentService.cs:46`），前端提前校验更友好，避免无图时整页跳转后才看到错误。
4. **加文件名/缩略图预览**：两个 input 均 `hidden`（用 `<label class="btn">` 包裹做按钮样式），用户选完看不到默认文件名。选完即用 `URL.createObjectURL` 显示缩略图 + 文件名，确认选择生效。
5. **选了一个就清空另一个**：避免两个 input 都有文件时，服务端只取其一的歧义。
6. **不改体积/类型限制**：服务端现限制 5MB、仅 jpeg/png/webp/gif（`Services/ShipmentService.cs:14-21`）。**需求方确认维持现状，不做前端压缩、不调上限**。已接受的权衡：手机拍照常 6~12MB，超过 5MB 会被服务端以 `Shipment.InvalidImage`（「图片无效」）拒掉——届时用户需重拍或换图。此限制不在本次改动范围。
7. **JS 放进现有 `wwwroot/js/site.js`**，用 `#addShipmentModal` 存在性守卫，纯原生 JS（不依赖 jQuery，虽 `_Layout.cshtml:96` 已加载 jQuery）。`site.js` 现为空占位文件（`wwwroot/js/site.js`），在 `_Layout.cshtml:98` 全局加载、位于 body 末尾，运行时 DOM 与 jQuery 均就绪。无 CSP（`Program.cs` 仅有 `UseStaticFiles()`）。
8. **本地化文案**：复用现有 `SharedResource.zh-CN.resx` / `SharedResource.en.resx` 与 `@L["..."]` 机制，紧接现有 `Shipment.*` 块追加 3 个 key。

## 3. 现有结构（事实依据）

- 弹窗与上传框：`Pages/Shared/_ShipmentSection.cshtml:55-80`（modal）、`:67`（file input）。`<form method="post" enctype="multipart/form-data" asp-page-handler="AddShipment">`。
- 提交 handler：`Pages/Supplier/Mail.cshtml.cs:88` `OnPostAddShipmentAsync(long buyerId, string? description, IFormFile? image)`——`image` 可空，无图时 `input=null` 传入 service。
- 服务端校验与存储：`Services/ShipmentService.cs:14`（`MaxBytes = 5MB`）、`:15-21`（`AllowedTypes`：jpeg/png/webp/gif）、`:46-50`（null 或超限或类型不符 → 返回 `Shipment.InvalidImage`）。
- 布局脚本：`Pages/Shared/_Layout.cshtml:96-100`（jQuery → bootstrap bundle → `site.js` → `RenderSectionAsync("Scripts")`）。`_ShipmentSection.cshtml` 是 partial，无法定义 `Scripts` section，故 JS 走全局 `site.js`。
- 本地化：`Resources/SharedResource.zh-CN.resx:172-184`、`Resources/SharedResource.en.resx:172-184`（现有 `Action.AddShipment` 与 `Shipment.*` 块）。

## 4. UI 改动

`_ShipmentSection.cshtml` 弹窗内，把第 66-68 行的：

```html
<div class="mb-3">
  <label class="form-label">@L["Shipment.Image"]</label>
  <input type="file" name="image" accept="image/*" class="form-control" required />
</div>
```

替换为：

```html
<div class="mb-3">
  <label class="form-label">@L["Shipment.Image"]</label>
  <div class="d-flex gap-2 flex-wrap mb-2">
    <label class="btn btn-outline-primary mb-0">
      📷 @L["Shipment.TakePhoto"]
      <input type="file" name="image" accept="image/*" capture="environment" hidden />
    </label>
    <label class="btn btn-outline-secondary mb-0">
      🖼 @L["Shipment.ChooseImage"]
      <input type="file" name="image" accept="image/*" hidden />
    </label>
  </div>
  <div class="shipment-image-preview d-none">
    <img class="rounded mb-1" style="max-height:96px;max-width:128px;object-fit:cover;" alt="" />
    <small class="d-block text-muted shipment-image-name"></small>
  </div>
</div>
```

- 两个 input 共用 `name="image"`、均无 `required`。
- 预览容器初始 `d-none`，JS 选完图后显示。

## 5. JS 放入 `wwwroot/js/site.js`

在文件末尾追加（文件现有内容仅为注释占位）：

```js
// 发货拍照/选图：两个同名 input 互斥 + 预览 + 提交前必填校验
(function () {
  var modal = document.getElementById('addShipmentModal');
  if (!modal) return;
  var form = modal.querySelector('form');
  var inputs = Array.prototype.slice.call(form.querySelectorAll('input[type="file"][name="image"]'));
  var preview = modal.querySelector('.shipment-image-preview');
  var previewImg = preview && preview.querySelector('img');
  var previewName = preview && preview.querySelector('.shipment-image-name');
  var currentUrl = null;

  function showPreview(file) {
    if (currentUrl) URL.revokeObjectURL(currentUrl);
    currentUrl = file ? URL.createObjectURL(file) : null;
    if (file) {
      previewImg.src = currentUrl;
      previewName.textContent = file.name;
      preview.classList.remove('d-none');
    } else {
      previewImg.src = '';
      previewName.textContent = '';
      preview.classList.add('d-none');
    }
  }

  inputs.forEach(function (input) {
    input.addEventListener('change', function () {
      // 选了一个就清空另一个
      inputs.forEach(function (other) { if (other !== input) other.value = ''; });
      showPreview(input.files && input.files[0]);
    });
  });

  form.addEventListener('submit', function (e) {
    var has = inputs.some(function (i) { return i.files && i.files.length > 0; });
    if (!has) {
      e.preventDefault();
      alert(modal.getAttribute('data-image-required'));
    }
  });
})();
```

- 弹窗上需带 `data-image-required="@L["Shipment.ImageRequired"]"`（在 modal 的 `data-bs-` 同级加一个普通 data 属性），供 `alert` 取文案。故 `_ShipmentSection.cshtml` 的 modal 根 `<div class="modal fade" ...>` 需追加 `data-image-required="@L["Shipment.ImageRequired"]"`。

## 6. 本地化文案

紧接现有 `Shipment.*` 块追加：

| key | zh-CN | en |
|---|---|---|
| `Shipment.TakePhoto` | 拍照 | Take Photo |
| `Shipment.ChooseImage` | 选择图片 | Choose Image |
| `Shipment.ImageRequired` | 请拍摄或选择一张图片 | Please take or select a photo |

## 7. 测试

- 手动验证（手机自带浏览器）：点「拍照」→ 后置摄像头开启 → 拍照 → 预览显示 → 提交成功，发货列表出现新记录与图片。
- 手动验证：点「选择图片」→ 可从相册选 → 预览显示 → 提交成功。
- 互斥：先选拍照、再点选择图片，前者被清空，预览更新为后者。
- 必填：两个都没选直接提交 → `alert` 提示，不跳转。
- 桌面浏览器：两按钮都打开文件选择框，提交正常（`capture` 被忽略）。
- 超限回归：上传 >5MB 图 → 服务端返回 `Shipment.InvalidImage`（现有行为，未改）。
- 现有自动化测试不覆盖该 UI；后端无改动，`SupplierMailModelTests` 等不受影响。

## 8. 不在范围

- 不做前端图片压缩 / 不上调 5MB 限制（需求方确认）。
- 不改后端 handler、service、存储、图片接口。
- 不引入第三方库或新增 JS 文件（复用 `site.js`）。
- 不做微信内置浏览器 / WebView 适配（使用环境为自带浏览器）。
