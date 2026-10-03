# Design basis before implementation

Goal: reuse the correct rendered YMM4 frame across valid interactive operation sequences; an optional cache must not make the renderer less correct.

Requirements: interactive demand wins over speculation; reuse survives A/B/undo when content matches; edits change only dependent identities; preview geometry and export semantics stay explicit; persistence requires stable render provenance; budgets include transient and borrowed resources; failures revert to the host renderer.

Invariants: complete identity; certified dependency snapshot; ready pixels rather than decode placeholders; separate immutable content identity from request/publication epoch; atomic purge admission; cancellation must not poison other consumers; exact output interpretation; immutable borrows survive eviction; bounded pending jobs/bytes; owning-context GPU use; no external callbacks or blocking I/O under coordination locks; restart validates identity and integrity; selective invalidation includes temporal dependencies; unknown contracts bypass.

Architecture derived from requirements: host adapter certifies snapshots and render readiness -> frame request (exact time + output specification + dependency digest + renderer provenance) -> demand/speculation coordinator with publication lease -> bounded immutable RAM/disk store and context-owned GPU borrows -> advisory residency UI. Pixels have content identity; edit/session/purge tokens govern requests, never substitute for content identity. CPU storage and GPU rendering do not share an execution lock.

Competing designs to evaluate: (A) global revision-keyed cache (simple but loses selective/A-B reuse); (B) certified content-addressed completed frames with explicit publication transactions (preferred if host boundary is provable); (C) full effect DAG cache and MFR (requires dependency/thread-safety contracts absent from the public host integration); (D) trust only observed pixels (cannot prove future validity). Choose correctness, robustness, simplicity, then speed.

Unproven host facts remain audit findings, not permissions to invent APIs or enable MFR.
