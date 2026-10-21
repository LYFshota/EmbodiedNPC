# 从源码运行（Windows / Unity Editor）

本页对应当前源码，不包含已编译 Player、Python Portable Runtime、模型权重或玩家存档。便携包请使用包根 `Start.cmd`，不要按源码命令手工启动其 Backend。已验证环境的版本与证据边界见 [DEPENDENCIES](DEPENDENCIES.md)；本轮文档核对未做净机器安装或模型推理。

本页所述 S01 会话和纯 `/live` 已随配套修复纳入源码。本候选的许可处理与定向 Unity 验证见 [候选说明](OPEN_SOURCE_CANDIDATE.md)；源码已纳入不等于重新构建或验收了历史 Standalone。

## 环境与首次启动

1. 安装并激活 `UnityClient/ProjectSettings/ProjectVersion.txt` 指定的 Unity **6000.5.10f1**，安装 Git for Windows 并确保 `git --version` 可执行；UPM 的 Git 依赖需要它。Windows x64 构建支持只在构建对应 Standalone 时需要。用 Unity Hub 打开 `UnityClient`，按现有 manifest/lock 恢复包，不升级包、不重跑场景一次性生成菜单。
   首次恢复依赖需要能访问 GitHub、`packages.unity.com` 和 `download.packages.unity.com`。若出现 `ECONNRESET`，先检查网络并重试自己的工程实例，保留错误日志；不要通过复制旧 Library、升级依赖或操作其他 Editor 掩盖恢复失败。

2. 安装 Python **3.12 x64**。从仓库根目录运行：

   ```powershell
   cd .\AIBackend
   py -3.12 -m venv .venv
   .\.venv\Scripts\python.exe -m pip install -r requirements.txt
   .\.venv\Scripts\python.exe -m pip check
   .\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
   ```

   不使用多 worker、反向代理或公开监听。导入应用会初始化数据路径；若要隔离测试，必须在启动 Python/导入应用**之前**配置所有数据、模型配置、知识索引及私有会话路径，详见 [OPERATIONS](OPERATIONS.md)。不要在正式存档上运行测试。
3. 在 Editor 打开 `Assets/Scenes/TitleScene.unity` 并运行；可按开发测试需要直接运行 `CafeDemoV2.unity`，仍需通过原配置准入。当前 Build Settings 正式入口是 TitleScene → CafeDemoV2，旧 CafeDemo 仅作历史回归。
4. 首次为 Main 模型配置有效 Provider、模型和必要 Key，保存并应用，按提示重启 Backend。源码 Editor 模式通过本机开发重启脚本/人工停止再启动 Backend 生效；Portable 的“重启并应用”由 `Start.cmd` 的现有监督进程处理，不能混用两个入口。

新建 Main 草稿默认 32768 上下文，已有值不自动迁移。该值是请求预算配置，不证明 Provider 实际容量。首次 Main 保存会一并启用默认预选的内置 Embedding；**需先准备下面的资源**，或在保存前明确选择已有外部 Embedding / 关闭 Embedding 的路径。关闭向量能力仍有 lexical/structured fallback，不等于启用向量检索。

### 首次 Unity 导入后的 VRM / GLB 核对

新建 Library 的首次导入可能先处理 VRM/GLB、后完成包内 Shader，导致 CafeDemoV2 报 Missing Prefab 或材质导入异常。先等 Package Manager、Shader 和脚本导入结束，再在 Project 中对下面六项已跟踪资源执行右键 **Reimport**；不需要复制其他工作区的 Library 或资源，也不要更改 UniVRM 的固定版本。

```text
Assets/CafeDemo/V2/Characters/薇薇.vrm
Assets/CafeDemo/V2/Characters/Ao.vrm
Assets/CafeDemo/V2/Characters/桜田 史利矢.vrm
Assets/CafeEnvironmentCompatibilityTest/GirlA.vrm
Assets/CafeEnvironmentCompatibilityTest/CozyCafe.glb
Assets/VRMCompatibilityTest/Xingmang.vrm
```

