[简体中文](README.md) | [English](README.en.md)

# CafeDemoV2

`Assets/Scenes/CafeDemoV2.unity` 是当前咖啡馆演示场景，正式启动入口为 `Assets/Scenes/TitleScene.unity`。

## 场景与角色

场景包含吧台、顾客桌椅、服务区和导航区域。星芒为玩家角色，四位 NPC 分别是：

- **林薇薇**：咖啡馆店员，可执行制作饮品、递物、点心服务等授权任务。
- **沈知意**：顾客。
- **温予安**：顾客。
- **江亦舟**：顾客。

每位 NPC 有独立身份、人设、感知、认知与记忆。场景复用项目的事件路由、行为模式、Mini-GOAP、动作和关系系统。部分日常活动由 Unity 本地调度。

## 编辑与运行

使用 Unity Hub 打开 `UnityClient`，运行 TitleScene。场景使用现有 NavMesh、CharacterController、NavMeshAgent 和人物动画组件。

源码依赖、模型资源和启动步骤见 [SOURCE_SETUP](../../../../Docs/SOURCE_SETUP.md)；整体数据流见 [ARCHITECTURE](../../../../Docs/ARCHITECTURE.md)。

## 素材与许可

场景使用 CozyCafe、VRM 角色及第三方动作素材。来源与修改记录见 [角色资源](Characters/SOURCE.md)、[第三方资源说明](../../../../THIRD_PARTY_NOTICES.md)。星芒采用 [单独的角色许可](../../VRMCompatibilityTest/Xingmang.LICENSE.md)，和项目原创代码的 Apache-2.0 许可分开。
