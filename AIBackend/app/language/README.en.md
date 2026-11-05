[简体中文](README.md) | [English](README.en.md)

# NPC reply language and bilingual projection

Reply language is specified by trusted `reply_language` metadata: `zh-CN` or `en-US`. Unity captures it when composing a cognitive request. Backend Step 0, optional Step 1 and final presentation follow the same frozen language contract.

## Language selection

- The global setting is stored in Unity player preferences, independently of model provider settings.
- A single NPC conversation may have an identity-scoped language override.
- Reply language is request metadata rather than a free model-selected value.

## Default persona and prompt projections

`default_personas.v1.json` contains reviewed English projections for the built-in characters. Source and projection hashes define when an authored English view is valid. Changes to source character fields or local overrides use the relevant updated profile rather than a stale translation.

`behavior.v1.json` contains reviewed language-specific behavior text. The budget renderer expands translated fragments and measures the final payload including schema and response limits.

## Response length

| Locale | Soft limit | Hard limit |
| --- | ---: | ---: |
| `zh-CN` | 100 | 150 |
| `en-US` | 200 | 250 |

Length is measured in Unicode scalar values. Backend and Unity validate the same request locale and correlation. Dialogue continuation follows the same language rules.

## Updating English projections

When changing built-in character projections, review translation semantics and update `source_sha256`, `projection_sha256` and the projection version together. Run the related default-persona and override tests.

See [Backend](../../README.en.md), [Wire Contracts](../../../Contracts/README.en.md), and [Architecture](../../../Docs/ARCHITECTURE.en.md).
