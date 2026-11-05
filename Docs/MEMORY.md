# EmbodiedNPC Memory

EmbodiedNPC stores source-qualified dialogue, observations, and action outcomes as **NPC-local Experience**. Episodes group those experiences; asynchronous consolidation turns them into durable memories that can be recalled in later conversations. Relationship reflection maintains a separate, pair-scoped social understanding.

Persona describes the character, World Knowledge supplies authored background, and Memory records what actually happened. Unity remains the source of truth for the current world and completed actions.

Canonical Memory includes Episodic records, Semantic Facts, Derived Insights, evidence links, and an Embedding index. The current memory model and retrieval flow are described below.

## Data model

```text
World / Dialogue / ActionOutcome
→ observer-specific Experience
→ append-only ExperienceLedger
→ Episode
→ asynchronous Consolidation (memory-consolidator/3.0)
→ long-term Episodic Memory / open Semantic Fact
→ low-frequency Maintenance / cross-Episode DerivedInsight
→ asynchronous shared Embedding index
→ governed Recall
→ future CognitiveDecision
```

Canonical storage includes `memory_records`, `memory_subjects`, `memory_evidence`, `memory_semantic_facts`, `memory_derived_insights`, source links and `memory_embeddings`. Schema 16 introduced open Semantic Facts, 17 added Maintenance, and 18 added multi-Episode Derived Insights. Historical migrations remain available for review.

## World, Experience, Episode, Memory

These are deliberately separate concepts.

### World

What is currently true in Unity.

### Experience

What a particular NPC actually experienced, heard, learned, or executed.

Experience is durable source evidence and carries observer/provenance metadata.

### Episode

A semantic grouping of contiguous Experience belonging to one observer.

Episode does not copy a second `dialogue[]` or `actions[]` history. The ExperienceLedger remains the evidence source of truth.

### Long-term Memory

Consolidation admits independent `EPISODIC` experiences and open atomic `SEMANTIC_FACT` claims. Low-frequency Reflection forms `DERIVED_INSIGHT` from multiple Episodes, clearly marked as a tentative interpretation with original evidence. Relationship Reflection remains a separate social interpretation layer.

Historical memory cannot override current Unity truth.

## Semantic Episode boundary

Runtime v2 supports a minimal semantic directive:

```text
CONTINUE
END
```

A semantic `END` is only consumed through a real durable Experience carrier. The carrier Experience is included first, then the Episode is closed.

Deterministic safeguards still exist:

- source-session lifecycle;
- idle/max-events/max-duration limits;
- sequence-gap protection;
- restart/replay recovery;
- one-open-Episode and no-overlap invariants.

The LLM therefore contributes semantic meaning without becoming the sole lifecycle authority.

## Persistent participant identity

Long-term continuity requires separating the player's current world instance from the persistent player identity:

```text
entity_id
= current player instance used by World/Action

player_uid / persistent identity
= trusted long-term subject identity used by Memory/Relationship
```

A reconnect or scene transition may produce a new `entity_id` while preserving the same persistent identity.

Display name is never used as the identity key.

Persistent identity is runtime/backend metadata; it does not need to be exposed verbatim to the model prompt.

## Consolidation

A finalized Episode is reconstructed from its exact Experience range and can be consolidated asynchronously.

Current Consolidation:

```text
Finalized Episode
+ Character
+ relevant prior Memory
+ participant/provenance metadata
→ one structured consolidation call
→ deterministic admission
→ durable Episodic Memory / Semantic Fact
```

The raw ExperienceLedger remains replayable even if derived memory or indexes are rebuilt.

The model preserves negation, time, intent, conditions, and subject relationships.
Backend validates trusted handles, cited evidence, literal excerpts, lifecycle and
transaction integrity; excerpt location does not prove semantic entailment. Normal
saving performs one extraction call and never waits for Maintenance or Reflection.

## Low-frequency Maintenance and Reflection

One persistent Maintenance Worker shares the existing foreground-priority executor.
It polls every 30 seconds; Maintenance becomes eligible at four pending facts per
scope or after 300 seconds. Scoped SQL candidates are bounded before vector/lexical
ranking. LLM-proposed DUPLICATE, REFINES, COEXISTS, SUPERSEDES, CONFLICTS and
UNDETERMINED relations retain original evidence. Explicit supersession requires
locatable correction/change evidence; confidence, similarity and insertion time alone
cannot retire a fact. Conflicts preserve both sides. Calls occur outside transactions;
revision/attempt checks and short atomic commits protect updates, retries and undo.

Reflection uses the same Worker with a separate task kind. Defaults require at least
three source Episodes and a 300-second wait; inputs contain at most eight direct
memories. Each Insight cites memories and original evidence from multiple Episodes.
Schema 18 stores explicit memory/Episode source links, rather than a fabricated single
Episode or Consolidation Job. DerivedInsight is excluded as original reflection input;
consumed batches and valid NO_INSIGHT results are recorded. Social impressions remain
the responsibility of Relationship Reflection, not general Reflection.

