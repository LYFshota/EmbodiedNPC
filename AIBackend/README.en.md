[简体中文](README.md) | [English](README.en.md)

# AIBackend

The Python backend provides **NPC cognition, world knowledge, long-term memory, relationships, persistent tasks, and model scheduling**. Unity supplies game events, perceptual snapshots, available capabilities, and executed outcomes. Unity remains authoritative for live world state.

## Cognitive flow

```text
Unity AgentEvent → CognitiveRun → CognitiveRequest (/ws)
→ Backend assembles persona, recent dialogue, relationship and authorized knowledge
→ Step 0: terminal decision or CONTEXT_READ
→ Optional one-batch knowledge / memory / task read
→ Step 1: terminal structured decision
→ Validation → Unity presents speech or admits an action
```

Ordinary cognition makes at most two model calls. Background Episode consolidation, relationship reflection and memory maintenance share the bounded executor, with foreground priority and background fairness.

## Start from source (Windows)

Prepare Python 3.12 x64, then run from the repository root:

```powershell
cd AIBackend
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --no-proxy-headers
```

See [Source Setup](../Docs/SOURCE_SETUP.en.md) for the full Unity and model workflow, and [Operations](../Docs/OPERATIONS.en.md) for isolated Mock testing. Windows preview packages have their own launcher.

## Model configuration

The in-game AI settings configure the Main LLM and Embedding independently:

- Main adapters: Mock, Ollama, DeepSeek Official Responses, OpenAI-compatible, Gemini Native and Anthropic Native.
- Embedding may use the bundled model or a separately configured service for the memory index and vector retrieval.
- New Main profiles default to a 32K request budget, adjustable to the deployed model window.
- Save and activate configuration, then restart Backend when the UI requests it for invocation-affecting changes.

Configuration is stored in `data/model_api_configs.json`. Environment variables can initialize the provider if no config file exists. For exact fields, use the game UI and `/v2/model-config` DTO. Keep API keys private.

## HTTP and WebSocket

| Endpoint | Purpose |
| --- | --- |
| `GET /live` | Process liveness |
| `GET /health`, runtime-status | Provider and runtime status |
| `/v2/model-config/*` | Profiles, activation, probes and model discovery |
| `/ws` | Cognition, cancellation, Experience append and task sync |
| Knowledge and index APIs | World-knowledge management and context settings |

The Backend generates local session credentials for protected Unity and HTTP/WS access. Unity Editor and the portable launcher discover the session through their respective local paths. Keep personal configuration, sessions and UserData out of public commits.

See [Wire Contracts](../Contracts/README.en.md) when integrating a new client. They describe versioned messages, correlation IDs, structured model output and identity scoping.

## Data and services

- **Experience / Episode / Memory:** durable evidence from presented dialogue and actual action outcomes is grouped and consolidated asynchronously.
- **Relationship:** pair-scoped social summaries based on qualified experiences.
- **World Knowledge:** constant background and topic retrieval under character access rules and context budgets.
- **TaskService:** stores, considers, cancels and settles limited future commitments.
- **ActionProposalValidator:** validates the model proposal before Unity performs independent live-world admission.

## Developer guides

[Architecture](../Docs/ARCHITECTURE.en.md) · [Memory](../Docs/MEMORY.en.md) · [Tasks](../Docs/NPC_TASK_SYSTEM.en.md) · [World Knowledge](WORLD_KNOWLEDGE.en.md) · [Context Budget](../Docs/CONTEXT_CAPACITY_FEEDBACK.en.md) · [Source Setup](../Docs/SOURCE_SETUP.en.md) · [Operations](../Docs/OPERATIONS.en.md)
