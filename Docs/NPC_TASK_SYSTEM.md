# NPC 自主任务系统

当前实现面向 CafeDemoV2 单机、四位 NPC 的 Demo。模型选择是否接受约定、创建或修改待办、查询、延期、取消、执行或沉默；可信代码管理身份、权限、事务、占用和结果。存在 waiting 任务只提供考虑机会，不强迫执行。任务模块不使用通用 Function Calling 循环，也不增加任务专用模型服务。

> 当前行为按 v0.1.3-preview 生产源码描述；后文带日期/阶段的验收记录只代表对应历史范围。

## 存储与服务

Schema 19 在旧 Memory、Experience、Episode 和用户表之上增加两张表，旧迁移不重写。

| 表 | 主要内容 |
| --- | --- |
| `npc_tasks` | `task_id`、稳定 owner/player 三元组键、来源、严格目标、触发类型、创建 Visit、状态、版本、去重键、调度/条件锚点、考虑记录、当前执行槽 |
| `npc_task_commands` | 全局命令 ID、owner/task 关联、操作、载荷哈希、操作唯一键、来源、不可变结果与时间 |

玩家委托为 `player_request`，NPC 自主计划为 `self_initiated`。前者绑定合法玩家，后者没有玩家所有者；长期身份不使用显示名或临时 EntityId。目标限定为 `serve_drink` 和 `clean_assigned_counter`，触发限定为 `next_visit` 和 `when_idle`。

`TaskService` 提供 `create_task`、`get_task`、`query_tasks`、`update_task`、`defer_task`、`cancel_task`、`get_command`，以及可信宿主使用的考虑、领取、开始、结算和未知标记接口。严格 DTO 拒绝额外字段、状态伪造和任意 SQL。

写操作在短 SQLite 事务内完成，使用版本 CAS；命令载荷哈希相同则返回原结果，不同则冲突。活跃目标去重索引和执行操作唯一键防止重复任务/效果。事务失败不返回成功回执，任务和命令一起回滚。重开保留任务、版本、执行关联和命令历史。

## 认知与承诺

复用 `CognitiveBackendEntry`、`ModelRequest` 和原 Provider。模型内部意图为：

- `create_task`：受限目标、`source=requester/self`、触发类型。
- `TASK_READ` 内的 `query_tasks`：受限状态、分页或不透明 `task_ref`。
- `update_task`：提供已披露的 `task_ref` 和精确版本，选择 edit/defer/cancel。

模型不能选择持久身份、task_id、完成状态、执行结果或事务结果。愿望、假设、引用和讨论不是自动成立的承诺，NPC 可以拒绝。明确未来承担使用 `future_commitment=true` 和结构化操作；服务提交后才向 Unity 返回可信外层 `task_receipt`，Unity 验证原请求、Run、NPC、玩家、Visit、scene 和版本后展示回复。保存失败不释放原成功承诺；保存成功即成立，不另建展示 ACK 状态机。

普通对话 Step 0 先接收常驻背景及世界知识自动预检索结果，可直接终结，或使用 Backend 内部 `CONTEXT_READ` 一次组合读取：`knowledge_plan` 补查世界知识、`memory_plan` 召回真实历史、`task_plan` 查询持久任务。`task_plan` 只在可信任务上下文准入的 task-aware Schema 中提供，字段沿用 `TaskReadPlan`（受限状态、分页或已披露 task_ref），不由模型选择持久身份。至少一个读取分支非空；各分支保留独立状态，再交给显式 Step 1 终态推理。某路不可用不代表事实不存在，也不抹掉其他成功结果。

