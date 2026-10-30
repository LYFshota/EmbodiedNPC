# 世界知识与管理

当前默认模板 `app/knowledge/cafedemo_v2.json` 有 108 条知识；真实合同以 `world_knowledge.py` 和可信部署配置为准。

- 默认模板只读。首次初始化或旧覆盖格式显式迁移后，玩家的 `data/world_knowledge.json` 是 `schema_version=2` 的完整目录；游戏更新不合并新默认内容。
- `access.mode=public` 面向部署/场景条件匹配的可信注册角色；`restricted` 只允许明确 CharacterId。缺失字段按旧 restricted 范围解释。
- `usage=constant` 经权限过滤后计入上下文预算；`retrieval` 使用现有词法 + Embedding + RRF、Top-K=3。仅语义投影改变才重算向量。
- 认知自动预检索必要世界知识，模型可在 Step 0 用 `CONTEXT_READ` 补查知识/Memory/合法 Task Read，Step 1 必须终态；最多两次推理。
- Unity 设置提供搜索、类型/使用模式/访问范围/NPC 筛选、编辑、完整 JSON 导入导出和恢复系统默认。导入是完整替换，预览后 revision 变化会拒绝提交。
- 复用跨进程写锁、revision 校验、备份和原子写入。知识专用 JSON 上限 16 MiB；无目录级 128 条限制。
- 知识内容不是实时世界状态，不授予制作权限，也不是玩家真实经历。Memory、Relationship、Persona 和世界事务独立。

本地 CLI：从 AIBackend 运行 `python -m tools.manage_world_knowledge --help`。测试必须用独立 `--file`，先显式 initialize；普通读取不产生迁移或写入。运行和会话配置见 [SOURCE_SETUP](../Docs/SOURCE_SETUP.md)。
