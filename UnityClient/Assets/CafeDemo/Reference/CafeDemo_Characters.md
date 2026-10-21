# CafeDemo 当前角色

Player 与 NPC 的 Visual wrapper 分别为 `Assets/CafeDemo/Prefabs/Characters/PlayerVisual.prefab` 和 `NPCVisual.prefab`，复用项目导入的 LowPoly man_1 / woman_2 模型及有效 Humanoid Avatar。第三方原资源和许可保持原样；Visual 不承担 collider、NavMesh 或世界 authority。

Player root 使用 CharacterController、CafeDemoPlayerController、PlayerLocomotionPresenter、PlayerEmoteController。Merchant / RegularCustomer root 分别持有 WorldEntity、Perception、CognitiveRun 和配置；Merchant 带当前 Default Routine 与 action capability，Customer 无 bring_object。

Animator 关闭 Root Motion。当前表现包括 Idle/Walk、Talking、procedural Nod、KayKit Wave、Facing、Head Gaze 与 Carry。Carry follower 跟随 Animator 最终手部 pose，持有真值来自 HeldItemState。Give-to-Actor 成功后物品退出世界呈现，不删除 entity identity。

## Character Persona v2 Lite

当前七字段：CharacterId（120，只读）、DisplayName（80）、RoleSummary（240）、Backstory（600）、Personality（600）、SpeakingStyle（480）、Boundaries（600）。Settings → NPC 编辑六项内容，复用当前角色/角色背景/人格与表达三个 Section；不回写 Scene、Prefab 或 CharacterDefinition 默认资产。

Merchant 为 cafe.merchant / 店员：温和慢热、克制服务、懂咖啡但不卖弄。RegularCustomer 为 cafe.regular_customer / 顾客：随性、口语、个人意见，不承担制作/递送等店员职责。完整默认内容见 `Assets/CafeDemo/Characters/CafeMerchantCharacter.asset` 和 `CafeRegularCustomerCharacter.asset`。

本机 override schema 2 兼容 schema 1：旧三项保留，新三项 fallback 真实内置值，加载不写文件，下次 Save 原子升级。新 Run 使用新 profile，旧 request 保持快照；Restore 删除当前 entry 并立即刷新 UI，保存全部默认值也删除 entry。Persona 不是 Memory/Relationship/Perception/Capability/WorldState authority。

Give-to-Actor 成功后 ownership 转移，Renderer/Collider/Perceivable 关闭；WorldEntity、HeldItemState、EntityRegistry identity 保留。没有 Player Carry、Equipment、Inventory UI 或 staged PickUp/HandOver。

## Not implemented / possible extension

Facial Expression、LipSync、TTS/ASR、runtime PMX/VRM mod loader 未实现。Facial/Replaceable Character Visual 只是 OPTIONAL DISPLAY ENHANCEMENT；Humanoid/PMX/VRM adapter 为可能的未来方向，不是当前必做。

人物与 UI 的实际运行检查使用 MainCamera；测试使用当前 Cafe scene，不以旧占位模型或旧任务房间作为基准。
