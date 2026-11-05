[简体中文](README.md) | [English](README.en.md)

# Windows 便携版启动器

本目录包含 Windows 试玩包的 Backend 启动、停止、配置重启和打包脚本。普通玩家只需双击试玩包根目录的 **「启动游戏.cmd」**（或 `Start.cmd`），并按照包内 `使用说明.html` 配置模型。

## 启动流程

统一启动入口完成以下工作：

1. 定位当前试玩包，将用户数据目录设置为包内 `UserData`。
2. 检查所需资源和目录写入权限，为 Backend 选择本机可用端口。
3. 启动捆绑的 Python Backend，等待其监听端口就绪。
4. 启动 `UnityPlayer/EmbodiedNPC.exe`，并传入匹配的 WebSocket 和日志路径。
5. 游戏结束后，按已记录的进程所有权停止配套 Backend。

游戏内 TitleScene 继续负责 Main/Embedding 配置、连接与就绪检查。启动器本身不代替模型配置。

## 目录与进程

| 路径 | 用途 |
| --- | --- |
| `Backend/app`、`Backend/python` | 后端程序与 Python Runtime |
| `Backend/models/embeddinggemma-300m-q4` | 内置向量模型 |
| `UserData/Backend` | 记忆数据库、模型配置与知识数据 |
| `UserData/runtime` | 私有运行状态、锁与本机会话 |
| `UserData/logs` | Backend、Unity Player 日志 |

默认使用系统分配的本机端口，Backend 和 Unity Player 共享同一地址及授权会话。文件锁与进程句柄用于识别本次启动的实例，确保停止指令作用于自己的服务。

## 维护命令

技术人员可以在打包目录使用：

```text
Backend\start.cmd --port <local-port>
Backend\stop.cmd
Backend\restart.cmd --port <local-port>
```

试玩包根目录 `Stop.cmd` 用于游戏关闭后的受控恢复，正常使用可直接关闭游戏并让启动器完成清理。

## 模型配置重启

游戏设置中保存并激活新的 Main/Embedding 配置后，如果需要重启，TitleScene 可请求当前启动器执行**一次受控退出、Backend 重启和游戏重开**，保留同一 UserData。

该过程使用既有进程与目录归属，重启后继续由 TitleScene 检查模型连接。

## 打包与备份

`assemble.py` 接收 Python Runtime、模型资源和 Unity 构建目录，组装新的便携包。打包时包含许可和资源说明，用户数据与个人密钥保留在本机：

```text
assemble.py --destination <folder> --python-runtime <python-dir> --models <model-dir> --player <build-dir>
```

完整 ZIP 可以使用 `--zip <new-file.zip>` 生成。备份存档时，请正常退出游戏并停止 Backend 后复制完整 `UserData`，保留 SQLite 的相关文件。

[项目首页](../../README.md) · [Backend](../README.md) · [源码安装](../../Docs/SOURCE_SETUP.md)
