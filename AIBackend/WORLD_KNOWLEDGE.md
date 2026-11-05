[简体中文](WORLD_KNOWLEDGE.md) | [English](WORLD_KNOWLEDGE.en.md)

# 世界知识与管理

世界知识提供角色、咖啡馆、饮品和街区等**可被 NPC 使用的背景信息**。当前内置 [咖啡馆模板](app/knowledge/cafedemo_v2.json) 包含 **161 条**知识。玩家可以在 Unity 设置页面中按 NPC 权限管理、搜索、编辑、导入和导出。

## 数据来源与使用模式

- 内置模板用于首次初始化及主动恢复默认。玩家编辑的完整知识目录保存在 `data/world_knowledge.json`（schema 2），更新游戏后继续使用自己的目录。
- `access.mode=public` 适用于允许访问的角色和部署场景；`restricted` 指定可访问的 CharacterId。
- `usage=constant` 为常驻背景，在权限筛选后直接进入上下文；`retrieval` 则使用关键词、向量和 RRF 融合检索。当前相关知识 Top-K 为 3。
- 世界知识描述角色被授权知道的背景，不代替 Unity 世界状态、玩家经历或动作权限。

## 认知如何读取知识

普通对话在第一次模型推理前，依据玩家当前话题自动预检索知识。短追问可结合最近的会话话题。自动预取和模型后续补查复用同一目录快照。

如果 Step 0 判断还需要更多资料，可以通过一次 `CONTEXT_READ` 联合查询世界知识、长期记忆及适用的任务信息，随后由 Step 1 输出最终结果。自动预取与补查合计最多展示三个不同的检索条目；常驻背景独立计入预算。

## Unity 编辑与后台管理

Unity 面板支持按类型、使用模式、权限和角色筛选，编辑条目，以及完整 JSON 导入、导出与恢复默认。导入前提供预览，最终提交使用版本检查。

后端通过 `WorldKnowledgeStore` 负责权限、完整目录和版本，通过 `KnowledgeEmbeddingIndex` 管理可重建的 SQLite 向量索引。只有参与向量内容的字段变化才需要重新编码；权限更改在读取时立即应用。目录写入使用锁、备份和原子更新。

从 `AIBackend` 目录可运行：

```powershell
python -m tools.manage_world_knowledge --help
```

管理 API、模型请求和索引代码分别位于 [knowledge_management_api.py](app/knowledge_management_api.py)、[model_requests.py](app/model_requests.py)、[knowledge_embeddings.py](app/knowledge_embeddings.py)。

## 与其他系统的关系

Persona 负责“角色是谁”，World Knowledge 负责“角色知道什么背景”，Memory 负责“角色亲历了什么”。知识内容不会自动增加咖啡制作菜单或新的动作能力；实际服务范围由 [DrinkType](app/drink_types.py) 和 Unity 行动准入定义。

参见 [整体架构](../Docs/ARCHITECTURE.md)、[记忆](../Docs/MEMORY.md) 和 [协议](../Contracts/README.md)。
