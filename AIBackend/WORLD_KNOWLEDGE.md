# 世界知识与开发者配置

世界知识是开发者提供的场景与设定资料，与玩家对话产生的 Experience、Episode、长期记忆及 NPC 主观关系分开。知识检索不能将模型输出或主观解释变成已经发生的世界事实；Unity 仍决定实际动作结果。

本工程包含结构化知识存储、独立 Hybrid RAG 及受保护的本机知识管理接口。具体接口以 [Backend](README.md) 和现有 `app/knowledge_management_api.py`、`app/world_knowledge_store.py` 为准；本页是公开源码入口摘要，不复制私人研发报告。

运行数据默认在本 checkout 的 `AIBackend/data`，不会随源码提供。多个 checkout 或测试时，应在导入应用前设置全部数据、索引、模型配置及会话路径；参见 [隔离说明](../Docs/OPERATIONS.md)。不要把私人知识文件、对话、Key、数据库或会话复制进发布树。

Embedding 与 Main 独立配置。关闭或缺少向量资源时，不应声称向量检索已启用；按现有错误提示与降级行为处理，不放宽准入、容量或业务校验。配置 Main/Embedding、保存重启和模型资源取得步骤见 [SOURCE_SETUP](../Docs/SOURCE_SETUP.md)。

既有技术验收与其未验证部分见 [发布状态](../Docs/RELEASE_STATUS.md)。本轮没有运行知识、模型或 Backend 测试。
