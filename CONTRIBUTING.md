# Contributing to EmbodiedNPC

欢迎任何人提交 Issue、建议和 Pull Request，包括问题复现、文档、测试、可读性改进及新功能。请先阅读 [README](README.md)、[架构](Docs/ARCHITECTURE.md) 和相关模块的合同；改动尽量聚焦，并说明实际验证和未运行的部分。

- 报错请附复现步骤、Unity/Python 版本和脱敏日志；不要上传 API Key、会话 Token、UserData、个人对话、数据库或模型权重。
- 修改运行行为时补充与风险相称的定向测试。Fake/Mock 测试、真实模型、Unity PlayMode、Windows Player 和净机器验收是不同证据，请分别标明。
- 保持权威边界：Unity 决定世界事实与动作结果；模型只提供有界意图；Memory 保留来源和主体隔离；Relationship 是主观解释，不能冒充历史或世界事实。唤醒认知不代表获准行动。
- 保留 TaskService 的版本、command/attempt 幂等、GameVisit/scene 关联及真实回执验证；重连不是重放执行的许可。协议、迁移、感知/记忆语义或新架构层的改动，宜先开 Issue 讨论。
- 注释解释目的、不变量、来源及安全边界，避免机械补注释、无关重构或批量格式化。不要为简化代码移除必要的事务、生命周期或拒绝校验。
- 仅提交有权贡献的内容，并保留第三方许可和来源。除非明确另有说明，向本项目提交以纳入第一方作品的贡献按 [Apache-2.0](LICENSE) 第 5 条处理；贡献者保留自己的著作权，不要求版权转让。资源仍按[独立许可](THIRD_PARTY_NOTICES.md)处理。

本地测试应在导入应用前隔离数据、配置、索引和私有会话路径。不要连接他人的 Backend 或付费模型来验证普通 PR；不要在有价值的玩家数据上运行测试、迁移或清理。
