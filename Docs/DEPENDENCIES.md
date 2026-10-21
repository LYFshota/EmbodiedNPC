# 依赖与已观测环境

本页记录真实环境，不升级或修改依赖解析规则。安装声明仍为 [AIBackend/requirements.txt](../AIBackend/requirements.txt)；其范围依赖未来可能解析为其他版本，本页**不是跨平台完整锁文件，也不是净环境安装验收**。

## Windows Backend（2026-10-19）

从本地 `AIBackend/.venv` 的 Python 和发行包元数据只读取得：Python **3.12.7 x64**，Windows 11 x64。该环境执行过 S03 的 38 项 WS/失败解释测试及 8 项 S02 lifespan/诊断兼容测试；本轮仅复核元数据和 `pip check`（无依赖冲突），未重新运行模型或完整回归。

| 包 | 实际安装版本 | 作用 |
| --- | --- | --- |
| fastapi | 0.141.1 | HTTP / WebSocket 应用 |
| starlette | 1.6.0 | ASGI / WS（FastAPI 依赖） |
| uvicorn | 0.52.4 | ASGI 服务进程 |
| httpx | 0.28.1 | Provider HTTP |
| jsonschema | 4.26.0 | JSON Schema 校验 |
| pydantic | 2.13.5 | 直接使用 v2 的 model_validate / ConfigDict 等 API |
| fastembed | 0.8.0 | 现有 Embedding 依赖，requirements 中已固定 |
| numpy | 2.5.3 | 内置 Embedding 向量运算 |
| tokenizers | 0.23.2 | 内置 tokenizer |
| onnxruntime | 1.30.0 | 内置 ONNX CPU 推理 |
| huggingface-hub | 1.31.0 | 现有传递依赖 |

后四项的安装不代表本轮运行了模型。Pydantic、numpy、tokenizers 和 onnxruntime 是第一方代码直接导入的包，当前部分依赖由上游传递安装；修改其声明或固定版本应另做净目录安装、导入、定向测试、离线 ONNX 和 Portable 验证。本轮没有据此调整 requirements。

需要制作可复现运行环境时，应在**实际验收使用的环境**导出完整清单、保存 Python/平台信息和 wheel 来源，并在新目录验证后作为对应发行物的锁定依据；不要把开发机快照写成已验收 Portable Runtime。可用 `python -m pip freeze` 记录发行版本，但不要把包含私有 URL/本地路径的输出直接提交。库、原生 DLL 和模型各自保留许可，见[许可范围](../THIRD_PARTY_NOTICES.md)。

## Unity

Editor **6000.5.10f1**，以 `ProjectVersion.txt` 为准。Unity 包由 `UnityClient/Packages/manifest.json` 和 `packages-lock.json` 记录，包括 URP 17.5.0、Input System 1.20.0、AI Navigation 2.0.14、Test Framework 1.7.0。UniGLTF/VRM10 固定提交 `9750dadc592f421c356bf0376c73e61abbe323e3`；保留项目锁文件，不为了“最新版本”改依赖。Editor 集成工具不代表 Player 必需的新业务能力。

本轮没有重新导入 Unity、构建或进入 PlayMode。源码运行、内置模型文件准备与 Portable 区别见 [SOURCE_SETUP](SOURCE_SETUP.md)。

## 2026-10-21 私人冷启动安装快照

新建 Python 3.12.7 x64 venv 由原 requirements 安装通过，pip check 无冲突。主要观测版本：fastapi 0.156.8、uvicorn 0.54.0、httpx 0.28.1、jsonschema 4.26.0、fastembed 0.8.0、numpy 2.5.4、onnxruntime 1.31.0、tokenizers 0.23.3、pydantic 2.14.1。此处是那次安装快照，不是新锁文件；requirements 与 Unity manifest/lock 保持原样。验收范围见 [发布状态](RELEASE_STATUS.md)。
