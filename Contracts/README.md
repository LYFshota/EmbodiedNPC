[简体中文](README.md) | [English](README.en.md)

# EmbodiedNPC 通信协议

Unity 与 Python Backend 通过本机 HTTP 和 WebSocket 交换认知请求、经历与任务信息。协议强调**请求关联、结构化决策和真实世界回执**。字段的最终定义位于 [Backend contracts.py](../AIBackend/app/contracts.py) 及 Unity `Cognition/` DTO。

## 认知消息

认知请求/响应支持版本 **1.0**，持久任务扩展使用 **1.1**。每次响应关联原 `request_id`、`trace_id`、`run_id`、NPC 身份与协议版本。

`CognitiveRequest` 包含本轮事件、角色、局部感知、相关玩家、行为模式、可用能力与当前执行状态。角色人设包含 `character_id`、`display_name`、`role_summary`、`backstory`、`personality`、`speaking_style`、`boundaries`。

普通 `CognitiveDecision` 的三种最终结果：

| 类型 | 含义 |
| --- | --- |
| `DIRECT_RESPONSE` | 向玩家回复 |
| `NO_ACTION` | 本轮不采取行动 |
| `ACTION_PROPOSAL` | 提出带动作 ID 与参数绑定的行动请求 |

系统事件还有经过事件类型与资格校验的 `DAILY_MODE_INTENT`、`AUTONOMOUS_ACTION_INTENT` 等受限分支。行动提案经 Backend 验证后由 Unity 独立准入，实际执行结果通过 `GoalOutcome` 和世界回执确认。

## WebSocket 与 HTTP

WebSocket `/ws` 传输 `cognitive_request`、`cognitive_cancel`、`experience_append_request`、`task_runtime_sync`、`task_control` 及响应和心跳消息。

HTTP 包括 `/live`、`/health`、模型配置、运行状态、世界知识管理、上下文策略和玩家体验导出等接口。本机受保护接口使用 Backend 会话授权；模型 API Key 则用于对接外部模型服务。

`ExperienceAppendRequest/Result 1.0` 表示经历写入账本的确认。Episode 分段及长期记忆整理在后台独立进行。

## Backend-internal context reads

普通对话先自动预取有权限访问的世界知识，再进入 Step 0。模型可以直接给出最终结果，也可以请求一次内部上下文读取：

| 内部决策 | 作用 |
| --- | --- |
| `CONTEXT_READ` | 按需组合 `knowledge_plan`、`memory_plan` 和任务协议允许的 `task_plan` |
| `MEMORY_RECALL` | 兼容的单一记忆读取形式 |
| `TASK_READ` | 兼容的受限任务读取形式 |

这组步骤在 Backend 内执行，不是向 Unity 发送的任意工具调用。至少一个读取计划需要非空；知识和记忆通过语义查询指定目标，主体权限与持久身份由 Backend 解析。

示例（任务协议下的 Step 0 读取计划）：

```json
{
  "decision_kind": "CONTEXT_READ",
  "knowledge_plan": {"semantic_query": "馥芮白与拿铁有什么区别"},
  "memory_plan": {
    "semantic_query": "当前玩家此前明确表达的饮品偏好",
    "subject_role": "requester",
    "memory_type": "SEMANTIC_FACT"
  },
  "task_plan": {"op": "query_tasks", "statuses": ["waiting"], "limit": 3}
}
```

Backend 为各读取来源返回状态和有限证据，统一分配最终上下文预算。Step 1 接收结果后必须输出最终决策。普通认知运行最多包含两次模型推理。

## 任务与结果关联

任务系统使用版本 1.1 的运行上下文、保存回执、考虑机会和执行票据。执行请求按玩家、NPC、Visit、任务版本与 attempt 关联；取消、延期与结算继续调用 TaskService。

行动失败反馈保留原 Trace 与行动结果关联，可在受限条件下请求一次新的语义判断或恢复选择；Unity 仍核验当前世界和原执行归属。详见 [持久任务](../Docs/NPC_TASK_SYSTEM.md)。

## Presentation metadata and management HTTP

普通回复可附带 `emotion`、`gesture`、`look_at` 和 `expression_icon`，用于 Unity 的对话与表情表现。它们不改变动作或记忆的真实状态。

知识管理使用 [管理 API](../AIBackend/app/knowledge_management_api.py)。体验导出使用受保护的 `GET /v1/experience-export`，读取现有持久记录并由 Unity 合并本地设置数据。

## 相关文档

[整体架构](../Docs/ARCHITECTURE.md) · [Backend](../AIBackend/README.md) · [记忆](../Docs/MEMORY.md) · [持久任务](../Docs/NPC_TASK_SYSTEM.md)
