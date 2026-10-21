# NPC reply language, D production projection v1 (2026-10-18)

`reply_language` is trusted transport presentation metadata (`zh-CN` or `en-US`),
not a model output or permission. Legacy clients default explicitly to `zh-CN`.
Unity snapshots it at request composition; the frozen backend request carries it
through both steps. Global preference lives in Unity's existing settings store,
separate from Provider configuration. Session overrides are owned by each NPC
composer and keyed by conversation plus player/NPC identities.
Missing reliable durable identities use the global default without storing a temporary override.

`default_personas.v1.json` reuses the reviewed LanguageStudy-20261018 English
projections. `source_sha256` covers **all** original Character fields, sorted JSON
keys, compact separators, UTF-8 with unescaped Unicode. `projection_sha256` covers
the English view with the same algorithm. Any edit to an original/default/custom
field disables the default projection. The original assets and DTO field caps
remain authoritative. To update a default, review translation semantics, update
both hashes together, increment the projection version, and run the four-default
hash/override tests. Do not regenerate translations automatically at runtime.

`behavior.v1.json` contains only reviewed model-visible prose fragments, not a
second core Prompt. Continuation uses a shared English-equivalent final review.
Projection is applied inside the budget renderer, so the Provider's actual wire
payload (including expanded English text, schema and final language contract) is
what is measured. Historical speech, Memory, knowledge and perception are unchanged.

Length counts **Unicode scalar values**, including spaces/punctuation; combining
marks count separately. Invalid isolated surrogates fail validation. Soft/hard
limits are Chinese 100/150 and English 200/250. `reply_language.response_schema`
changes only utterance limits; backend Pydantic validation receives the same run
locale. Unity's correlated request/report gate enforces it again. Replies within
the hard maximum remain verbatim. Overlong replies fail normally: no truncation,
rewriting or automatic retry is implemented.

Temporary language recognition deliberately accepts standalone directives only,
e.g. `这句话用中文回答`, `接下来用中文交流`, `Please reply in English`,
`From now on, speak Chinese.` Ordinary words such as OK/yes/好的 do not change
locale. Arbitrary prose, quoted instructions, mixed clauses and negation are not
classified; use the preference dropdown or a standalone directive. No inferred
language request is written to persistent settings.

Validation artifacts and limitations: `D:\EmbodiedNPC-LanguageProduction-20261018`.
The opt-in Editor tests require that isolated harness; they must never be pointed
at a player's database or a running production backend. A successful language
test does not prove semantic factuality or final package readiness.
