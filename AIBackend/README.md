# AIBackend

> **当前修复范围**：S01 本机 HTTP/WS 准入、S02 Episode 可靠性、S03 失败解释异步调度及配套 Unity S05 生命周期修复已纳入源码。版本与验收范围见 [发布状态](../Docs/RELEASE_STATUS.md)；不据此宣称 Windows Player 或完整回归通过。

> **2026-10-19 当前入口**：本页保留 Backend 启动、接口和协议边界。当前产品还有受限行为模式、咖啡动作、第三方旁听、世界知识、独立双语输出合同和 Portable/Standalone 相关代码；整体状态、Candidate 与验证限制请见 [CURRENT_STATUS](../Docs/RELEASE_STATUS.md)。测试/项目阶段日期不能代替当前运行时配置。

Backend 提供 Runtime v2 认知、Experience/Episode、canonical Memory、Relationship reflection 和任务失败回复。Unity 保持世界状态与动作执行权威。

## 当前生产认知链

```text
AgentEvent → CognitiveRun → CognitiveRequest (/ws)
→ CognitiveBackendEntry → CognitiveDecision
→ automatic knowledge retrieval + optional CONTEXT_READ / MEMORY_RECALL / TASK_READ
→ at most one final Step 1 / Relationship Context
→ ActionProposalValidator → Unity admission / presentation
```

`AgentRequest → AgentResponseService` 已在 R2-B5 退休。HTTP `/v1/agent-response`、`/v1/mock/agent-response` 与 WS `agent_request` 不再受支持。仓库无法判断是否存在外部旧客户端；这是主动结束的旧外部契约，不提供桥接。

启动时若存在 `data/model_api_configs.json`，Backend 从其中构造 Mock、Ollama、DeepSeek Official Responses、OpenAI-compatible、Gemini Native 或 Anthropic Native Main Provider，以及独立的 Embedding Provider；同一个 Main 对象供 cognition、TaskFailure、canonical consolidation 与 Relationship reflection 共享。接入类型不代表每个云端模型均已真实验收。无文件时沿用 `EMBODIEDNPC_PROVIDER` 环境 bootstrap。`/health` 只检查当前 Provider，不证明后台 worker 或 Unity 连接。

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
- 原生 WS `/ws`：`cognitive_request`、`cognitive_cancel`、`task_failure_report`、`experience_append_request`、`task_runtime_sync`、`task_control`、ping/pong。版本及相关性见 [Contracts](../Contracts/README.md)。认知与失败解释使用连接所属任务及原发送锁；失败解释最多 1 项处理、3 项等待，超额/30 秒超时按原合同安全澄清，不更改任务状态。共享模型资源仍遵循原串行优先级。
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

## Persona 与验证边界

CognitiveCharacterContext 七字段为 character_id/display_name/role_summary/backstory/personality/speaking_style/boundaries，上限 120/80/240/600/600/480/600，非空白，extra=forbid、frozen=True。Unity authoring/本机 override 经唯一 Resolver 构成新 Run snapshot；Backend 只把 profile 当作现有请求 JSON 中的低权限数据，不授予 action/capability、EntityRef 或世界/历史事实 authority，不增加模型调用。

Persona 与 Memory 的 profile facts 不同；玩家偏好、共同经历必须来自真实 Dialogue/Memory evidence。运行验证已有 targeted contract/action 21/21 与 10-call DeepSeek persona/action smoke；既有 Unity Cafe E2E 和 post-Persona NOT_RUN 项见 [CURRENT_TEST_MATRIX](../Docs/RELEASE_STATUS.md)，不要把 Backend validator acceptance 写成 Unity execution success。本次文档更新未执行 Backend 或模型测试。

## World Knowledge K0 / K1 / K1.5 与 Cognitive Budget

Backend 结构化世界知识、独立 Hybrid RAG、受保护的本机管理 API、Unity 第六页编辑闭环及 Cognitive Budget P0/P1 已完成，阶段结论为 PASS WITH LIMITATIONS。16K、关闭 thinking 的云端路径已通过定向真实验收；该次历史验收配置为 revision 17/8K，不能据此推定当前用户配置；容量变更须显式保存激活并重启。小容量/未知容量安全降级及模型语义限制见 [WORLD_KNOWLEDGE](WORLD_KNOWLEDGE.md)。

Model Config v2 新建 Main（含本地、云端及自定义 API）默认 32K（32768），可手动调整；已有保存容量不自动迁移。新建默认值是请求预算，不是所有 Provider 的真实容量保证。
