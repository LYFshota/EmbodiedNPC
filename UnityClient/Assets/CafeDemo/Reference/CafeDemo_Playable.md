# CafeDemo 运行与集成

场景：`Assets/Scenes/CafeDemo.unity`。Player 使用 CafeDemoPlayerController 和 CafeDemoOrbitCamera；WASD 移动，鼠标右键视角，滚轮缩放，E 与可见/范围内 NPC 对话，Escape/取消隐藏对话，G Wave。

Merchant 与 RegularCustomer 独立运行 perception/cognition。Merchant 有 bring_object；Customer 无 world-action capability。Settings 提供 Overview、Model/API、NPC Character、Debug、Runtime Trace。每次 Play 的配置检查后自动打开 Model/API；模型配置保存后 restart apply，Persona 七字段中的六项编辑则下一次新 Run 生效、无需重启。Merchant 和 Customer 切换时加载各自有效 profile。

合法 bring_object 通过 admission、planning、WalkTo、PickUp、Carry、Give 与 GoalOutcome；结果由 Unity 决定。Merchant 在 task/run/dialogue 结束且 engagement 到期后 ReturnToHome。Give-to-Actor 转移 ownership 后隐藏 Renderer/Collider/Perceivable，保留 HeldItemState 与 entity identity。Dialogue bubble 与 gesture 都不改变世界真值。无 conversation 的 Wave reply 可展示/Trace，不写 Dialogue Memory；真实 Dialogue 正常持久化。

`CafeDemo.Playable.Tests` 的 Editor runner 显式进入/退出 Play Mode，运行真实 Input System、CharacterController、NavMeshAgent 与当前场景 UI。场景无关的合同与数学测试位于 `Assets/Tests/Editor`。只执行请求指定的 targeted class，不跑历史 fixture 或压力重复。

Backend 启动与隔离见 [OPERATIONS](../../../../Docs/OPERATIONS.md)；验证证据与 NOT_RUN 项见 [CURRENT_TEST_MATRIX](../../../../Docs/RELEASE_STATUS.md)。下一阶段 Demo Recording Preparation 尚未开始，本页不提供已完成录制的声明。MainCamera smoke 需要检查实际 UI/角色截图，静态 hierarchy 不能替代运行证据。
