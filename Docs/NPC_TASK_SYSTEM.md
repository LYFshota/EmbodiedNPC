[简体中文](NPC_TASK_SYSTEM.md) | [English](NPC_TASK_SYSTEM.en.md)

# NPC 持久任务系统

持久任务让 NPC 能够接受部分未来委托，在下次来访或空闲时重新考虑是否执行。任务记录、条件核验与最终结算由可信代码管理，模型负责理解请求和提出意图。

## 任务范围与存储

当前 CafeDemoV2 支持 `serve_drink` 与 `clean_assigned_counter` 两类受限目标，触发条件包括 `next_visit` 和 `when_idle`。

| 数据 | 主要内容 |
| --- | --- |
| `npc_tasks` | 任务 ID、角色和玩家持久身份、目标、触发条件、状态、版本、执行占用 |
| `npc_task_commands` | 创建、修改、延期、取消等命令及其去重关联 |

`TaskService` 提供创建、查询、修改、延期、取消与结果结算能力。写入使用 SQLite 事务、版本检查与幂等命令，避免重试生成重复任务。

## 从对话保存未来约定

模型在普通认知中判断用户是否真的提出了未来委托，使用结构化 `create_task` 等操作。保存成功后，Backend 返回可信 `task_receipt`，Unity 按原始请求、角色、玩家和会话关联展示确认。

任务读取可以作为一次 `CONTEXT_READ` 的可选分支：`task_plan` 只在任务感知协议中开放，与世界知识、历史记忆共用该轮的唯一补充读取机会。

## GameVisit 与任务考虑

`GameVisit` 表示一次有效的游戏进程来访，与当前场景实例的标识分开。创建于当前 Visit 的 `next_visit` 委托，在后续 Visit 才具备触发资格。

Unity 的 `TaskRuntimeBridge` 结合玩家在场、角色空闲、冷却和当前 Run/Goal 占用等条件提供 `npc.task_consideration`。这是系统事件，不是新的玩家发言；模型可以选择继续等待或提出合法行动。

## 执行与可信结算

执行任务时，Backend 按身份、任务版本与触发条件领取唯一 attempt，再由 Unity 校验实时世界、能力和执行票据。获准目标复用原 `GoalPlanRunner`、Mini-GOAP 和动作执行器。

制作饮品或清理台面完成后，`GoalOutcome` 及对应的 `DrinkReceipt` / `CounterCleaningReceipt` 被关联验证，任务才能结算为完成。任务状态包括等待、执行、完成、取消、阻塞与待核实等情形。

## 取消与恢复

玩家可在对话或游戏内的当前委托菜单里取消允许取消的任务。执行中的取消交给现有 Runner 尝试停止；若世界效果已经成功提交，则以真实结果为准。

断线或场景异常时，任务保留执行关联和待核实状态，重连后按原命令和回执进行幂等对账。当前演示以单机稳定身份为基础，跨进程崩溃恢复可以继续扩展。

## 代码入口

[任务模型](../AIBackend/app/task_models.py) · [Service](../AIBackend/app/task_service.py) · [任务认知适配](../AIBackend/app/cognitive_tasks.py) · [Unity TaskRuntimeBridge](../UnityClient/Assets/Scripts/Cognition/TaskRuntimeBridge.cs) · [任务协议](../Contracts/README.md)
