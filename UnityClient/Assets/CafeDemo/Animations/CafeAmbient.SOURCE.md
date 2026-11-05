[简体中文](CafeAmbient.SOURCE.md) | [English](CafeAmbient.SOURCE.en.md)

# 咖啡馆日常动作来源与实现

这里的日常动作由项目内 `CafeAmbientSetup` 等工具制作成 Humanoid 动画曲线，沿用现有动作层、导航及授权组件。日常表现由 Unity 调度，不需要每次动作都调用大模型。

## 已使用的动作

| 动画 | 角色与用途 |
| --- | --- |
| `CafeSeatedDrink.anim` | 温予安与江亦舟坐姿端杯、饮用及放回 |
| `CafeAmbientWipe.anim` | 薇薇在吧台进行轻量擦拭表现 |
| `CafeServeSnack.anim` | 点心服务中的伸手、短暂保持与收回 |

### 坐姿喝饮品

`CafeSeatedDrink.anim` 是约 **6.2 秒**的右臂和手指动作，最初以温予安的 VRM 调整杯子接触点。约 1.1 秒到达杯子，2.4–4.1 秒饮用，5.1 秒释放，随后回到坐姿。

杯子沿当前手部锚点移动，完成或取消时恢复原本桌面位置。头、身体、Root 和双腿继续使用原坐姿动画与脚部 IK。温予安、江亦舟分别配置交互锚点和动作间隔；沈知意当前未启用喝杯动画。

### 吧台擦拭

`CafeAmbientWipe.anim` 使用项目已有擦拭动作的手臂表现，并根据薇薇模型修正手指接触点。环境擦拭属于视觉活动，正式 `CleanCounter` 任务仍由世界状态和对应执行器结算。正式任务、紧急状态和对话可能中断当前日常动作。

### 点心动作

`CafeServeSnack.anim` 是项目制作的手臂伸出、保持和收回片段，不生成实体食物。服务是否成功由动作执行结果确认。

## 扩展与许可

动画参考了现有 [Quaternius](ThirdParty/Quaternius/SOURCE.md) 与 [KayKit](ThirdParty/KayKit/SOURCE.md) 的已授权片段，新增曲线为项目内制作内容。为新 NPC 启用喝杯行为时，需要分别校准桌面位置、手部锚点和人物动作间隔。

[日常行为架构](../../../../Docs/ARCHITECTURE.md) · [第三方许可](../../../../THIRD_PARTY_NOTICES.md)
