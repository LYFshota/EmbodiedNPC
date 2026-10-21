# EmbodiedNPC Memory

> **Status note (2026-10-18):** This page describes the canonical evidence and recall model. The latest integrated demo also supports bounded third-party hearing as observer-local historical Observation; that does **not** automatically create long-term hearing-derived claims. Source and validation: [hearing report](ARCHITECTURE.md); current delivery and release status: [CURRENT_STATUS](RELEASE_STATUS.md).

EmbodiedNPC Memory is **observer-local, evidence-first, and provenance-aware**.

Memory is not the current world state. Character Persona v2 Lite is a stable, Unity-authored/user-editable profile; its Backstory/Personality/Boundaries are not player semantic facts or evidence of shared experiences. Exact player history comes from actual Dialogue/Memory evidence, not the character card.

M3 uses canonical `memory_records`, `memory_subjects`, `memory_evidence`,
`memory_semantic_facts`, `memory_derived_insights`, source links, and `memory_embeddings`.
Schema 16 adds open Semantic Fact, 17 adds Maintenance, and 18 adds multi-Episode
DerivedInsight. Existing Schema 16/17 semantic data upgrades without a reset.
Historical Profile definitions remain in migrations, but production does not read or
write `PROFILE_FACT`. Incompatible populated pre-semantic databases and future schemas
are rejected explicitly rather than silently converted. Migration 15 and versions 1–14
remain historical definitions.

## M3 acceptance (2026-10-13)

Open Semantic Fact, independent Episodic Memory, low-frequency Memory Maintenance,
cross-Episode DerivedInsight, async Embedding / Vector-first Recall, independent
Relationship Reflection, and Observation sampling debounce are implemented.
M3 is **PASS within the single-player, four-NPC CafeDemoV2 acceptance scope**, including
restart persistence and real Unity action outcomes. This is not final project release
acceptance. Free-text semantic accuracy depends on the model; bounded Maintenance
candidates cannot guarantee all historical relationships are found. Multiplayer
servers, large NPC populations, and cross-platform deployment remain unvalidated.

## Memory v2 Lite single-player increment (2026-10-10)

This historical increment used fixed ProfileFacts; M3 has replaced that production contract with open Semantic Facts and low-frequency semantic Maintenance. Its authority boundaries remain: the Demo binds one Player UID (default `cafe-local-player-001`), and checks request identity and linked Recall evidence, including open-subject reads. Displayed NPC speech can form owner-only self-statements with source limitations; it cannot certify world execution, another actor, or shared Player history. Skill/Goal outcomes use trusted Runtime participant bindings. Repetition does not independently verify a claim. NPC-only narration and autonomous continuation do not independently produce Player Relationship reflection.

Historical real acceptance and current delivery checks are separated in [the validation summary](RELEASE_STATUS.md).

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

The current canonical recall path is bounded and on-demand, but it is **not** a general Tool Loop:

```text
CognitiveRun
→ Backend Step 0
→ optional MEMORY_RECALL
→ validated canonical retrieval
→ bounded EvidenceSet
→ Backend Step 1
→ terminal CognitiveDecision
```

At most one recall step is allowed. A second recall is rejected rather than iterated. Normal turns do not retrieve the entire memory store.

Current world/perception always has priority over historical evidence.

Recall distinguishes `SEMANTIC_FACT`, `EPISODIC` and tentative `DERIVED_INSIGHT`.
Current excludes superseded facts and Insights with stale source revisions; All History
retains historical labels. Conflicts are unresolved, not a unique truth. Insight is not
proof of a world action or action permission. All types share the existing async
Embedding index and safe fallback when vectors are pending or unavailable.

## Relationship

Relationship is a derived interpretation of social Experience rather than a world truth or action permission.

Target identity key:

```text
(observer PersistentIdentity, subject PersistentIdentity)
```

For a player, the trusted player UID maps to subject PersistentIdentity. The current EntityId remains provenance, not a durable key. Backend-owned RelationshipReflectionRecord cites pair-scoped social Experience evidence; background Relationship Reflection and a deterministic, bounded on-read RelationshipSummaryReader are implemented. Backend cognition resolves a trusted current Player pair and injects the same bounded read model into Step 0 and optional Recall Step 1. It does not expose persistent IDs, change current world state, or authorize actions. The single-player local CafeDemo vertical slice was validated with Runtime v2 and real Ollama qwen3.5:4b on 2026-09-25; remote and multiplayer behavior remains unvalidated. If exact historical facts are required, Recall supplies an EvidenceSet. CurrentAffect/persistent Mood is a possible Agent Runtime concept, not an implemented state subsystem; deterministic Gameplay Relationship State would belong to host game mechanics.

## Scale

World Knowledge K0/K1/K1.5 is implemented: Backend-owned structured knowledge,
independent Hybrid RAG using the existing Embedding Provider, and a protected local
Unity editor. It is developer reference, separate from personal Memory, Relationship,
current Perception and Unity world authority. See [World Knowledge](../AIBackend/WORLD_KNOWLEDGE.md).

Foreground Step 0/1 share one ContextBudgetAllocator and effective model capacity.
Recall selects whole facts/excerpts rather than fixed character prefixes; independent
Semantic Facts may split, while causes, conditions, negation and time changes remain
self-contained. Memory and relevant Relationship use bounded candidates plus global
remaining capacity, rather than fixed 1800/800-token quotas. Relationship updates and
uncertain interpretations are retained together, without treating recency as proof.
New v2 cloud profiles default to a developer-selected 16K budget, not a service capacity guarantee; saved values are unchanged. The verified 16K/thinking-disabled cloud path is PASS WITH LIMITATIONS; the formal
saved 8K profile on the 2026-10-13 acceptance machine (historical snapshot, not a universal active setting). Small/unknown capacities retain safe bounds, and model
Recall/entailment decisions are not guaranteed by deterministic admission or budgeting.

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
