# Validation gates

| Gate | Evidence / status |
|---|---|
| Installed assembly compatibility | Release builds against 1.2.0-rc3 pass; inherited nullable warnings remain. |
| Protocol | 608 console assertions pass: frame truncation/fuzzing, 4 participants, concurrent sequencing, replay, gaps, snapshot metadata/hash/quota/expiry. |
| Runtime loading | Two isolated real Unity 6000.3.19f1 clients load the mod; critical Harmony hooks checked. Clean exits and no exceptions in the successful run. |
| Host + client | Loopback IP gate passed on a fresh world: snapshot equality at step 326; shared digests at 400/500; 200+ steps after join on both. `local/qa/run-20261020-184821/` (ignored local evidence). No production/action fixture yet. |
| Simulation equality | Fixed synchronous 20 Hz, zero worker threads, no camera LOD; checksum includes island/building state, configuration, trains/cargo, global signals and research. Serializer order may still cause false mismatches. Not established as deterministic. |
| Network and performance | WAN, Steam relay, loss/delay, 3–4 clients and large factories not run. |

Protocol command (workspace SDK): `local/tools/dotnet/dotnet.exe run --project mod/tests/ProtocolTests -c Release`.

Do not equate protocol tests with in-engine tests: runtime command adapter currently implements stricter observed-revision rejection independently of the tested general sequencer.

Required next gameplay cases: mod load and clean shutdown; snapshot join with equal state; identical 200+ steps; same-tile simultaneous placement; stale delete after replacement; blueprint placement/deletion; research purchase and operator goals; constant signal edit; multiple joins; disconnect during snapshot; reconnect; forced state corruption detected; snapshot recovery; 4 participants; bounded throughput and stable memory on a large factory.

QA copies live only in ignored `local/qa`. GameProbe inventories persistent-path calls and patches **only copies under local/qa** to redirect `Application.persistentDataPath`, disable managed PlayerPrefs and use the game's StartInMainMenu mode. It removes the copied boot.config single-instance setting. `SHAPEZ2_COOP_QA_PROFILE` selects a separate profile per process; it must be inside `local/qa`. Original game DLLs are read-only. The QA bot is excluded from distribution. A successful scorer must require explicit results in both client logs, matching session/step evidence, no faults, and clean exits; a timeout/missing result is never PASS.
