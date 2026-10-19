# Upstream and dependencies

This derivative of [Bknibb/Shapez2Multiplayer](https://github.com/Bknibb/Shapez2Multiplayer) is distributed under GPL-3.0; see LICENSE. Base commit: `5ab336eb31018eefbc2cfbae90e6229f455c9e96` (v1.1.9). Original author: Bknibb. Changes are maintained in this repository's Git history.

Architecture references: [shapez2-shifter](https://github.com/tobspr-games/shapez2-shifter), MIT, inspected at `8a9fa01472c8554d77a3b8d0c0974a1867445f04`; [coop-anything](https://github.com/cranebeaker/coop-anything), MIT. No Shifter implementation is copied into this mod.

Runtime dependencies: HarmonyX 2.16.1 (LGPL-2.1), K4os.Compression.LZ4 1.3.8 and K4os.Hash.xxHash 1.0.8 (MIT), ENet-CSharp and native ENet (MIT, binaries retained from upstream), System.Runtime.CompilerServices.Unsafe 6.0.0 (MIT). Exact dependency graph is recorded in packages.lock.json. Redistributed dependency licenses accompany packages.

Shapez2UILib and Steamworks Platform Resolver are external Workshop dependencies. Shapez2UILib's public source is used locally for compilation only; no license was found in its repository, so its source and DLL are not redistributed here. Game and Unity assemblies are read-only references and are never redistributed.

Local builds require your own installed copy of Shapez 2. Source corresponding to each packaged mod is included alongside its binaries.
