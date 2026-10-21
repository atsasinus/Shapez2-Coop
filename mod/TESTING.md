# Validation gates

| Gate | Evidence / status |
|---|---|
| Installed assembly compatibility | Release builds against 1.2.0-rc3 pass; inherited nullable warnings remain. |
| Protocol | 608 console assertions pass: frame truncation/fuzzing, 4 participants, concurrent sequencing, replay, gaps, snapshot metadata/hash/quota/expiry. |
| Runtime loading | Two isolated real Unity 6000.3.19f1 clients load the mod; critical Harmony hooks checked. Clean exits and no exceptions in the successful run. |
| Host + client | Two-process loopback gate passed with a real shape producer: snapshot matched at step 349; shared digests at 400/500; both ran 200+ steps after joining and exited with code 0. Evidence: ignored `local/qa/run-20261021-131045/`. |
| Client world actions | Ordinary belt placement/deletion, replacement, stale deletion rejection and no optimistic mutation passed through the actual Harmony hook and network adapter. Five accepted revisions; stale intent preserved its replacement. |
| Production | Both cleared the producer output at step 470 and refilled it at step 471. Previous QA setup omitted the simulation barrier, leaving the host producer absent from the simulation graph; fixed by finalizing pending calculations before setup. This tests one producer, not a complete factory. |
| Four participants + reconnect | Host and three real clients passed the loopback gate in `local/qa/run-20261021-132257/`: three serialized initial snapshot admissions, one writer executing five revisions, producer clear/refill on all replicas, primary client rejoined while two observers remained, two shared digests on every client after rejoin, four exit codes 0. This does not test four concurrent writers or WAN. |
| Simulation equality | Fixed synchronous 20 Hz, zero worker threads, no camera LOD; checksum includes island/building state, configuration, trains/cargo, global signals and research. Serializer order may still cause false mismatches. Not established as deterministic. |
| Network and performance | Four local processes tested; WAN, Steam relay, loss/delay and large factories not run. |

Protocol command (workspace SDK): `local/tools/dotnet/dotnet.exe run --project mod/tests/ProtocolTests -c Release`.

Do not equate protocol tests with in-engine tests: runtime command adapter currently implements stricter observed-revision rejection independently of the tested general sequencer.

Required next gameplay cases: same-tile simultaneous placement; blueprint placement/deletion; research purchase and operator goals; constant signal edit; disconnect during snapshot; forced state corruption detected; snapshot recovery; bounded throughput and stable memory on a large factory. Unity reports GraphicsBuffer/JobTempAlloc cleanup warnings during QA shutdown; their origin and performance impact are not yet isolated.

Build QA with `mod/tools/build-qa.ps1 -GameDirectory '<installed-game>'`, prepare the host/client profiles with `mod/tools/prepare-qa.ps1` (also `-ProfileName client-profile`), then run `mod/tools/run-qa.ps1 -PeerCount 3` for four processes. The runner prepares observer profiles, validates every participant and shuts down only processes it created.

QA copies live only in ignored `local/qa`. GameProbe inventories persistent-path calls and patches **only copies under local/qa** to redirect `Application.persistentDataPath`, disable managed PlayerPrefs and use the game's StartInMainMenu mode. It removes the copied boot.config single-instance setting. `SHAPEZ2_COOP_QA_PROFILE` selects a separate profile per process; it must be inside `local/qa`. Original game DLLs are read-only. The QA bot is excluded from distribution. A successful scorer must require explicit results in both client logs, matching session/step evidence, no faults, and clean exits; a timeout/missing result is never PASS.
