# WebMail

ASP.NET Core 8 (Razor Pages) 应用，用于管理买家邮箱授权与供应商邮件同步。集成 Gmail 与 Outlook OAuth，SQLite 存储，后台定时同步邮件。

- **运行时**：.NET 8
- **数据库**：SQLite（文件数据库，无需单独部署）
- **前端**：Razor Pages + 本地化
- **OAuth**：Gmail、Outlook（token 用 DataProtection 加密存储）

## 本地开发

```bash
dotnet build WebMail.sln          # 构建
dotnet test WebMail.sln           # 跑测试
dotnet run --project src/WebMail  # 启动
```

开发配置见 `src/WebMail/appsettings.json`，密钥可用 user-secrets 管理。

## Docker 部署（推荐）

采用「服务器本地构建」方案：把代码放到服务器，`docker compose` 直接构建启动。SQLite 数据库与 DataProtection 密钥都落在 Docker 命名卷里，容器重建不丢失。

### 目录结构

```
Dockerfile              # 多阶段构建（SDK 编译 → aspnet 运行时）
.dockerignore
docker-compose.yml      # 服务、端口、卷、环境变量声明
.env.example            # 密钥模板（复制为 .env 填写，.env 已在 .gitignore 中）
```

### 首次部署

```bash
# 1. 拉代码到服务器
git clone <仓库地址> WebMail && cd WebMail

# 2. 配置密钥
cp .env.example .env
vim .env        # 填 GOOGLE_CLIENT_SECRET / OUTLOOK_CLIENT_SECRET / ADMIN_PASSWORD / WEBMAIL_PUBLIC_BASE_URL

# 3. 构建并启动
docker compose up -d --build
```

`.env` 需要填的变量（被 `docker-compose.yml` 引用）：

| 变量 | 对应配置键 | 说明 |
|---|---|---|
| `GOOGLE_CLIENT_SECRET` | `GoogleOAuth:ClientSecret` | Google OAuth Client Secret |
| `OUTLOOK_CLIENT_SECRET` | `OutlookOAuth:ClientSecret` | Outlook OAuth Client Secret |
| `ADMIN_PASSWORD` | `Seed:AdminPassword` | 首次播种的管理员密码（用户名默认 `admin`） |
| `WEBMAIL_PUBLIC_BASE_URL` | `WebMail:PublicBaseUrl` | 公网地址，Outlook 后台刷新 token 构建回调用 |

其余配置已在镜像内写好或读 `appsettings.json` 默认值，无需手动设：

| 配置键 | 值 | 来源 |
|---|---|---|
| `ConnectionStrings:Default` | `/app/data/webmail.db` | compose 写死（落在数据卷） |
| `DataProtection:KeysPath` | `/app/data/dpkeys` | compose 写死（落在数据卷） |
| `Shipments:StoragePath` | `/app/data/shipments` | compose 写死（发货图片，落在数据卷） |
| `GoogleOAuth:RedirectUri` | `https://webmail.example/oauth/callback` | `appsettings.Production.json` |
| `MailSync:InitialSyncDays` | `30` | `appsettings.json` |
| `MailSync:CacheTtlSeconds` | `30` | `appsettings.json` |
| `Seed:AdminUserName` | `admin` | `appsettings.json` |

> 要覆盖某个默认值：在 `.env` 加一行 `VAR=value`，并在 `docker-compose.yml` 的 `environment:` 下加一行 `配置__键: "${VAR}"`（`__` 对应 `:` 层级）。

> `appsettings.Production.json` 里的 `RedirectUri` 必须与 Google OAuth 后台登记的回调地址一致。

### 常用命令

```bash
docker compose up -d --build    # 构建并启动（更新代码后也是这条）
docker compose logs -f          # 实时日志
docker compose restart          # 重启容器
docker compose stop             # 停止（保留数据）
docker compose down             # 删容器和网络，保留 ./data 数据
# 要彻底清空数据：docker compose down 后手动删除宿主机的 ./data 目录
```

日常更新就两条：`git pull` → `docker compose up -d --build`。数据和密钥都保留。

## Nginx 反向代理 + HTTPS

容器只监听 `127.0.0.1:8080`，对外由 Nginx 反代并签 HTTPS 证书（OAuth 回调要求 HTTPS）。

```nginx
server {
    server_name webmail.example;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

```bash
sudo certbot --nginx -d webmail.example   # 自动签 Let's Encrypt 证书并配 HTTPS
```

应用已开启 `ForwardedHeaders`，会信任反代传来的 `X-Forwarded-Proto`，HTTPS 重定向不会死循环。

## 数据与持久化

宿主机 `./data/` 目录（compose 里 `./data:/app/data`）挂载进容器，持久化以下内容：

- **SQLite 数据库**：`/app/data/webmail.db`
- **DataProtection 密钥**：`/app/data/dpkeys/`——加密 OAuth token 与登录 cookie 用，**必须持久化**，否则容器重建后已授权的邮箱 token 全部解不开、用户需重新登录
- **发货图片**：`/app/data/shipments/`——上传的发货单附件，容器重建不丢

`docker compose down` 不删 `./data/`；要彻底清空需手动删该目录。

## 项目结构

```
src/WebMail/
├── Data/            # EF Core DbContext 与实体
├── Domain/          # 领域模型
├── Services/
│   ├── Auth/        # 身份与授权
│   ├── Background/  # 后台邮件同步任务
│   ├── EmailProviders/  # Gmail / Outlook 适配
│   ├── Security/    # DataProtection token 保护、OAuth 状态
│   └── Localization/
├── Pages/           # Razor Pages
└── Program.cs
```

## 故障排查

- **`docker compose up` 后访问不通**：先 `docker compose logs` 看是否启动成功；再确认 Nginx 反代指向 `127.0.0.1:8080`。
- **OAuth 回调报错 redirect_uri_mismatch**：核对 OAuth 后台登记的回调地址与 `appsettings.Production.json` 中的 `RedirectUri` 完全一致（含 `?provider=Gmail` 查询串）。
- **容器重建后登录态/邮箱授权失效**：说明 DataProtection 密钥没持久化，检查 `DataProtection__KeysPath` 是否指向挂载卷内的目录。
- **更新代码后没生效**：构建时漏了 `--build`。用 `docker compose up -d --build`，否则会用旧镜像。
