# Progress

- Current code: host-ordered edits, compatibility handshake, bounded reliable snapshots, fixed-step simulation and production-state digest prototypes. Main integration is in local commit `c4607d2`; no push.
- Verified: normal build against installed 1.2.0-rc3 succeeds (406 warnings, mostly inherited nullable warnings); protocol console passes 608 assertions.
- In-engine gate passed on two isolated Unity processes over loopback IP: snapshot matched at step 326, digests matched at steps 400/500, both ran 200+ steps after joining, both exited cleanly; no exceptions in those logs. Evidence: ignored `local/qa/run-20261020-184821/`. This was a fresh world without a production fixture, not proof of general determinism or internet play.
- Fixed during QA: missing Harmony class annotation, batchmode keyboard/layout, snapshot loading blocked by menu animation, replica save guard across world disposal, copied Unity single-instance setting, Windows sharing mode for log reader.
- Original game and user saves remain untouched; QA patches only full copies under `local/qa` and uses separate profiles. QA bot is excluded from normal builds.
- Remaining: real building/blueprint/configuration/research tests, active production, stale/concurrent intents, reconnect/recovery, full mod compatibility, Steam/WAN and 3–4 players, large-factory performance. Undo/redo remains disabled.
- Packaging not ready: collect exact ENet/K4os notices and add an explicit redistribution allowlist. UI library remains an external dependency.

Next: add a reproducible production fixture and client place/delete actions to the two-process gate, fix any divergence, then save the next coherent stage locally.
