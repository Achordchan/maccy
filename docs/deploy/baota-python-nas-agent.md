# 宝塔 Python 项目部署 `nas-agent-py`

## 目录建议

- 代码目录：`/www/wwwroot/maccy-agent/current`
- 数据目录：`/www/wwwroot/maccy-agent/data`
- 虚拟环境：`/www/wwwroot/maccy-agent/venv`
- 历史发布：`/www/wwwroot/maccy-agent/releases`

## 宝塔网站

1. 新建网站，域名继续使用现有同步域名。
2. 网站根目录可指向 `/www/wwwroot/maccy-agent/current`。
3. SSL 继续由宝塔网站管理。
4. 反向代理目标固定为 `http://127.0.0.1:17655`。

## Python 项目

1. 将 `nas-agent-py/` 的发布文件上传到 `/www/wwwroot/maccy-agent/current`。
2. 在宝塔 Python 项目中选择该目录，并创建虚拟环境。
3. 安装依赖：
   `pip install -r requirements.txt`
4. 在项目环境变量里配置：
   - `STORAGE_ROOT=/www/wwwroot/maccy-agent/data`
   - `ADMIN_KEY=你的后台密钥`
   - `AUTH_ISSUER=maccy-self-hosted`
   - `AUTH_AUDIENCE=maccy-client`
   - `AUTH_SIGNING_KEY=迁移现有环境时保持和之前服务一致的签名密钥`
   - `LISTEN_HOST=127.0.0.1`
   - `LISTEN_PORT=17655`
5. 启动命令：
   `sh run.sh`

## 反向代理建议

- 保持长超时：
  - `proxy_connect_timeout 60s`
  - `proxy_send_timeout 300s`
  - `proxy_read_timeout 300s`
- 允许大文件上传：
  - `client_max_body_size 0;`

## 上线步骤

1. 准备好 `subscriptions.db` 和用户快照目录。
2. 启动 Python 服务。
3. 先通过本机端口验证：
   - `curl http://127.0.0.1:17655/health`
   - `python tests/compat_smoke.py --base-url http://127.0.0.1:17655 --skip-upload`
4. 验证通过后，把宝塔反向代理指向 `127.0.0.1:17655`。
5. 再执行一次完整 smoke：
   `python tests/compat_smoke.py --base-url https://你的域名`

## 回滚

1. 停掉宝塔 Python 项目。
2. 恢复上一版上传目录或历史发布目录。
3. 如果 `AUTH_SIGNING_KEY` 未变，数据库和快照目录都不需要额外迁移。