## Observation sampling

Real-time Perception and SpatialMemory continue refreshing. At the Observation-to-
Experience boundary, continuous visual position changes below 0.25 metres are skipped
only when all other fields, source qualifications and lifecycle remain unchanged.
Displacement is compared with the last admitted sample, so gradual movement accumulates.
Visibility transitions, meaningful state changes, GameplayEvents, Dialogue and typed
Action/Goal outcomes remain eligible. Episode timeout and background features remain enabled.

## Recall

The current canonical recall path is on-demand within the hybrid two-step dialogue flow:

```text
Authorized constant background + automatic world-knowledge prefetch
→ Step 0: terminal decision OR one CONTEXT_READ batch
→ optional knowledge_plan / memory_plan / task_plan (task-aware only)
→ independent validated reads + per-source result states
→ shared final-payload budget + knowledge snapshot checks
→ Step 1: terminal decision only
```

Legacy `MEMORY_RECALL(recall_plan)` and `TASK_READ(task_query)` remain compatible. They consume the same single batch opportunity; a second read is rejected, and a direct Step 0 answer needs no second model inference. Normal turns never retrieve the whole memory store. World Knowledge and Memory keep separate retrievers; a knowledge hit is not runtime Memory, and a failed source does not mean the fact does not exist. See [internal contracts](../Contracts/README.md#backend-internal-context-reads).

### Semantic subjects and shared evidence

[MemoryRecallPlan](../AIBackend/app/cognitive_step_models.py) accepts a semantic query, optional memory type and `subject_role`/`subject_ref`. [CognitiveMemoryRecall](../AIBackend/app/cognitive_memory_recall.py) resolves these through the trusted [RecallSubjectCatalog](../AIBackend/app/recall_subjects.py), not model-supplied persistent IDs:

| Role | Meaning and boundary |
| --- | --- |
| `requester` | The current trusted player's statements, preferences or experiences; it does not mean the NPC owning the store. |
| `observer` | This persistent NPC's own statements/experiences. |
| `shared` | Candidates from this observer's authorized history, narrowed using original Ledger evidence involving this NPC and the current player. It is not a cross-NPC/global store or permission expansion. |
| `catalog` | Another authorized subject selected by a required catalog `subject_ref`. |

Legacy plans without a role still resolve their supplied ref; a null ref is only an open-subject search within this observer's authorized history. Missing/unauthorized identity is explicit, never an unscoped fallback. `shared` requires raw event ID/sequence and persistent-participant matches, excludes `HEARD` provenance, and can only remove existing bounded candidates. If the requester or Ledger is unavailable it returns unavailable; an empty qualified set is `NO_MEMORY_FOUND`. Same CharacterId or display name does not merge different persistent NPC instances or players.

### What proves a past conversation?

`NPC_SELF_STATEMENT` means the NPC made a source-qualified statement; its summary alone does not prove it was shown to this player. `qualify_self_statement_delivery` reopens the immutable Ledger sources and checks dialogue_response type, event/sequence, self-executed provenance, actor/observer identity, nonempty speech and the current persistent player recipient. Only that yields `PRESENTED_TO_REQUESTER`; otherwise delivery is `UNKNOWN`. This certifies delivery of the linked speech, not the truth of everything the NPC said.

[Model request assembly](../AIBackend/app/model_requests.py) separately projects retained pair-local RecentDialogue/recent interactions and `PRESENTED SPEECH EVIDENCE JSON`. It distinguishes the player's latest question from an actual NPC answer and recomputes presentation metadata after budget selection. Generated but unpresented text is not displayed history. Missing/evicted evidence is unknown, not proof that a conversation never happened. World Knowledge biographies or preset events do not establish that a story was told to this player. A recorded plan proves the discussion/intent, not its later execution; speculation and expressive narration remain possible without asserting unsupported shared history or completed actions.

Current world/perception always has priority over historical evidence. Recall distinguishes `SEMANTIC_FACT`, `EPISODIC` and tentative `DERIVED_INSIGHT`. Current excludes superseded facts and Insights with stale source revisions; All History retains historical labels. Conflicts remain unresolved, not a unique truth. Insight is not proof of a world action or permission. Types share the existing async Memory Embedding index and safe fallback when vectors are pending/unavailable; the separate World Knowledge index does not change Memory's identity semantics.

Memory P2 distinguishes an Episodic `EVENT` from an attributed `DIALOGUE_EXCHANGE` candidate. The latter requires this persistent NPC and one persistent Player, direct input and response evidence, and both participants in every cited event. Admission/2.1 retains it in the existing EPISODIC store with a backend-owned dialogue prefix and `DIALOGUE_ASSERTIONS_NOT_WORLD_PROOF` label. It certifies the conversation, not narrated world events, other actors' behavior or completed actions. NPC-only accounts retain the existing self-statement path; untyped world claims do not gain dialogue admission. Read-time source verification hides an exchange whose full pair evidence is missing. Recall, ranking, subject authorization and Cognition reuse the existing chain; no table, migration, additional inference or autobiographical subsystem is introduced. RecentDialogue may intentionally span sessions: Memory-only validation must check that the old account is absent from its bounded window, rather than assume restart clears it.

## Relationship

Relationship is a derived interpretation of social Experience rather than a world truth or action permission.

Target identity key:

```text
(observer PersistentIdentity, subject PersistentIdentity)
```

For a player, the trusted player UID maps to subject PersistentIdentity. The current EntityId remains provenance, not a durable key. Backend-owned RelationshipReflectionRecord cites pair-scoped social Experience evidence; background Relationship Reflection and a deterministic, bounded on-read RelationshipSummaryReader are implemented. Backend cognition resolves a trusted current Player pair and injects the same bounded read model into Step 0 and optional context-read Step 1. It does not expose persistent IDs, change current world state, or authorize actions. The 2026-09-25 local Ollama vertical slice is historical evidence. Subsequent cloud/hybrid checks do not certify a public multiplayer server or arbitrary multi-user deployment; the current demo remains local single-player. If exact historical facts are required, Recall supplies an EvidenceSet. CurrentAffect/persistent Mood is a possible Agent Runtime concept, not an implemented state subsystem; deterministic Gameplay Relationship State would belong to host game mechanics.

Relationship P1 maintains compact, subjective social understanding and lasting communication styles/boundaries, not a second store for drinks, activities, stories or concrete agreements. One new Episode plus the current PairState produces at most three independent ADD/CORRECT/CONFLICT updates in one model call. INTERPRETATION requires a nonempty Episode audit explanation but permits zero persistent updates; NO_CHANGE requires a null explanation and empty `state_updates`. Both no-delta outcomes preserve existing items and are durably processed, without projecting audit explanations into Cognition. Qualified social inference and gradual refinement from new support are allowed; rereading old state is not new evidence. Up to four current items are retained. Oversized batches are entirely unapplied and durably diagnosed; source, identity, CAS and transaction protections remain. Cognition receives the whole current snapshot as one budgeted component, never a rolling historical-interpretation window. Current Preview contracts are reflection/3.0, pair-state/2.0 and summary/3.0; initialization and restart persistence are supported on fresh current databases, without cross-Preview conversion, backfill or user-data resets.

## Scale

World Knowledge has its own Backend-owned complete player catalog, public/restricted access, constant/retrieval usage, hybrid RAG and Unity management UI. Its template and editable knowledge are separate from personal Memory, Relationship, current Perception and Unity authority. See [World Knowledge](../AIBackend/WORLD_KNOWLEDGE.md).

Foreground Step 0/1 share one ContextBudgetAllocator and effective model capacity.
Recall selects whole facts/excerpts rather than fixed character prefixes; independent
Semantic Facts may split, while causes, conditions, negation and time changes remain
self-contained. Memory and relevant Relationship use bounded candidates plus global
remaining capacity, rather than fixed 1800/800-token quotas. Relationship updates and
uncertain interpretations are retained together, without treating recency as proof.
New Main model profiles without an explicit capacity default to **32768 tokens (32K)** in [model_provider_presets.py](../AIBackend/app/model_provider_presets.py). This is a developer-selected request budget, not a model/service guarantee. Existing saved values are not silently migrated. [CognitiveBackendEntry](../AIBackend/app/cognitive_entry.py) falls back to 8192 when capacity is unknown and respects a smaller declared provider capacity. Output/thinking and estimation reserves still reduce the available input budget; 32K does not imply a 32K input payload.

The earlier 16K/thinking-disabled PASS WITH LIMITATIONS and the saved 8K profile on the 2026-10-13 acceptance machine are historical observations only. Small or explicit legacy capacities can still fail safely. Recall/entailment correctness is not guaranteed by admission or budgeting. Mandatory constant knowledge is included intact or yields an explicit constant-budget failure; it is never silently truncated.

The runtime does not keep every known player in every prompt.

```text
many historical player UIDs
→ durable indexed storage
→ filter current/relevant UID
→ bounded candidates
→ lexical/embedding retrieval when needed
→ bounded evidence
→ CognitiveContextSnapshot
```

The storage implementation can evolve without changing the identity and memory semantics above.

## Read-only experience export

The settings experience export reads retained current-player Ledger dialogue/Experience, Episodes, canonical memories and source links, and pair-scoped Relationship records through the [existing exporter](../AIBackend/app/experience_export.py). SQLite is read in one read-only transaction, including committed WAL data; knowledge and Unity settings use separately identified snapshots. Export does not consolidate, reflect, recall, mutate or reconstruct cleaned history. Categories/counts, exclusions and incomplete evidence are reported in manifests. Personal text is preserved for voluntary analysis, while credentials and foreign private records are excluded. See [architecture and ZIP layout](ARCHITECTURE.md#read-only-complete-experience-export).
