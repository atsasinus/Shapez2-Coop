# Progress

## Current state
- Initial research: upstream Shapez2Multiplayer includes Steam/ENet, building/blueprint actions, research and snapshot joins; explicitly lacks belt/pipe/logic state replication.
- Installed target observed: Steam Windows Mono, game 1.2.0-rc3, build 24668948. No .NET SDK found in standard locations (runtimes only).
- Upstream license GPL-3.0; Shifter/playbook MIT. No game/save modifications.

## Stages
1. Reproducible isolated build, pinned provenance, compatibility inspection.
2. Fix host authority, ingress validation, concurrent actions and snapshot lifecycle; regression harness.
3. Simulation synchronization with desync detection/recovery; in-engine automated gates.
4. Installable package, installation instructions, multiplayer/performance validation.

## Checks / limitations
- Source inspected; no build or gameplay test yet.
- Existing mod cannot currently be described as fully synchronized. Full playthrough and large-factory performance unknown.

## Next priority
Establish local SDK/tooling and compile upstream against installed assemblies; inspect game simulation/save boundaries before choosing synchronization implementation.
