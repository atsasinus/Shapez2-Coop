# Shapez 2 Co-op

- Work in `mod/`. Before resuming, inspect the current source code, `git status`, `git diff`, recent commits and relevant tests. Treat the actual project state as the source of truth. Avoid repeating completed work or unnecessary research.
- References are read-only snapshots; do not pull or publish them automatically.
- Preserve original game files and user saves. Game assemblies are read-only build inputs. Test only with isolated data under `local/`; never commit game binaries, decompilations, saves, logs or machine/account details.
- Keep GPL-3.0 licensing and upstream attribution when reusing Shapez2Multiplayer. Preserve third-party notices. Do not push or use external accounts without authorization.
- Host owns world mutations, progress and shared saves. Clients submit intents; apply authoritative ordered results, with no optimistic world mutations. Never claim simulation determinism without testing it.
- Unity/game state is main-thread only. Network input is untrusted: bound messages, allocations, queues and player count; validate packet direction, phase, identity and compatibility.
- Joins/reconnects require a consistent snapshot boundary. Desync detection must cover actual simulation, not only building counts. Fail closed if recovery cannot establish agreement.
- Build/test tools and outputs stay in the workspace. Packaging must explicitly allowlist redistributable files, exclude game DLLs and include source/license information.
- Follow the applicable coop-anything research/audit/QA methodology. Bounded read-only subsystem agents are permitted; verify findings before changes. Give editing agents disjoint ownership.
- Commit coherent stages locally. Use Git history, code and test evidence to track progress. Do not maintain separate progress reports unless explicitly requested.
- Clearly distinguish compilation, protocol tests, in-engine tests and real multiplayer evidence. Never invent PASS results or claim unverified functionality.