# 本地开发与数据隔离

默认在本机运行单 worker Backend：从 `AIBackend` 按 [源码运行说明](SOURCE_SETUP.md) 启动，监听 `127.0.0.1:8000`。默认运行数据位于该 checkout 的 `AIBackend/data`，不随源码提供。不要混用其他 checkout 的数据库、模型配置或会话凭据。

## 模型配置与会话

从 TitleScene 的 AI 配置选择 Main/Embedding，保存并应用后重启自己的 Backend，再重新检查就绪状态。`model_api_configs.json` 可能明文包含 API Key，不要上传或粘贴其完整内容。关闭 Embedding 时仍有已有的 lexical/structured fallback。

Backend 自动生成本机会话，配套 Editor 从同一 checkout 读取，无需手输 Token。`/live` 只说明进程存活；敏感 HTTP 与原生 WS 需要原有本地准入。不要把令牌放进 URL、日志或 Issue，不要放宽 Host、Origin、peer、session 或业务校验来绕过配置。

## 测试或多个 checkout 的隔离

须在导入应用或启动服务前设置全部路径；只换端口或设置 Mock 不足以隔离已有配置。以下沿用现有隔离入口，不自动连接个人模型：

```powershell
cd .\AIBackend
$testRunDir = Join-Path ([IO.Path]::GetTempPath()) ('EmbodiedNPC-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRunDir | Out-Null
$env:EMBODIEDNPC_MEMORY_DB = Join-Path $testRunDir 'memory.sqlite3'
$env:EMBODIEDNPC_SETTINGS_FILE = Join-Path $testRunDir 'runtime_settings.json'
$env:EMBODIEDNPC_MODEL_CONFIG_FILE = Join-Path $testRunDir 'model_api_configs.json'
$env:EMBODIEDNPC_KNOWLEDGE_FILE = Join-Path $testRunDir 'world_knowledge.json'
$env:EMBODIEDNPC_KNOWLEDGE_INDEX_DB = Join-Path $testRunDir 'world_knowledge_index.sqlite3'
$env:EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE = Join-Path $testRunDir 'knowledge_admin_session.json'
$env:EMBODIEDNPC_CONTEXT_POLICY_FILE = Join-Path $testRunDir 'context_policy.json'
$env:EMBODIEDNPC_KNOWLEDGE_ADMIN_URL = 'http://127.0.0.1:8001'
$env:CANONICAL_EMBEDDING_ENABLED = 'false'
$env:EMBODIEDNPC_PROVIDER = 'mock'
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8001 --no-proxy-headers
```

非默认端口运行 Unity 时，还需按实际客户端配置同步 Backend URL 及会话文件/URL；上例只用于隔离 Backend，不能宣称 Unity 已自动切到 8001。配置、存档和会话路径选取以 [Backend 文档](../AIBackend/README.md) 为准。测试前停止自己的服务，不结束其他工程或未知进程。

## 贡献时的验证

按实际变更选择定向测试，分别说明静态检查、Mock、真实模型、Unity Editor 和 Player 的结果。不要在有价值的玩家数据上测试或迁移。首次部署已知的六项 Reimport 见 [SOURCE_SETUP](SOURCE_SETUP.md#首次-unity-导入后的-vrm--glb-核对)。


## Model concurrency (v0.1.4-preview)

Settings → Developer tools → Concurrency settings exposes total, foreground and background model limits (integers 1–32, each class no greater than total). Cloud defaults: 6/6/2; Ollama: 1/1/1. Save & Apply includes any pending AI configuration edits and requires the existing Backend restart flow. The scheduler is shared by cognition and all three background workers; class caps do not reserve fixed slots. Existing timeouts, bounded waiting and local priority remain in effect.
