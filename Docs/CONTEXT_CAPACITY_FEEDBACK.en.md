[简体中文](CONTEXT_CAPACITY_FEEDBACK.md) | [English](CONTEXT_CAPACITY_FEEDBACK.en.md)

# Model context capacity and feedback

New EmbodiedNPC Main model profiles default to a **32K (32768) context budget** and **1024 output tokens**. These are local request settings; choose a window supported by the actual provider and deployed model. Existing saved 8K, 16K and custom values stay unchanged.

## Adjust in game

Open **Settings → AI Configuration → Advanced settings**, then choose 8K, 16K, 32K, 64K or a custom capacity. Save and Apply, and restart Backend when prompted.

Ollama uses the configured `num_ctx`. Cloud requests use the declared setting when budgeting their final payload.

## Budget allocation

`ContextBudgetAllocator` measures system instructions, recent conversation, world knowledge, memory, relationships, tasks, structured-output schema, output reserves and safety margin.

Optional context is selected using relevance and priority while keeping important evidence groups intact. A 32K window is **not 32K tokens of free input text**. With the current default 1024 output reserve and 3072 estimated safety reserve, the 32K configuration gives a calculated maximum of 28672 input tokens.

## Feedback

- **WARNING:** Cognition succeeds, but important recent dialogue, memory or knowledge has been excluded because of the budget.
- **ERROR:** Required input cannot fit, or the provider reports a capacity or output constraint.

The UI ties warnings and errors to their original source. After a configuration change, `POST /v1/context-budget/recheck` can re-evaluate signed, sanitized cost metadata without making another model inference.

Warnings may be dismissed in settings; errors remain until the originating issue is resolved. Diagnostics store capacity metadata rather than full player prompts.

## Technical reference

`context_diagnostics_version=1` opts Unity into `context_status`. An ordinary cognitive run has at most two budget checks: Step 0 and optional Step 1.

[Architecture](ARCHITECTURE.en.md) · [Backend](../AIBackend/README.en.md) · [Source Setup](SOURCE_SETUP.en.md)
