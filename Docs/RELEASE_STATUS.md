# Early Prototype release scope

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
or full-gameplay certification. The new Windows preview will be described only by
the minimum build/startup checks actually completed for this release.

The Windows preview bundles the eight pinned EmbeddingGemma-300M Q4 ONNX runtime files, with the Gemma Agreement, Prohibited Use Policy, Notice and conversion/source record. Source Git remains weight-free. Built-in Embedding is the first-start default; external Embedding and disabled-vector options remain available. The Main LLM is not bundled and must be configured by the user.

## v0.1.0-preview checks

The cleaned candidate produced a new Windows x64 Player (Unity 6000.5.10f1, zero build errors). The bundled Python launcher started its Backend and title screen, and the first-time model settings UI was opened; built-in Embedding was selected by default. The bundled model loaded and produced a valid 768-dimensional vector in one targeted check. The owner elected to perform subsequent cafe/gameplay testing; no completed cafe walkthrough or clean-machine certification is claimed for this release. Existing targeted animation/cold-start evidence is retained, not repeated.
