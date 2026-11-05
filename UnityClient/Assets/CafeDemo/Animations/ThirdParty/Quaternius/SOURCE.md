[简体中文](SOURCE.md) | [English](SOURCE.en.md)

# Quaternius Humanoid 动画来源

项目使用 **Quaternius · Universal Animation Library（Standard）** 中的动作，按照 [CC0 1.0](LICENSE.txt) 许可使用。

来源：[官方网站](https://quaternius.com/packs/universalanimationlibrary.html) · [发布页](https://quaternius.itch.io/universal-animation-library)。

原始本地 FBX：`SceneResources/Universal Animation Library[Standard]/Unity/UAL1_Standard.fbx`，SHA-256：

```text
21B32D912DA3CB93426D974FB945E86F5B2E86970ACD2CE89905E0FBF9F1DCC2
```

## 项目使用的片段

| 独立动画 | 用途 |
| --- | --- |
| `Walk_Loop.anim` | 站立角色移动 |
| `Idle_Loop.anim` | 常规站立待机 |
| `Idle_Talking_Loop.anim` | 说话时的上半身轻动作 |
| `Sitting_Idle_Loop.anim` | 顾客坐姿基础层 |
| `Sitting_Talking_Loop.anim` | 坐姿对话兼容素材 |

Unity 将原始 FBX 作为 Humanoid 导入，提取上述动作并保存成可单独使用的 `.anim`。发行工程使用提取后的片段，不需要保留完整源 FBX。

## 播放方式

移动基础层由角色现有的 `NavMeshAgent` 和 LocomotionPresenter 控制；动画关闭 Root Motion，保证游戏的位移由 Unity 世界系统决定。

对话动画通过上半身 Mask 叠加，不接管 Root、双腿或 HeadGaze。坐姿顾客继续使用现有脚部 IK，保持角色和座椅的接触。

原始授权与来源见本目录的 `LICENSE.txt`；相关玩家挥手与持杯姿态来自 [KayKit](../KayKit/SOURCE.md)。

[第三方资源说明](../../../../../../THIRD_PARTY_NOTICES.md)