冷导入日志中的 `ArgumentNullException: Shader` / `Asset import failed` 应按导入失败处理，即使批处理最终退出码为 0。六项 Reimport 后重新打开正式场景，确认实际模型、材质和贴图完整；首帧 Shader 尚在编译时应等待稳定画面后再判断显示结果。此操作重新导入工程已有素材，不需要额外下载或手动添加美术包。

这些历史目录名下的资源也被正式 CafeDemoV2 场景引用。仅重导入资源，不执行 `ReimportAndApplyBatch` 或场景配置菜单，不重写场景/Prefab。打开 CafeDemoV2 后确认无 Missing Prefab/资源导入错误，再执行场景测试。此步骤已有定向重导入验证，不等于已完成全新机器、全新 Library 或 Player 的完整验收。

Backend 的相关离线测试可从 `AIBackend` 目录通过标准入口运行，例如 `.\.venv\Scripts\python.exe -B -m unittest -v tests.test_cognitive_request tests.test_world_action_eligibility`。测试公共入口会在首次导入应用前创建临时 Mock 数据路径，并为 HTTP/WS 提供真实本地会话；不会加载本机模型配置或关闭 S01。其他未采用此夹具的集成测试仍须遵守上面的导入前隔离要求。

## 内置 Embedding 资源

资源规范以 [builtin_embedding_resources.json](../AIBackend/app/builtin_embedding_resources.json) 为唯一文件/哈希来源：

- 上游：[`onnx-community/embeddinggemma-300m-ONNX`](https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/tree/5090578d9565bb06545b4552f76e6bc2c93e4a66)。固定 revision `5090578d9565bb06545b4552f76e6bc2c93e4a66`，本项目标识 `embeddinggemma-300m-q4`。
- 默认目标目录：`AIBackend/models/embeddinggemma-300m-q4/`。若需放在别处，启动 Backend 前设置 `EMBODIEDNPC_BUILTIN_EMBEDDING_DIR` 为该资源目录的绝对路径；不要修改规范或已保存配置来绕过资源校验。
- 按固定 revision 合法取得以下 **8 个文件**，保留 `onnx/` 子目录：`onnx/model_q4.onnx`、`onnx/model_q4.onnx_data`、`tokenizer.json`、`tokenizer_config.json`、`special_tokens_map.json`、`tokenizer.model`、`config.json`、`README.md`。不要下载另一量化版本、混用 tokenizer、保存网页/LFS 指针代替权重，或编辑受哈希约束的模型卡。
- 取得资源前阅读该 revision 的模型卡、基础模型及转换资源的适用许可；如平台要求接受条款，按其流程取得。Google 的 [Gemma Terms](https://ai.google.dev/gemma/terms) 包含使用限制及分发义务；模型不随第一方源码改为 Apache-2.0。发布带权重的包前另行核对并附所需条款、Notice 和上游声明。当前规范的 8 文件清单只用于运行身份校验，不是完整的授权文件清单。

以下命令从 `AIBackend` 目录执行，只校验文件，不导入应用、不加载模型、不访问玩家数据：

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

`builtin_resources_present` 仅表示文件存在，不能代替哈希或真实加载验证。缺失/哈希不匹配会明确失败，不自动联网补下载。Portable 则由组包流程在 `Backend/models` 预置并绑定资源，不复制开发机数据或会话凭据。

## 本机会话与配置边界

Backend 启动创建私有会话文件；Editor 默认从同一 checkout 的 `AIBackend/data/knowledge_admin_session.json` 读取，Portable 使用启动器传入的 UserData 路径。敏感 HTTP 与原生 WS 共用该凭据，每次连接重新读取，重启轮换；无需手输 Token。非默认端口或多个 checkout 必须让客户端 URL、会话 URL/文件和各数据根一致。不要将 Token 放入 URL、普通日志或手工提交的配置。

`/live` 只证明进程存活，不访问 Provider；受保护 `/health`、runtime-status 才进行就绪探测，特定配置可能生成一次。Mock 环境变量不能覆盖已有 Model Config，因此不要把在个人 checkout 设置 `EMBODIEDNPC_PROVIDER=mock` 当成测试隔离。完整 API 与旧 v1 边界见 [Backend](../AIBackend/README.md)。
