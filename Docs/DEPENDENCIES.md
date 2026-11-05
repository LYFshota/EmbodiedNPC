[简体中文](DEPENDENCIES.md) | [English](DEPENDENCIES.en.md)

# 依赖与运行环境

项目使用 **Unity 6000.5.10f1** 和 **Python 3.12 x64**，Windows 开发环境可按照 [源码运行说明](SOURCE_SETUP.md)配置。

## Python 后端

依赖以 [requirements.txt](../AIBackend/requirements.txt) 为准，在独立虚拟环境中安装：

```powershell
cd AIBackend
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m pip check
```

主要组件包括 FastAPI、Uvicorn、HTTPX、Pydantic、FastEmbed 与 ONNX Runtime。确切版本以安装时的依赖解析结果为准。

## Unity

Unity 包依赖由 [manifest.json](../UnityClient/Packages/manifest.json) 和 [packages-lock.json](../UnityClient/Packages/packages-lock.json) 管理，包括 URP、Input System、AI Navigation、Test Framework 和 UniVRM。

首次导入需要联网恢复依赖。按仓库已有的锁文件安装即可，具体操作参见 [源码运行说明](SOURCE_SETUP.md)。

## 模型资源

Windows 试玩包已包含默认 Embedding 资源；源码运行需要按 [资源准备说明](SOURCE_SETUP.md#内置-embedding-资源)另行准备，主语言模型由使用者配置。

第三方依赖与资源许可见 [THIRD_PARTY_NOTICES](../THIRD_PARTY_NOTICES.md)。
