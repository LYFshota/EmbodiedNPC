[简体中文](OPERATIONS.md) | [English](OPERATIONS.en.md)

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

按实际变更选择定向测试，分别说明静态检查、Mock、真实模型、Unity Editor 和 Player 的结果。不要在有价值的玩家数据上测试或迁移。首次部署已知的六项 Reimport 见 [源码运行指南](SOURCE_SETUP.md#首次-unity-导入与模型就绪)。


## 模型调用并发

在「设置 → 开发者工具 → 并发设置」中，可以调整模型调用的总量、前台和后台上限。取值范围为 1～32，前台和后台的上限均不能超过总量。

云端模型默认 `6/6/2`，Ollama 默认 `1/1/1`，依次为「总量 / 前台 / 后台」。NPC 对话与后台记忆任务共享调度器，优先响应前台请求，同时兼顾后台任务；各类上限并不代表预留固定执行槽位。

点击「保存并应用」时，待应用的 AI 配置修改也会一起保存，按页面提示重启 Backend 即可生效。
