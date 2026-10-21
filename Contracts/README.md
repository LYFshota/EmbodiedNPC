# EmbodiedNPC wire contracts

> 本页区分基础协议与配套本地 WIP：S01 安全准入、纯 `/live`、S03 失败解释连接任务及限额尚未随本次纯文档/注释提交发布；相关描述不构成仅检出本提交后的运行保证。

当前认知 envelope 支持 `CognitiveRequest/Response 1.0` 和任务扩展 **1.1**。1.0 不携带 task_runtime；1.1 必须携带合法运行关联，回复保留原 request/trace/run/agent 与版本。Backend 严格模型定义在 `AIBackend/app/contracts.py`，Unity DTO 与 validator 位于 `UnityClient/Assets/Scripts/Cognition/`。本目录保留的 JSON schema/示例对应 TaskFailure、Experience 的 1.0 合同；认知及任务扩展以实际 DTO、严格校验和[任务系统文档](../Docs/NPC_TASK_SYSTEM.md)为准，不能拿其他消息的 schema 验证认知 envelope。

普通 CognitiveDecision 为 DIRECT_RESPONSE、NO_ACTION、ACTION_PROPOSAL；response.decision union 另有受限系统事件的 DAILY_MODE_INTENT、AUTONOMOUS_ACTION_INTENT。内部 MEMORY_RECALL、任务读取/变更步骤不是新增的通用 Unity Tool。动作意图只是提案，需 Backend 校验及独立 Unity 准入、当前世界验证和真实 Outcome，不代表动作已完成。

CognitiveRequest.context.character 当前固定七项：character_id/display_name/role_summary/backstory/personality/speaking_style/boundaries，字符上限 120/80/240/600/600/480/600，拒绝空白/超限/extra fields。Character 是低权限角色数据，不授权 Capability 或覆盖世界/感知/Memory/Relationship；只读 CharacterId 与本机 override migration 属 Unity 设置层，不是新的 wire message。

共享协议还包括 `TaskFailureReport/Response 1.0`、`ExperienceAppendRequest/Result 1.0`。WS `/ws` 承载 `cognitive_request`、`cognitive_cancel`、`task_failure_report`、`experience_append_request`、`task_runtime_sync`、`task_control` 和 ping/pong；相应结果按原关联检查分发，不要求不同业务全局完成有序。

`task_runtime_sync` 返回 snapshot/opportunity，生命周期对账可能写入 lost-world/stale-condition 状态；不是纯只读查询，也不执行模型或重放 begin_execution。`task_control` 包含原请求恢复 query、cancel_task 和 begin_execution/not_started/settle_execution/mark_execution_unknown。Task status、attempt phase、Unity 本地握手 stage 不能混用；重连时只能恢复原关联及证据，不能据此获得新的执行权。

失败解释只生成语言，不修改任务或结算。WS 每连接最多保留 4 项（1 处理、3 等待），等待与生成设 30 秒超时；超额/异常返回已有安全 clarify 合同，不调用额外模型。Experience ACK 表示 Ledger 接受/去重，不保证 Episode 已关闭或长期 Memory 已形成。

HTTP 还包括纯常量 `/live`、受保护 `/health`、dialogue-history、task-failure-response、Model Config 等。敏感 HTTP 与原生 WS 统一使用 S01 本机私有会话准入；Token 仅放 Authorization header，不放 URL。协议版本不是安全准入凭据，模型 API Key 也不能代替本机会话。范围及 v1/v2 配置兼容见 [Backend](../AIBackend/README.md#本地原生调用方准入)。

旧 `AgentRequest / AgentResponse`、HTTP agent-response 与 WS agent_request 均已退休，不提供旧认知协议桥接。早期 proactive schema 已从 active repo 删除，不属于当前生产 wire。当前认知只使用下述 CognitiveRequest / CognitiveDecision 合同。