旧 `TASK_READ(task_query)` 和 `MEMORY_RECALL(recall_plan)` 继续兼容，与 `CONTEXT_READ` 共用一次内部读取批次，不能叠加为第三次推理。Step 1 不接受任何再次读取。自动知识预检索与补查共用三个检索 ID 的额度，任务列表受自己的受限分页和共同上下文预算约束；这些不是新增 Unity 行动工具。静默系统事件、任务考虑和续话仍用各自受限 Schema，不因普通对话扩展获得全部读取能力或玩家委托写权限。可信吧台脏污入口可提出自己的清理待办，不能替其他玩家创建委托。见[内部读取协议](../Contracts/README.md#backend-internal-context-reads)。

任务摘要进入现有上下文预算；身份、当前输入和关键能力不会被任务列表挤掉。任务 Schema 去除冗余自动标题/默认值注解并精简重复说明，验证约束不变。目标语言冻结于 Run，TaskRead Step 1 不重新判断语言，ID/枚举不翻译，中文/英文长度合同不变。

## 协议与 GameVisit

Backend 接受旧认知协议 `1.0` 与任务协议 `1.1`。旧客户端没有任务能力时不得假装保存承诺；新协议仍拒绝未知字段。新增内容为任务运行上下文、可信保存回执、任务考虑上下文、执行 ticket 和结果，Unity 普通终态 decision_kind 不增加。

Cafe 启动准入通过、Registry 和玩家持久身份有效后启用 TaskRuntimeBridge。一个 Unity 游戏进程只拥有一个 GameVisit；同进程菜单往返、场景重载和 Backend 重连不改变 Visit，scene_instance_id 随世界实例独立变化。新游戏进程成功进入 Cafe 才产生新 Visit。当前 Visit 创建的 next_visit 任务不能在该 Visit 触发。

`task_runtime_sync` 是宿主受控读取，不进行模型推理。合法感知、空闲机会、冷却和单 Run/Goal 占用保护决定是否提供 `npc.task_consideration`。该事件不是 `player.utterance`。NO_ACTION 不触发动作或自动重复推理，忙碌期间保留必要事实，恢复后重新核对。隐藏但保留的对话会话不永久阻塞后台考虑；可见对话和真实占用仍受保护。

## 世界执行与取消

模型只有选择合法任务的 `ACTION_PROPOSAL + task_focus` 才请求执行：

1. Backend 核对来源、owner/player、Visit 触发、版本、能力、目标和在途执行，CAS 领取唯一 attempt。
2. Unity 核对实时场景、身份、感知、能力和 ticket，再通过 `task_control(begin_execution)` 获得一次开始许可。
3. 同一任务 Run 在安装结果所有权后可从 Running 进入 WaitingActionResult，仍经过原 WorldActionEligibility、Admission、Handoff、GoalPlanRunner 和运动占用检查。普通模式转换不因此放宽。
4. 原 `prepare_drink` 或 `clean_counter` 执行；整个 GoalOutcome 和目标特定的 WorldState receipt 全部匹配才结算 completed。

任一饮品都需要匹配 attempt、proposal、trace、scene、NPC、玩家、饮品 ID、世界 revision 和真实 DrinkReceipt；清理需要指定台面、有效条件版本及 CounterCleaningReceipt。台词、动画或走到制作点不证明完成。任务 effect guard 仍在效果提交时检查实时条件，普通玩家即时动作保留原链路。

玩家可在对话中请求取消 waiting 委托，模型也可自主选择 `update_task(cancel)`。游戏菜单仅为当前玩家有权取消的委托提供轻量入口，执行中取消通过原 Runner 尝试停止。点击不等于已停止或已持久取消。可信无效果取消才能变为 cancelled；真实成功效果优先保留，取消过晚不能回滚咖啡。

ESC 一级保留继续、设置、操作说明和退出；有活动委托或重要异常时才显示小型「当前委托」入口。二级面板显示可信 NPC、状态与取消资格，确认提交前重新核对任务、版本与 attempt，ESC 逐层返回。保存与取消成功在可信回执验证后复用右上角 Toast（约 2.8 秒）；重复回执、刷新和重连不重播。处理中、失败、过晚和 uncertain 仍保留准确说明，不伪装成成功。

`serve_drink` 复用 [Backend DrinkType](../AIBackend/app/drink_types.py) 的十种合法热饮 ID，与即时 `prepare_drink` 和 [Unity DrinkTypes](../UnityClient/Assets/Scripts/Actions/PrepareDrinkAction.cs) 保持一致；新增饮品不改变上述票据、收据和恢复边界。完整体验 ZIP 只读导出已保存的任务/命令及可归属行动结果，保留状态与证据，不把承诺或提案改写为成功，见[导出架构](ARCHITECTURE.md#read-only-complete-experience-export)。

## Release 任务诊断

沿用 `TASK_DIAGNOSTIC` 与 RuntimeTrace 的 technicalOnly 白名单。只记录真实协议支持、结构化 decision/future_commitment/operation、可信 persistence receipt、Visit/Bridge、可见性与门控、sync/eligible_count、考虑决策、准入、Runner 和可信结算。没有 waiting→已考虑、提案→已完成的推断，也没有新增模型调用。

日志不保存完整 Prompt、原始对话、模型 JSON、Key 或可复用机会 Token；诊断有去重及容量限制。用脱敏 task 关联标识、request/run、attempt/proposal 和时间追踪链路。缺日志也可能是过滤或容量上限，不能直接认定事件没有发生。便携包日志在 `UserData/logs`，安全导出在 `UserData/Exports`。

## 恢复与安全边界

状态区分 waiting、executing、completed、cancelled、blocked；未知执行保留 attempt/证据并标记 uncertain。领取不等于已经开始，明确未启动可以记录 not_started。网络断开、场景变化或新 Visit 不能自动释放未知在途执行，失败也不启动语义恢复模型循环重做。

已知结果按原命令重报并幂等结算；Unity 保留有界同进程待同步证据和明确取消命令，从不重放 begin_execution。没有新增 Durable Outbox，不能承诺任意跨进程崩溃恢复。少数无法核实的 blocked/uncertain 任务需要人工诊断，模型没有强行改为 completed 的权限。

当前控制协议依赖本机 Demo 和预期稳定玩家身份，不宣称具有公网多人认证、完整任务列表或复杂生活模拟能力。

## 历史验收记录（任务初次集成阶段）

阶段一至三 C 的定向存储、双端协议、自主机会、真实世界结果和取消恢复证据已完成。历史阶段测试数量不能作为当前整套全量回归结果。

最终正式 Cafe UI 验收使用 Scripted Provider，只替换模型输出，保留实际认知、服务、SQLite、感知、GameVisit、准入、Runner 和 WorldState。通过两次 Editor Play 的 next_visit 拿铁闭环、NO_ACTION、自主清理、waiting/executing 菜单取消，以及离线取消后重连确认。真实 GoalOutcome 与真实回执完成结算，重复结果没有新增世界效果。

最后一轮最小修复涉及任务 Schema/Prompt 预算、同一 Run 的行动交接状态以及隐藏对话门控；追加的 Backend 4 项与 Unity EditMode 4 项定向验证通过。存储迁移、版本、幂等和未知执行保护另复用相应阶段证据。

测试要求在应用模块导入前设置完整私有数据、配置、知识、索引和日志路径。Unity Editor 的便携数据根目录不能单独保证隔离。最终集成过程中曾因测试恢复场景丢失未保存隔离值，两次尝试默认端点但没有连接，新增两份默认追踪日志；已披露、保留证据并修正隔离流程，不能称为全程零接触。带固定测试端口/UID、依赖本地证据的 `TaskGameUiAcceptance` 是本地验收辅助，不纳入产品提交或发布依赖。

## 当前边界与历史限制

- 模型任务意图的语义正确性仍需实际体验判断，Scripted/Fake Provider 合同通过不等于真实模型理解始终正确。
- 当前新 Main 配置默认 32K，已有配置不自动升级。实际输入仍受输出/安全预留和最终载荷预算限制；复杂 Memory/Task 场景可以安全失败，不能以读取成功推断可执行。
- 结果无法核实时保留 blocked/uncertain 并提供诊断，不通过超时重制饮品伪装恢复成功。没有 Durable Outbox，不能承诺任意跨进程强杀、断电后自动恢复。
- 原阶段的「16K 余量很小」「两个 Standalone 进程来访未验收」「完整混合 UI / 发布构建 NOT_RUN」是当时的验收范围，不是 v0.1.3-preview 的整体当前状态。后续已集成 32K 新配置默认、委托二级 UI、Toast、混合读取、Windows 发行包与体验导出。后续发行验证以[当前发布说明](https://github.com/LYFshota/EmbodiedNPC/releases/tag/v0.1.3-preview)和[发布范围](RELEASE_STATUS.md)为准，不把旧 NOT_RUN 继续当作现状，也不把后续冒烟扩大为所有崩溃恢复或陌生机认证。

## 代码入口

- [存储模型](../AIBackend/app/task_models.py)、[迁移](../AIBackend/app/task_schema.py)、[Repository](../AIBackend/app/task_repository.py)、[Service](../AIBackend/app/task_service.py)。
- [认知适配](../AIBackend/app/cognitive_tasks.py)、[认知 DTO](../AIBackend/app/cognitive_task_models.py)、[执行控制](../AIBackend/app/task_execution.py)、[恢复与取消](../AIBackend/app/task_lifecycle.py)。
- [Unity 运行桥](../UnityClient/Assets/Scripts/Cognition/TaskRuntimeBridge.cs)、[考虑源](../UnityClient/Assets/Scripts/Cognition/TaskConsiderationSource.cs)、[执行桥](../UnityClient/Assets/Scripts/Cognition/TaskExecutionBridge.cs)、[生命周期](../UnityClient/Assets/Scripts/Cognition/TaskRuntimeLifecycle.cs)。
- [正式菜单](../UnityClient/Assets/CafeDemo/Scripts/CafeUiFlowController.cs)、[任务存储测试](../AIBackend/tests/test_task_service.py)、[认知测试](../AIBackend/tests/test_cognitive_tasks.py)。
