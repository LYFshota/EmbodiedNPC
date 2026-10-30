# EmbodiedNPC wire contracts

> 当前说明对应 v0.1.3-preview 生产源码。独立 TaskFailure 1.0 协议及其 Schema 已退役；行动结果反馈使用现有 Cognitive envelope。已发布版本不会自动更新玩家运行中的旧实例。

当前认知 envelope 支持 `CognitiveRequest/Response 1.0` 和任务扩展 **1.1**。1.0 不携带 task_runtime；1.1 必须携带合法运行关联，回复保留原 request/trace/run/agent 与版本。Backend 严格模型定义在 `AIBackend/app/contracts.py`，Unity DTO 与 validator 位于 `UnityClient/Assets/Scripts/Cognition/`。本目录保留的 JSON schema/示例对应 Experience 的 1.0 合同；认知及任务扩展以实际 DTO、严格校验和[任务系统文档](../Docs/NPC_TASK_SYSTEM.md)为准，不能拿其他消息的 schema 验证认知 envelope。

普通 CognitiveDecision 为 DIRECT_RESPONSE、NO_ACTION、ACTION_PROPOSAL；response.decision union 另有受限系统事件的 DAILY_MODE_INTENT、AUTONOMOUS_ACTION_INTENT。内部 CONTEXT_READ、兼容 MEMORY_RECALL/TASK_READ 及任务变更步骤不是新增的通用 Unity Tool。动作意图只是提案，需 Backend 校验及独立 Unity 准入、当前世界验证和真实 Outcome，不代表动作已完成。

CognitiveRequest.context.character 当前固定七项：character_id/display_name/role_summary/backstory/personality/speaking_style/boundaries，字符上限 120/80/240/600/600/480/600，拒绝空白/超限/extra fields。Character 是低权限角色数据，不授权 Capability 或覆盖世界/感知/Memory/Relationship；只读 CharacterId 与本机 override migration 属 Unity 设置层，不是新的 wire message。

共享协议还包括 `ExperienceAppendRequest/Result 1.0`。WS `/ws` 承载 `cognitive_request`、`cognitive_cancel`、`experience_append_request`、`task_runtime_sync`、`task_control` 和 ping/pong；相应结果按原关联检查分发，不要求不同业务全局完成有序。

行动失败反馈以 `CognitiveRequest.action_feedback` 携带原 Request/Run、Proposal/revision、Trace、
真实 Outcome、已完成步骤与 Unity 给出的候选。它使用新 Request/Run，保留原 Trace；
Backend 只做一次统一认知，返回 DIRECT_RESPONSE/WAIT_FOR_PLAYER 与可空的
`recovery_alternative_id`。选择仅在原权限与有限修订预算内有效，Unity 再检查身份、场景、
当前 Outcome、候选可用性和准入；取消不能恢复。普通聊天不能使用该选择字段。
持久 Task 的可信回执继续使用原 1.1 流程，反馈不写 Task、不 Recall、不自主续话。

`task_runtime_sync` 返回 snapshot/opportunity，生命周期对账可能写入 lost-world/stale-condition 状态；不是纯只读查询，也不执行模型或重放 begin_execution。`task_control` 包含原请求恢复 query、cancel_task 和 begin_execution/not_started/settle_execution/mark_execution_unknown。Task status、attempt phase、Unity 本地握手 stage 不能混用；重连时只能恢复原关联及证据，不能据此获得新的执行权。

失败解释只生成语言，不修改任务或结算。WS 每连接最多保留 4 项（1 处理、3 等待），等待与生成设 30 秒超时；超额/异常返回已有安全 clarify 合同，不调用额外模型。Experience ACK 表示 Ledger 接受/去重，不保证 Episode 已关闭或长期 Memory 已形成。

