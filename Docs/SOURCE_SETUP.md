[简体中文](SOURCE_SETUP.md) | [English](SOURCE_SETUP.en.md)

# 从源码运行（Windows / Unity Editor）

这份指南介绍如何在 Windows 上通过 Unity Editor 和 Python 后端运行项目。只想试玩，可以下载 [Windows 试玩包](../README.md)，按包内「使用说明.html」配置模型。

源码环境与版本清单见 [依赖说明](DEPENDENCIES.md)，本地多工作区和测试隔离见 [运行说明](OPERATIONS.md)。

## 环境与首次启动

1. 安装并激活 Unity **6000.5.10f1**，同时安装 Git for Windows（Unity 的 Git 包依赖需要它）。用 Unity Hub 打开 `UnityClient`，等待项目按现有 `manifest.json` 和 lock 文件恢复依赖。

   首次导入需要能访问 GitHub 和 Unity 包服务。如果遇到 `ECONNRESET`，检查网络后重试；导入问题可参考下方的资源校验说明。

2. 安装 Python **3.12 x64**。从仓库根目录运行：

   ```powershell
   cd .\AIBackend
   py -3.12 -m venv .venv
   .\.venv\Scripts\python.exe -m pip install -r requirements.txt
   .\.venv\Scripts\python.exe -m pip check
   .\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
   ```

   上述命令以单 worker 在本机启动。多工作区或测试环境请先按 [运行与数据隔离](OPERATIONS.md) 设置独立的配置、索引和会话目录。
3. 在 Editor 打开 `Assets/Scenes/TitleScene.unity` 并运行；可按开发测试需要直接运行 `CafeDemoV2.unity`，仍需通过原配置准入。当前 Build Settings 正式入口是 TitleScene → CafeDemoV2。
4. 首次为 Main 模型配置有效 Provider、模型和必要 Key，保存并应用，按提示重启 Backend。在 Editor 模式下手动重启 Backend；Windows 试玩包由启动器处理重启。

新建 Main 配置的上下文预算默认是 **32768 token**，可按模型实际容量调整。首次使用默认内置 Embedding 前，请按下文准备模型资源；也可选择外部 Embedding，或在设置中关闭向量检索（此时仍可使用关键词和结构化检索）。

### 首次 Unity 导入与模型就绪

首次打开 `UnityClient` 时，等待 Unity 完成包下载、脚本编译与 Shader 导入。项目会自动核对咖啡馆使用的六项 VRM/GLB 资源（薇薇、Ao、桜田史利矢、GirlA、CozyCafe、Xingmang），正常情况下不需要手动重新导入或配置场景。

新 `Library` 首次导入 UniVRM 时，可能出现临时的 Shader 缺失。Editor 会在依赖就绪后尝试定向修复，完成后可在 Console 或 Editor.log 中看到：

```text
PUBLIC_PREVIEW_MODELS_VALIDATED: six models, five bound Humanoid avatars; meshes/materials/shaders valid (read-only)
PUBLIC_SOURCE_MODEL_IMPORT_READY: repaired=6; no source assets saved
```

`repaired` 是实际修复数量，也可能为 0。若导入仍失败，先检查 Package Manager、Editor.log 和报错的资源路径；优先解决网络、依赖或 Shader 问题。模型校验也可以运行：

```text
-batchmode -quit -executeMethod PublicSourceModelImport.ValidateBatch
```

资源导入完成后，可通过 Unity Editor Play 验证场景和模型是否正常。常用 Backend 定向测试可从 `AIBackend` 运行 `.\.venv\Scripts\python.exe -B -m unittest -v tests.test_cognitive_request tests.test_world_action_eligibility`；涉及数据读写的自定义测试请使用独立数据目录，参见 [运行说明](OPERATIONS.md)。

## 内置 Embedding 资源

资源规范以 [builtin_embedding_resources.json](../AIBackend/app/builtin_embedding_resources.json) 为唯一文件/哈希来源：

- 上游：[`onnx-community/embeddinggemma-300m-ONNX`](https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/tree/5090578d9565bb06545b4552f76e6bc2c93e4a66)。固定 revision `5090578d9565bb06545b4552f76e6bc2c93e4a66`，本项目标识 `embeddinggemma-300m-q4`。
- 默认目标目录：`AIBackend/models/embeddinggemma-300m-q4/`。若需放在别处，启动 Backend 前设置 `EMBODIEDNPC_BUILTIN_EMBEDDING_DIR` 为该资源目录的绝对路径；不要修改规范或已保存配置来绕过资源校验。
- 按固定 revision 合法取得以下 **8 个文件**，保留 `onnx/` 子目录：`onnx/model_q4.onnx`、`onnx/model_q4.onnx_data`、`tokenizer.json`、`tokenizer_config.json`、`special_tokens_map.json`、`tokenizer.model`、`config.json`、`README.md`。文件版本、目录结构及哈希以资源规范为准，模型权重和 tokenizer 应来自同一固定 revision。
- 下载或分发模型资源时，请遵循上游模型卡及 [Gemma Terms](https://ai.google.dev/gemma/terms)。Embedding 模型使用其独立许可，不适用本项目原创代码的 Apache-2.0 许可。

准备好资源后，在 `AIBackend` 目录运行以下命令校验 8 个文件的 SHA256：

```powershell
@'
import hashlib, json, os
from pathlib import Path
spec = json.loads(Path('app/builtin_embedding_resources.json').read_text(encoding='utf-8'))
root = Path(os.environ.get('EMBODIEDNPC_BUILTIN_EMBEDDING_DIR', 'models/' + spec['model']))
for name, expected in spec['files'].items():
    with (root / name).open('rb') as source:
        actual = hashlib.file_digest(source, 'sha256').hexdigest()
    if actual != expected:
        raise SystemExit('SHA256 mismatch: ' + name)
print('PASS: all 8 resource hashes match; inference NOT_RUN')
'@ | .\.venv\Scripts\python.exe -B -
```

缺失文件或哈希不匹配会直接报错。Windows 试玩包已在 `Backend/models` 中预置所需资源。

## 本机会话与配置边界

Backend 启动时生成本机会话文件。Editor 从当前工作区的 `AIBackend/data/knowledge_admin_session.json` 读取，试玩包则由启动器管理 UserData。Unity 会自动读取会话凭据，无需手动填写 Token。使用多个工作区或非默认端口时，应确保 Backend 地址、会话文件和数据目录对应同一个实例；API Key 和会话凭据保留在本机。

`/live` 用于检查进程是否存活，`/health` 和 runtime-status 用于检查模型与服务就绪情况。已有模型配置会优先于 `EMBODIEDNPC_PROVIDER=mock` 环境变量；Mock 测试请按 [数据隔离说明](OPERATIONS.md) 准备独立目录。完整 API 见 [Backend 文档](../AIBackend/README.md)。
