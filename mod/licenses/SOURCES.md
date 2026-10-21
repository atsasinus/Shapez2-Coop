# Copyright notice sources

Notices are retained verbatim. NuGet integrity values and transitive build dependencies are in `../src/packages.lock.json`; redistributed binaries are listed separately in `../DEPENDENCIES.json`.

- ENet-CSharp MIT: `nxrighthere/ENet-CSharp`, commit `5cb7b630e59540ee4e453c63eaed15a6217613cd`, `LICENSE`.
- Native ENet MIT: copyright/permission block at the beginning of `Source/Native/enet.h` in the same commit. This identifies the source of the notice, not a verified native binary revision. The native binary is retained from the pinned upstream mod and identified by SHA256 in `DEPENDENCIES.json`.
- K4os LZ4 MIT: `MiloszKrajewski/K4os.Compression.LZ4`, tag `1.3.8`, commit `f5a25b7d72e2e41550fe20662597169ff11c3b60`, `LICENSE`.
- Original LZ4 BSD: `orig/lib/LICENSE` in that same commit. GPL-licensed original command-line programs are not redistributed.
- K4os xxHash MIT: `MiloszKrajewski/K4os.Hash.xxHash`, tag `1.0.8`, commit `c36ef54edbd778c082cab04df7e42cb72a22d887`, `LICENSE`.
- Original xxHash BSD: first comment block of `orig/xxhash.h` in that same commit.
- .NET runtime notices: unchanged `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` from the `System.Runtime.CompilerServices.Unsafe` 6.0.0 NuGet package.

The UI library, Steamworks resolver, Harmony and game/Unity DLLs are external runtime dependencies and are not bundled. The derivative mod's GPL text is `../LICENSE`.