HTTP 还包括纯常量 `/live`、受保护 `/health`、dialogue-history、Model Config 等。敏感 HTTP 与原生 WS 统一使用 S01 本机私有会话准入；Token 仅放 Authorization header，不放 URL。协议版本不是安全准入凭据，模型 API Key 也不能代替本机会话。范围及 v1/v2 配置兼容见 [Backend](../AIBackend/README.md#本地原生调用方准入)。

旧 `AgentRequest / AgentResponse`、HTTP agent-response 与 WS agent_request 均已退休，不提供旧认知协议桥接。早期 proactive schema 已从 active repo 删除，不属于当前生产 wire。当前认知只使用下述 CognitiveRequest / CognitiveDecision 合同。

## Backend-internal context reads

这组协议是 Main Provider 的结构化模型输出与 Backend 之间的内部步骤，定义在 [cognitive_step_models.py](../AIBackend/app/cognitive_step_models.py)，由 [cognitive_entry.py](../AIBackend/app/cognitive_entry.py) 执行；它不是新的 WebSocket 消息、Unity action_id 或可自由调用的 Tool。Unity 最终仍收到原 CognitiveResponse/decision 以及经验证的任务回执/执行票据。

普通对话先自动预检索世界知识并装配有权读取的常驻背景。Step 0 可以直接终结，或选择一次读取批次：

| 内部决策 | 字段与范围 |
| --- | --- |
| `CONTEXT_READ` | 可选 `knowledge_plan`、`memory_plan`，task-aware 变体还允许 `task_plan`；至少一项非空。 |
| `MEMORY_RECALL`（兼容） | `recall_plan`，沿用 MemoryRecallPlan。 |
| `TASK_READ`（兼容） | `task_query`，仅对应任务 Schema，沿用 TaskReadPlan。 |

`knowledge_plan` 只接受非空 `semantic_query`（最长 512 字符），不接受 CharacterId、权限、Top-K 或预算。`memory_plan` 使用同样长度的语义查询、可选 `subject_role`（requester/observer/shared/catalog）、受信目录 `subject_ref` 和 Memory 类型过滤；`catalog` 必须有 ref，其他角色/ref 组合也要经过身份一致性校验。`shared` 不是扩大权限，而是对原候选做当前 NPC/玩家的原始证据核验。`task_plan` 使用 [TaskReadPlan](../AIBackend/app/cognitive_task_models.py) 的受限状态、分页和不透明引用；`limit` 为 1–5，不等于世界知识 Top-K。所有模型均拒绝额外字段。

例如，task-aware Step 0 可输出（没有任务读取需要时可以省略 task_plan）：

```json
{
  "decision_kind": "CONTEXT_READ",
  "knowledge_plan": {"semantic_query": "馥芮白与拿铁的区别"},
  "memory_plan": {
    "semantic_query": "当前玩家此前实际表达的饮品偏好",
    "subject_role": "requester",
    "memory_type": "SEMANTIC_FACT"
  },
  "task_plan": {"op": "query_tasks", "statuses": ["waiting"], "limit": 3}
}
```

Backend 显式维护 Step 0/1；各读取分支独立只读并保留 OK/no-match/unavailable/预算等状态，不能用「是否有 Memory 返回值」推断当前步骤。知识复用同一目录快照，自动预检索和补查总共最多三个不同检索 ID，常驻条目单独去重并计入预算；读取期间/推理期间快照失效会安全拒绝旧证据结果。共享 Embedding 的并发门控不因分支并发等待而放宽。

Step 1 使用终态 Schema 和校验，不能返回 CONTEXT_READ、MEMORY_RECALL 或 TASK_READ。整个普通 Run 最多两次推理，不重试读链或添加第三次模型调用。固定模式、受限行动反馈恢复、续话与系统任务事件仍保留自己的准入及 Schema，不能从此合同推导出新行动、持久身份或权限。真实任务写入、ticket、begin_execution、世界 receipt 和完成结算仍遵循[任务系统](../Docs/NPC_TASK_SYSTEM.md)。

## Presentation metadata and management HTTP

现有 `emotion`、`gesture`、`look_at` 加上可选 `expression_icon` 仅用于表现。图标严格取 `none`/`happy`/`confused`/`surprised`/`thinking`；缺失或 null 兼容旧响应，none/null 在序列化时省略，非空图标只能出现在 DIRECT_RESPONSE/ACTION_PROPOSAL。Unity 还检查真实 Speech 生命周期、NPC/会话归属和冷却。图标不授权动作，不是情绪持久状态或记忆事件。

世界知识管理使用既有本机授权 [REST API](../AIBackend/app/knowledge_management_api.py)，完整体验导出使用受保护 `GET /v1/experience-export`（[实现](../AIBackend/app/experience_export.py)）。后者由当前可信玩家范围构建只读 ZIP，再由 Unity 合并本地 Persona/设置与脱敏诊断；不是额外的模型读取工具，也不上传玩家数据。详情见[架构说明](../Docs/ARCHITECTURE.md#read-only-complete-experience-export)。
