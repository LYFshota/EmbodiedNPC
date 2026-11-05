[简体中文](README.md) | [English](README.en.md)

# AIBackend

Python 后端处理 NPC 的**认知推理、世界知识、长期记忆、角色关系、持久任务和模型调度**。Unity 提供当前游戏事件、感知快照、角色能力和执行结果，并拥有世界状态的最终决定权。

## 核心链路

```text
Unity AgentEvent → CognitiveRun → CognitiveRequest (/ws)
→ Backend 组装人设、最近对话、关系与授权知识
→ Step 0：最终决策 / CONTEXT_READ
→ 可选一次知识、记忆、任务联合读取
→ Step 1：最终结构化决策
→ 校验 → Unity 呈现回复或执行合法行动
```

普通认知最多两次模型调用。后台 Episode 整理、关系反思与维护任务复用有界模型执行器，采用前台优先与后台公平策略。

## 启动（Windows 源码环境）

准备 Python 3.12 x64，进入仓库根目录：

```powershell
cd AIBackend
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
$env:EMBODIEDNPC_PROVIDER = 'mock'
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
```

实际游戏和模型的完整配置见 [源码运行指南](../Docs/SOURCE_SETUP.md)。Windows 试玩包使用自带启动器，不需要按上面的开发命令启动。

## 模型配置

游戏内「AI 配置」可分别设置 Main LLM 与 Embedding：

- Main 适配器：Mock、Ollama、DeepSeek Official Responses、OpenAI-compatible、Gemini Native、Anthropic Native。
- Embedding 可以使用内置模型或单独配置的服务，用于长期记忆索引与向量检索。
- 新建 Main 配置默认 32K 请求预算，可按实际模型窗口调整。
- 配置保存、激活后，影响模型调用的改动按提示重启 Backend 生效。

配置文件位于 `data/model_api_configs.json`；如果文件不存在，可以通过环境变量初始化。模型和 Key 的具体字段以游戏配置 UI 和 `/v2/model-config` DTO 为准。API Key 属于个人凭据，请保存在本机配置中。

## HTTP 与 WebSocket

| 入口 | 作用 |
| --- | --- |
| `GET /live` | 进程存活检查 |
| `GET /health`、runtime-status | 模型服务与运行状态 |
| `/v2/model-config/*` | 模型配置、激活、测试与模型发现 |
| `/ws` | 认知请求、取消、经历写入及持久任务同步 |
| 知识与索引管理接口 | 世界知识编辑、检索索引及上下文配置 |

Backend 在本机为 Unity 与受保护 HTTP/WS 生成私有会话凭据。Unity Editor 和便携启动器会按各自运行方式读取会话文件。个人配置、会话文件和 UserData 不应上传到公开仓库。

需要对接新客户端时，先阅读 [通信协议](../Contracts/README.md)。其中列出了请求版本、关联标识、模型输出格式和主体权限。

## 数据与运行机制

- **Experience / Episode / Memory**：真实对话与行动结果先进入经历账本，按 Episode 异步整理为可召回的长期记忆。
- **Relationship**：根据特定 NPC 与特定对象的实际经历独立维护关系摘要。
- **World Knowledge**：常驻背景与按话题检索相结合，受角色权限和上下文预算约束。
- **TaskService**：保存、考虑、取消和结算有限的未来委托。
- **ActionProposalValidator**：对模型提出的动作与绑定进行第一道校验，Unity 再依据当前世界状态准入。

## 开发者入口

- [架构](../Docs/ARCHITECTURE.md) · [记忆](../Docs/MEMORY.md) · [持久任务](../Docs/NPC_TASK_SYSTEM.md)
- [世界知识](WORLD_KNOWLEDGE.md) · [模型与容量](../Docs/CONTEXT_CAPACITY_FEEDBACK.md)
- [源码启动](../Docs/SOURCE_SETUP.md) · [本地运行与隔离](../Docs/OPERATIONS.md)
