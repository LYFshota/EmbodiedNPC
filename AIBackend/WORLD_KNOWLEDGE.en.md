[简体中文](WORLD_KNOWLEDGE.md) | [English](WORLD_KNOWLEDGE.en.md)

# World Knowledge and Management

World Knowledge provides **authored background** about NPCs, the café, drinks and the surrounding setting. The current [café template](app/knowledge/cafedemo_v2.json) includes **161 entries**. The Unity settings UI lets players search, edit, scope by NPC and import/export their own catalog.

## Sources and usage

- The bundled template initializes the catalog and supports explicit restore. User edits live in `data/world_knowledge.json` (schema 2) and persist across game updates.
- `access.mode=public` applies to eligible characters and deployments; `restricted` lists permitted CharacterIds.
- `usage=constant` adds authorized background directly to context. `retrieval` uses lexical and vector matching with RRF fusion; related entries currently use Top-K 3.
- Knowledge is reference information. Live Unity state, actual player experience and action authority are managed separately.

## Cognition reads

Before the first ordinary model inference, the Backend automatically retrieves relevant knowledge based on the current player topic. Short follow-ups may use the latest session topic. Automatic prefetch and later supplementary reads use a consistent catalog snapshot.

If Step 0 needs more information, one `CONTEXT_READ` can combine a knowledge query with memory and permitted task reads, followed by terminal Step 1. Automatic and supplementary retrieval expose up to three distinct entry IDs; constant background uses its own budget.

## Editing and storage

The Unity panel supports type, usage, access and NPC filters, entry editing, full JSON import/export and restore. Imports offer a preview and verify the current catalog revision at commit.

`WorldKnowledgeStore` owns the catalog and authorization. `KnowledgeEmbeddingIndex` maintains a rebuildable SQLite vector index. Embeddings change when indexed content changes; authorization updates are checked at read time. Writes use locking, backups and atomic replacement.

CLI usage from `AIBackend`:

```powershell
python -m tools.manage_world_knowledge --help
```

Implementation: [Management API](app/knowledge_management_api.py), [Model Requests](app/model_requests.py), [Embedding Index](app/knowledge_embeddings.py).

## Relationship to other context

Persona describes who a character is; World Knowledge provides authorized setting information; Memory preserves what that NPC experienced. Knowledge text does not add new drink IDs or execution capabilities. Service actions are defined by [DrinkType](app/drink_types.py) and Unity admission.

See [Architecture](../Docs/ARCHITECTURE.en.md), [Memory](../Docs/MEMORY.en.md), and [Contracts](../Contracts/README.en.md).
