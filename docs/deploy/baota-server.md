# Maccy 云同步后端部署

旧的 `.NET + Docker` 部署方式已经废弃，不再使用。

当前后端统一改为 Python 版 `nas-agent-py`，宝塔部署请直接查看：

- [宝塔 Python 项目部署指南](/E:/test/maccy/docs/deploy/baota-python-nas-agent.md)

如果你是在清理旧环境，直接删除这些旧后端产物即可：

- `nas-agent/`
- 旧 Docker 镜像与容器
- 旧宝塔反向代理中指向 `.NET` 服务的目标

当前推荐结构是：

- `nas-agent-py/` 作为唯一后端代码目录
- 宝塔网站负责域名、SSL 和反向代理
- 宝塔 Python 项目运行 `uvicorn`
