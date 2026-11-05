# AIBackend

Python 后端负责 NPC 的认知决策、世界知识检索、长期记忆、关系反思和持久任务。Unity 负责世界状态与动作执行，双方通过 HTTP / WebSocket 协作。

## 认知运行流程

```text
AgentEvent → CognitiveRun → CognitiveRequest (/ws)
→ 上下文装配、授权知识预检索
→ Step 0（直接决策 / 请求 CONTEXT_READ）
→ 按需读取知识、记忆或任务 → 可选 Step 1 最终决策
→ 结构化结果 → Backend 校验 → Unity 呈现或行动准入
```

单次普通认知最多两次模型推理。不同 NPC 的前台请求和后台记忆任务使用共享的有界并发调度器。

Backend 使用 `data/model_api_configs.json` 中的 Main / Embedding 配置；未创建配置文件时可从环境变量初始化。支持 Mock、Ollama、DeepSeek Official、OpenAI-compatible、Gemini Native 和 Anthropic Native 等适配器。具体协议见 [Contracts](../Contracts/README.md)，版本验收记录见 [发布状态](../Docs/RELEASE_STATUS.md)。

## 启动

首次源码运行请先完成[环境与内置 Embedding 资源准备](../Docs/SOURCE_SETUP.md)，实际依赖快照见[DEPENDENCIES](../Docs/DEPENDENCIES.md)。本页的启动命令不会自动取得固定模型资源。

```powershell
cd .\AIBackend
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
$env:EMBODIEDNPC_PROVIDER = 'mock'
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
```

真实模型可用 `start_qwen_backend.ps1`；无 Model Config 文件时，Provider 设置保留 `OLLAMA_BASE_URL`、`OLLAMA_MODEL`、`OLLAMA_NUM_CTX`、`LLM_TIMEOUT_SECONDS`。

