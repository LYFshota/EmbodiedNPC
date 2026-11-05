[简体中文](CONTRIBUTING.md) | [English](CONTRIBUTING.en.md)

# Contributing to EmbodiedNPC

Issues, Pull Requests, documentation improvements, tests, and new features are welcome. EmbodiedNPC aims to be an open game-AI-NPC architecture that others can adapt and extend.

## Report issues and ideas

Describe the behavior you want or the problem you observed. For bugs, include reproduction steps, OS versions, screenshots and sanitized logs. Keep API keys, player saves, personal dialogue and private data on your device.

## Contribute code

1. Create a branch from the latest `main` and keep changes focused.
2. Prefer existing event, cognition, action and memory components over parallel frameworks.
3. Add focused tests for affected behavior and describe what you verified.
4. Open a PR with the design, changed files, usage and any relevant limitations.

For architecture or protocol changes, opening an Issue first can help coordinate contracts and migrations.

## Design conventions

- Unity owns live world state and action outcomes; Backend handles cognition and bounded action proposals.
- Memory stores sourced experience; Relationship maintains pair-scoped social interpretation.
- TaskService relies on identity, revisions, idempotent operations and world receipts.
- Ambient behavior, dialogue presentation and formal tasks coordinate through separate responsibilities.

## Licenses and assets

Contributions to first-party source are handled under [Apache-2.0](LICENSE). Third-party assets and Xingmang have their own [licenses](THIRD_PARTY_NOTICES.en.md). Submit material you are permitted to contribute and retain required credits.

See [Architecture](Docs/ARCHITECTURE.en.md) · [Developer Docs](Docs/README.en.md) · [Source Setup](Docs/SOURCE_SETUP.en.md).
