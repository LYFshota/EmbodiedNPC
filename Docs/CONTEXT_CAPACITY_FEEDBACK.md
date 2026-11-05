[简体中文](CONTEXT_CAPACITY_FEEDBACK.md) | [English](CONTEXT_CAPACITY_FEEDBACK.en.md)

# 模型上下文容量与提示

EmbodiedNPC 为新建主模型配置预设 **32K（32768）上下文预算**及 **1024 输出 Token**。这些是本地请求预算，实际应与所选模型的窗口和服务商配置一致。已保存的 8K、16K 或自定义值保持不变。

## 在游戏中调整

进入 **设置 → AI 配置 → 高级设置**，选择 8K、16K、32K、64K 或填写自定义容量。保存并应用；如果提示待重启，再按游戏入口完成 Backend 重启。

本地 Ollama 会使用配置的 `num_ctx`。云端模型按所设置的容量进行请求预算分配。

## 预算器如何工作

`ContextBudgetAllocator` 统一统计系统规则、对话、世界知识、记忆、关系、任务、输出预留与安全余量。预算不足时先按相关性和优先级减少可选内容，并尽量保持完整的事实与证据组。

实际发送给模型的输入还包含结构化输出 Schema 等成本，因此 **32K 不是 32K 的纯输入正文**。当前默认输出预留 1024、估算安全预留 3072 时，32K 的计算输入上限为 28672。

## 容量提示

- **WARNING**：认知成功，但某些重要的近期对话、记忆或知识因预算被排除。
- **ERROR**：必要输入无法放进有效窗口，或模型服务返回容量/输出限制。

游戏界面显示与当前来源关联的提示。调整模型配置后，`POST /v1/context-budget/recheck` 可使用脱敏成本信息复核容量问题，复核过程不需要再次生成对话。

WARNING 可在设置中关闭提醒；ERROR 会保留到相应问题解决。诊断记录只包含容量和关联元信息，避免把完整玩家对话写入诊断。

## 技术参考

`context_diagnostics_version=1` 允许 Unity 接收 `context_status`。每次普通认知最多两次预算检查，分别对应 Step 0 和可选 Step 1。

[整体架构](ARCHITECTURE.md) · [Backend](../AIBackend/README.md) · [源码运行](SOURCE_SETUP.md)