无 Model Config 文件时，DeepSeek Official 使用 `EMBODIEDNPC_PROVIDER=deepseek_official_responses`，从环境读取 `DEEPSEEK_API_KEY`，可用 `DEEPSEEK_BASE_URL`（默认 `https://api.deepseek.com`）、`DEEPSEEK_MODEL`（默认 `deepseek-flash`）、`DEEPSEEK_TIMEOUT_SECONDS`（默认 60）覆盖；密钥不得写入旧 Settings JSON。有 Model Config 文件时使用该 JSON 直接保存的 API Key。Provider 使用 `POST /responses` 推理、`GET /models` 健康检查，HTTP `trust_env=False`。官方协议依据：[Responses API](https://api-docs.deepseek.com/api/create-response/)、[Models](https://api-docs.deepseek.com/api/list-models/)。

## 本地原生调用方准入

Backend 启动时复用 Knowledge 的 `data/knowledge_admin_session.json`（schema 1）发布私有进程能力，敏感 HTTP 和原生 `/ws` 共用。发布前取得同目录 `knowledge_admin_session.lock` 的 OS 锁并私有原子写入；失败拒绝启动，同路径第二个实例不能覆盖活实例。仅支持本机单 worker，不支持反向代理、浏览器敏感操作或 WebGL。任何 Origin、Forwarded / X-Forwarded-*、非 loopback peer、与声明不符的 Host/端口均拒绝。

模型 v1/v2 的读取、保存、应用、重置、清 Key、探测，以及 `/health`、runtime-status、知识管理、历史、索引、设置/策略写入、context recheck 和任务失败生成均要求 `Authorization: Bearer`。拒绝在业务正文解析前发生；WS 在 accept 前拒绝。v1 的已授权 schema 1 读取仍含旧 Key，schema 2 仍返回 409；v2 仍只返回 key_present。敏感响应使用 no-store。

`GET /live` 仅返回进程存活常量，不读配置、不调用 Provider，不能证明 AI Ready。`/health` 和 runtime-status 保留 Provider 就绪语义，某些 `generation_once` 配置首次探测会生成一次，因此需要授权。脱敏 settings/context-policy GET 与静态 API 文档不需要 Token，但仍受网络边界保护。

Unity Editor 默认从相对 `Application.dataPath` 的 `../../AIBackend/data/knowledge_admin_session.json` 读取；Portable 使用启动器传入的 UserData 会话路径。普通玩家仍双击包根 `Start.cmd`，无需输入 Token。每次敏感 HTTP、每轮 WS 连接尝试重新读取文件，Backend 重启会旋转凭据，旧凭据失效；保留既有有限重试/手动 Retry，不自动重放保存、付费探测或任务开始。

非默认端口/多 worktree 必须在启动前将 `EMBODIEDNPC_KNOWLEDGE_ADMIN_FILE` 和 `EMBODIEDNPC_KNOWLEDGE_ADMIN_URL`（如 `http://127.0.0.1:8001`）与客户端目标对齐，并使用独立数据/会话路径。这些环境项只传路径和 URL；不要传 Token 值。调试工具应在内存中读取文件并向严格匹配的本机 endpoint 加头，禁止重定向；不要复制到命令行、URL、日志或仓库配置，也不能用模型 API Key 代替。

这不隔离能以同一用户权限读取文件或内存的恶意进程，不证明调用方是 Unity.exe。会话也不替代世界动作准入、TaskService、GameVisit 或业务幂等。Backend 与 Unity 源码必须配套更新，旧匿名 Player/脚本会被明确拒绝。

## 接口与数据

- HTTP：`GET /live`、受保护 `/health`、runtime-status、Model Config v2 与兼容 v1、settings、dialogue-history、task-failure-response，以及知识/索引/上下文策略接口；完整端点以路由定义为准。
- 原生 WS `/ws`：`cognitive_request`、`cognitive_cancel`、`task_failure_report`、`experience_append_request`、`task_runtime_sync`、`task_control`、ping/pong。版本及相关性见 [Contracts](../Contracts/README.md)。认知与失败解释使用连接所属任务及原发送锁；失败解释最多 1 项处理、3 项等待，超额/30 秒超时按原合同安全澄清，不更改任务状态。共享模型执行器按前台优先和后台公平策略调度。
- 新长期记忆只经 Experience → Episode → 当前 `memory-consolidator/3.0` → canonical Memory 产生；`memory-consolidator/2.0` 仅作为历史 job 版本常量保留。Migration 15 已退休旧 episodic、memory_v1、数值 Relationship 和 shadow 物理表；旧 job 行仍可留在共享 `memory_jobs` 中，但不会被 v2 worker 领取。

定向测试使用 `.venv\Scripts\python.exe -B -m unittest -q tests.test_cognitive_request tests.test_cognitive_websocket`，并按变更范围加入 canonical Memory、Relationship、Action、TaskFailure 和 Experience 测试。测试使用隔离 `EMBODIEDNPC_MEMORY_DB`。

## 兼容 SavedSettings API

`GET /v1/settings` 返回 SavedSettings 的公开 DTO、revision、`source=defaults|persisted` 和 `updated_at`（defaults 时为 `null`）。完整 `PUT /v1/settings` 请求为 `{"expected_revision": 0, "settings": {"selected_preset_id": "mock", "provider_presets": [...]}}`；通常从 GET 取得 `settings` 后修改并提交。成功 revision +1；过期 revision 返回 HTTP 409 `SETTINGS_REVISION_CONFLICT`。无效输入返回 422，损坏文件或存储故障返回安全的 5xx `error.code`。API 不返回凭据引用名、密钥或本机路径；已存在的 `credential_source=environment` 可在往返 PUT 中保留，但 P2 API 不能新增该绑定。

`GET /v1/runtime-status` 保留旧 SavedSettings selection、当前 Provider/model/health，以及 `provider_settings_activation=not_supported`；该字段只描述旧 P1/P2 ProviderPreset，重启也不会应用它。新 Model Config 的启动及重启状态见下文。`/health` 保持原就绪语义且需 S01 授权。可选 `episode_worker` 诊断区分 STOPPED/RUNNING/RETRYING/FAULTED，不将进程存活等同于后台记忆处理正常；`/live` 仍只返回常量。

## Model / API Config

当前 Unity 配置入口使用 **v2**：`GET /v2/model-config`、`POST /v2/model-config/profile`、`activate`、`save-and-activate`、`reset-main`、`probe`（后五项均相对 `/v2/model-config/`）。Main/Embedding 各有 profile；保存草稿与激活配置分开，`save-and-activate` 原子完成两者。写入携带 `expected_revision`，冲突 409；Key 按 retain/replace/clear 明确处理，公开 DTO 仅显示 `key_present`。Probe 的发现、连接及合同检查按原确认规则执行，可能调用 Provider，不能作为纯存活检查。

首次读取 v1 仅投影为 v2，不偷偷改写；成功的显式 v2 写入才迁移。当前激活快照与下一次启动配置仍有区别。以下 **v1 为兼容接口**：已授权 schema 1 文档可往返旧 Key，已有 schema 2 文档的 v1 读取/保存返回 409 `MODEL_CONFIG_V2_REQUIRED`，新客户端不应回退以绕过 v2。

`GET /v1/model-config` 返回 `configured`、`revision`、`main_model`、`embedding`；缺失时为 `configured=false`、revision 0。`PUT /v1/model-config` 接受 `{"expected_revision": 0, "config": {"main_model": {...}, "embedding": {...}}}`，保存两个 Slot 并递增 revision；冲突返回 409。默认文件 `data/model_api_configs.json` 被 Git 忽略，**直接包含 API Key**，已授权的旧 schema 1 GET 也直接返回 API Key 供兼容本机编辑。不要把配置文件、请求或响应上传到仓库或日志。

`POST /v1/model-config/discover-models` 用未保存的 `provider_type/base_url/api_key` 获取 OpenAI-compatible、Ollama 或 DeepSeek Official 的模型 ID。`POST /v1/model-config/test` 用 `kind=main|embedding` 和未保存的 `main_model`（Embedding 另带 `embedding`）执行最小连接测试。两者只返回 `success`、`models`（发现接口）与稳定 `error_code`；发现失败后仍可手动填 Model。Embedding 可复用 Main 的 Base URL 和 API Key，但模型独立。

OpenAI-compatible Main 使用 Chat Completions 与 `response_format=json_schema`，Cloud Embedding 使用 `/embeddings`；通用 Main adapter 不发送厂商专属 `thinking` 字段。旧 P1/P2 Settings API 保持兼容。

## Restart Apply

Backend 启动时只读一次 `data/model_api_configs.json`。存在时，它优先于 Provider/Embedding 环境变量；缺失时保留原环境 bootstrap。Main Provider 在四类生成任务之间共享；Cognition Step 0/1 使用启动快照的 temperature、thinking、max output tokens，后台任务保留各自的 inference options。Embedding 可独立于 Main 类型启用，包括 Main 为 Mock 的情况；disabled 时仍可使用 lexical/structured fallback。

v1 保存以及 v2 profile 保存/激活均不热切换运行中的 Provider。`GET /v1/runtime-status` 新增 `active_main_model`、`active_embedding`、`model_config` 和 `restart_required`，不返回 API Key。有效配置改变后重启 Backend，新的 Main/Embedding 才生效，`restart_required` 恢复 false；display name 或 revision 单独变化不触发重启。不提供 HTTP 重启接口。Unity Editor 的本机开发重启使用 `restart_local_backend.ps1` 并核对目标进程归属；Portable 则由包根 Start 的受控退出/重启流程处理，二者不混用。

## 单玩家身份与对话续话（历史 v2 Lite 增量）

Backend 默认固定 Player UID 为 `cafe-local-player-001`；隔离部署可在 app import 前设置 `EMBODIEDNPC_DEMO_PLAYER_UID`，不能从客户端显示名/LLM 文本选择身份。普通及合法已展示的自主 NPC 自述按同一本人来源规则准入，Recall 保留自述限定；不认证共同历史或实际动作。真实 ActionOutcome 继续要求合法执行证据。Step1 复用同一匿名 subject_catalog 并使用合法历史偏好。

可选 `dialogue_turn_policy` 只允许 WAIT_FOR_PLAYER/MAY_CONTINUE，缺省保持一问一答，不改变 episode_directive。`npc.dialogue_continue` 仅一次可抢占后台推理，DIRECT_RESPONSE/NO_ACTION、无额外 Recall；ACK、父轮次与 Recent Dialogue 顺序校验保持。Unity 默认禁用、等待4秒、每玩家轮最多一次。

能力、当前验证与 NOT_RUN 见[公开摘要](../Docs/RELEASE_STATUS.md)。

## Persona

角色人设包含 character_id、display_name、role_summary、backstory、personality、speaking_style 和 boundaries。Unity 的默认配置与玩家修改由 CharacterProfileResolver 合成新的认知快照。人设负责角色表达与个性，玩家经历和真实行动结果则由各自的证据与世界系统管理。

字段格式和上限见 [Contracts](../Contracts/README.md)，定向验收记录见 [发布状态](../Docs/RELEASE_STATUS.md)。

## 世界知识与上下文预算

世界知识由 Backend 管理，支持角色权限、常驻内容、混合检索与 Unity 内编辑。普通认知先自动预取相关背景，必要时再通过一次联合读取补查知识、记忆或任务。

新建 Main 配置默认使用 32K 上下文预算，也可按模型实际容量调整。现有保存的配置保持不变；输入内容、输出预留和安全余量由统一预算分配器协调。

实现与配置说明见 [世界知识](WORLD_KNOWLEDGE.md) 和 [上下文容量反馈](../Docs/CONTEXT_CAPACITY_FEEDBACK.md)。
