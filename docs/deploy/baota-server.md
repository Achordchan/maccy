# Maccy 云同步后台（宝塔 + Docker）部署指南

本指南用于把 `nas-agent` 部署到普通云服务器，不依赖 NAS。

## 1. 前置条件

- 一台 Linux 云服务器（推荐 Ubuntu 22.04）。
- 已安装宝塔面板。
- 已安装 Docker 和 Docker Compose（宝塔应用商店可装）。
- 一个已解析到服务器的域名，例如 `sync.example.com`。
- 服务器安全组仅开放 `80` 和 `443`。

## 2. 准备部署目录

建议目录：

```bash
mkdir -p /www/wwwroot/maccy-agent
```

把仓库中的整个 `nas-agent` 目录上传到服务器的 `/www/wwwroot/maccy-agent/nas-agent`。

最终目录结构建议如下：

```text
/www/wwwroot/maccy-agent
  └─ nas-agent/
     ├─ Dockerfile
     ├─ Program.cs
     ├─ NasAgent.csproj
     └─ deploy/baota/
        ├─ docker-compose.yml
        ├─ .env
        ├─ data/
        └─ nginx.conf.example
```

## 3. 配置环境变量

在 `/www/wwwroot/maccy-agent/nas-agent/deploy/baota` 下复制模板并修改：

```bash
cp .env.example .env
```

必须修改：

- `ADMIN_KEY`：改成强随机字符串。
- `AUTH_ISSUER`：你的 Authing Issuer。
- `AUTH_METADATA_ADDRESS`：你的 Authing Metadata 地址。

示例（不要直接照抄密钥）：

```env
TZ=Asia/Shanghai
ADMIN_KEY=8wY8nG9rX9YjE4...
AUTH_ISSUER=https://achord-maccy.authing.cn/oidc
AUTH_METADATA_ADDRESS=https://achord-maccy.authing.cn/oidc/.well-known/openid-configuration
```

## 4. 启动服务

在 `/www/wwwroot/maccy-agent/nas-agent/deploy/baota` 下执行：

```bash
docker compose up -d --build
docker compose ps
```

健康检查：

```bash
curl http://127.0.0.1:17655/health
```

预期返回：

```json
{"ok":true}
```

## 5. 宝塔配置反向代理 + HTTPS

1. 在宝塔创建网站：`sync.example.com`。
2. 在网站设置中添加反向代理，目标地址填：`http://127.0.0.1:17655`。
3. 申请 Let’s Encrypt 证书并开启强制 HTTPS。

可参考 `nas-agent/deploy/baota/nginx.conf.example` 的反代配置。

## 6. 客户端接入

在 Maccy 设置里填写：

- `NasAgentBaseUrl`：`https://sync.example.com`

然后登录并点击同步按钮测试。

## 7. 后台管理入口

- 管理页地址：`https://sync.example.com/admin`
- 现在管理页不再写死密钥，会提示输入 `Admin Key`。
- 第一次输入后会存到浏览器 `localStorage`。
- 如需更换密钥，浏览器里清掉对应站点存储，或在控制台执行：

```js
localStorage.removeItem('maccy_admin_key')
```

## 8. 常用运维命令

```bash
# 查看日志
docker compose logs -f

# 重启
docker compose restart

# 更新代码后重新构建
docker compose up -d --build
```

## 9. 安全建议

- 不要把 `17655` 暴露到公网；当前 compose 已绑定 `127.0.0.1`。
- `ADMIN_KEY` 必须用随机强密钥，严禁默认值。
- 定期备份目录 `/www/wwwroot/maccy-agent/nas-agent/deploy/baota/data`（包含订阅和快照数据）。
