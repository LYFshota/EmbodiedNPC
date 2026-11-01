# Early Prototype release scope

> Current preview: [v0.1.4-preview](https://github.com/LYFshota/EmbodiedNPC/releases/tag/v0.1.4-preview). Dated/versioned sections below retain their original validation scope; earlier NOT_RUN or capacity limits are not a statement of current implementation. Current module behavior is documented in [Architecture](ARCHITECTURE.md).

The public source keeps TitleScene → CafeDemoV2, the café, four NPCs and Xingmang,
AIBackend, Contracts and project dependencies. Private Git history, InternalDocs,
development WIP, model weights, caches, UserData and credentials are excluded.

## Licensing record

- Xingmang's independent project-scoped non-commercial license is adopted; the VRM commercial flag is Disallow. Original code remains Apache-2.0.
- The owner confirmed that P1_CoffeeCanister, P1_TreeCrown and RoundedSurface were made by the project without third-party source material and can be licensed under CC0. They join the scoped project-art schedule.
- The existing Vivi/GirlA/桜田史利矢 CC0 records and Ao's specific Hub page and embedded redistribution/modification permissions are retained. Ao's exact upstream file-revision linkage is not asserted beyond existing evidence.
- fangzhangmnm/Wacky contributions retain their CC BY 4.0 attribution. VamTastic's contribution is explicitly labelled CC BY with its version unspecified; no version is invented. Existing source and modification credits are retained.
- Historical platform labels are not treated as proof of a particular additional contract. No actual incompatible obligation has been identified in the retained records; this is not a claim that all hypothetical third-party rights cease to apply.
- Unity template components retain the official template/Unity Companion License notice. Restored Portable files come from the fixed committed baseline, not local WIP.

## Validation boundaries

The prior private source cold-start check was PARTIAL on raw first import and PASS
for the minimum startup chain after six-resource Reimport. This is not a clean-machine
or full-gameplay certification. Each Windows preview is described only by the build/startup and gameplay/export checks actually recorded for its own release; historical incomplete checks are not silently promoted to passes.

The Windows preview bundles the eight pinned EmbeddingGemma-300M Q4 ONNX runtime files, with the Gemma Agreement, Prohibited Use Policy, Notice and conversion/source record. Source Git remains weight-free. Built-in Embedding is the first-start default; external Embedding and disabled-vector options remain available. The Main LLM is not bundled and must be configured by the user.

## v0.1.0-preview checks

The cleaned candidate produced a new Windows x64 Player (Unity 6000.5.10f1, zero build errors). The bundled Python launcher started its Backend and title screen, and the first-time model settings UI was opened; built-in Embedding was selected by default. The bundled model loaded and produced a valid 768-dimensional vector in one targeted check. The owner elected to perform subsequent cafe/gameplay testing; no completed cafe walkthrough or clean-machine certification is claimed for this release. Existing targeted animation/cold-start evidence is retained, not repeated.


## v0.1.2-preview source scope

The final owner-accepted Animation P0 state is integrated: quiet standing
loops, naturally hanging relaxed arms/hands, an arms-only player Wave layer
and a restrained non-waving arm. Vivi's skirt-specific idle and the customers'
seated base/foot IK remain in place. Existing dialogue, task/world outcomes
and long-term memory semantics are retained.

That release used application/first-party runtime assembly version
0.1.2-preview (numeric assembly/file version 0.1.2.0). Backend OpenAPI version
0.3.0 remains the API version, not the application release version.
The build entry point reads the committed application version.

The owner completed visual playtesting of the final R4 posture package.
Version-specific fresh build, test, extracted-ZIP startup and cleanup evidence
is reported on the v0.1.2-preview Release page. It does not imply testing every
model provider, a fresh physical Windows machine, or every possible gameplay path.
The release retains the complete pinned EmbeddingGemma-300M Q4 ONNX resources
and license bundle; a Main LLM is still configured by the player.


## v0.1.3-preview

Runtime source is synchronized from private development main 7a47164ebd43633ca04174318ef9e5fcffc68b52 through the existing public allowlist. Public license/attribution, sanitized resources, README and build entry points are retained. Original Mixamo FBX, OlyPoly, Microsoft YaHei, hidden standee, private history/data and development evidence remain excluded.

Includes the complete player knowledge catalog, 108 defaults, public/restricted access, constant/retrieval usage, incremental embeddings, hybrid two-step cognition, ten drinks, dialogue presentation and rare emotion icons, plus player-scoped read-only experience ZIP export. Exported authored text can contain personal information; sharing is voluntary. API credentials and model weights are excluded from experience exports.

Application and first-party assemblies use 0.1.3-preview / 0.1.3.0. The Backend API version remains independent. Release-specific Windows build/startup/export/restart evidence and limitations are listed on the v0.1.3-preview Release page; no full regression or clean physical machine certification is implied.


## v0.1.4-preview

Release baseline is the public v0.1.3-preview tag. The current public default-branch documentation and the existing resource/license cleanup are retained. The release integrates private-main concurrency, retained-dialogue GoalOutcome linking, unified reply/history segmentation, 48 additional role-private backgrounds (156 defaults total), and developer concurrency controls. No private history, WIP, player data or internal reports are included.

Cloud model defaults are 6/6/2 and Ollama defaults to 1/1/1. Config changes use existing profile save/activation and restart. Background work uses bounded lanes, state/scope checks and drained native operations; it remains a single-process design.

Related Backend coverage: 514 tests, 496 passing, 8 assertion failures and 10 errors. The matched previous public tag ran 450 tests with 28 non-passing cases; every candidate non-passing test ID also fails on that baseline. This is not a green full-suite claim. New concurrency, dialogue-linked outcome and everyday-knowledge cases pass. Prior measured real DeepSeek concurrency evidence is reused rather than rerunning paid load tests. Release-specific Unity, Windows package and extracted-startup results are provided with the Release validation notes.

Existing player knowledge catalogs are not automatically overwritten or extended. Back up/export custom knowledge before explicitly restoring defaults to adopt the 156-entry catalog. Historical outcome association requires the original source dialogue to remain within the existing recent window and does not prove current item possession or location.
