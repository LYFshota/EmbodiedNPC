[简体中文](SOURCE.md) | [English](SOURCE.en.md)

# KayKit 动画来源与项目改作

项目使用 **Kay Lousberg** 的 **KayKit: Character Animations 1.1**，按随附 [CC0 许可](LICENSE.txt) 使用及改作片段。

来源：[作者网站](https://www.kaylousberg.com/) · [KayKit 发布页](https://kaylousberg.itch.io/)。

## 本项目使用的动作

| 动画 | 原始来源 | 用途 |
| --- | --- | --- |
| `Waving.anim` | Medium Simulation / Waving | 玩家与 NPC 挥手 |
| `Cheering.anim`、`Flexing.anim` | Medium/Large Simulation | 玩家表情动作 |
| `Holding_B.anim` | Medium Tools / Holding_B | 持杯及 Carry 上半身姿态 |
| `Idle_A.anim`、`Idle_Relaxed.anim` | Medium General / Idle_A | 站立待机及低幅度说话 |
| `Idle_Quiet.anim`、`Idle_Quiet_Vivi.anim` | 项目在 Idle_Relaxed 基础上改作 | 星芒与薇薇更安静的待机 |
| `Waving_PlayerRelaxed.anim` | 项目在 Waving 基础上改作 | 玩家自然挥手 |

片段使用 Unity Humanoid 导入并提取为独立 `.anim`，运行时复用已有 Animator、AvatarMask 和人物 Humanoid Avatar。移动与 Root Motion 仍由游戏控制组件决定。

## 项目调整

- **挥手**：使用动画原始手臂动作，结合现有上半身 Mask，保留角色头部和移动控制。
- **持杯**：选用更接近腰部的 `Holding_B` 姿势，通过现有持物绑定与世界状态追踪杯子。
- **待机**：`Idle_Quiet` 系列降低手臂和身体余量，让放松站姿与 VRM 角色外观更协调。
- **表情动作**：Cheering 与 Flexing 作为展示动画，不改变游戏事件的权限和实际行动结果。

原始素材与许可保持在本目录，项目改作片段与动画来源对应记录于此。其他动画见 [Quaternius](../Quaternius/SOURCE.md)。

[第三方资源说明](../../../../../../THIRD_PARTY_NOTICES.md)
