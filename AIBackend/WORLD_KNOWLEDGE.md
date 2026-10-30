# 世界知识与管理

当前 v0.1.3-preview 默认模板 [cafedemo_v2.json](app/knowledge/cafedemo_v2.json) 有 108 条知识；真实合同以 [world_knowledge.py](app/world_knowledge.py) 和[可信部署配置](app/knowledge/cafedemo_v2_deployment.json)为准。

- 默认模板只读。首次初始化或旧覆盖格式显式迁移后，玩家的 `data/world_knowledge.json` 是 `schema_version=2` 的完整目录；游戏更新不合并新默认内容。
- `access.mode=public` 面向部署/场景条件匹配的可信注册角色；`restricted` 只允许明确 CharacterId。缺失字段按旧 restricted 范围解释。
- `usage=constant` 经权限过滤后计入上下文预算；`retrieval` 使用现有词法 + Embedding + RRF、Top-K=3。仅语义投影改变才重算向量。
- 普通对话先自动预检索：通常使用当前玩家文本，短追问可结合当前会话最近话题；混合排序后以名称/关键词的词法话题支持筛掉无关预取。模型可在 Step 0 用 `CONTEXT_READ` 补查语义知识、Memory 和 task-aware Schema 允许的 Task Read，或直接终结；Step 1 必须终态，最多两次推理。自动与补查共用三个不同检索 ID 的额度，常驻不占 Top-K，也不会重复注入。
- Unity 设置提供搜索、类型/使用模式/访问范围/NPC 筛选、编辑、完整 JSON 导入导出和恢复系统默认。导入是完整替换，预览后 revision 变化会拒绝提交。
- 复用跨进程写锁、revision 校验、备份和原子写入。知识专用 JSON 上限 16 MiB；无目录级 128 条限制。
- 知识内容不是实时世界状态，不授予制作权限，也不是玩家真实经历。Memory、Relationship、Persona 和世界事务独立。

本地 CLI：从 AIBackend 运行 `python -m tools.manage_world_knowledge --help`。测试必须用独立 `--file`，先显式 initialize；普通读取不产生迁移或写入。运行和会话配置见 [SOURCE_SETUP](../Docs/SOURCE_SETUP.md)。

## 运行入口与边界

- [WorldKnowledgeResolver](app/world_knowledge_store.py) 的 capture/prefetch/query/validate_snapshot 在同一 Run 复用目录快照；目录内容或 revision 变化、读取失败都会使旧快照失效，不用撤权/删除前的结果继续发布回复或行动。
- [KnowledgeEmbeddingIndex](app/knowledge_embeddings.py) 是可重建 SQLite 派生索引。指纹来自实际 `embedding_text()`（ID、名称、类型、正文、attributes）及投影版本；模型空间和维度也需匹配。只改权限、usage、source 或管理版本/时间不重算向量。常驻不后台编码，切回 retrieval 时可复用仍有效向量。检索前仍按当前目录/权限过滤，旧缓存不能授予访问权。
- [model_requests.py](app/model_requests.py) 将常驻加入必需上下文预算，放不下时明确失败，不截断事实；其他知识/Memory/Task/Relationship 与最终 Provider 载荷共享预算。新 Main 配置默认 32K，保留旧保存值，见[记忆与预算](../Docs/MEMORY.md#scale)。
- [Knowledge Management API](app/knowledge_management_api.py) 与 [Unity 面板](../UnityClient/Assets/CafeDemo/Scripts/CafeWorldKnowledgePanel.cs) 复用同一 Store。UI 本地化只改变显示名，类型 ID/CharacterId/正文保持原样；权限和 NPC 筛选取交集。来源是内部元数据，不再作为普通玩家编辑/筛选项。
- [完整体验导出](app/experience_export.py) 只读导出当前完整目录，保留权限/usage；它不是导入、恢复默认或隐式模板同步。详细[架构](../Docs/ARCHITECTURE.md#read-only-complete-experience-export)与[内部读取协议](../Contracts/README.md#backend-internal-context-reads)说明各模块职责。
- 十种热饮知识只提供可聊的参考；可制作集合由 [DrinkType](app/drink_types.py) 和 Unity DrinkTypes/动作准入定义，世界知识不能新增能力、配方、库存或实时经营事实。
