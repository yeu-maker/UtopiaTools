# libs

No third-party binaries are committed here.

To build, copy these files from your game installation into this folder:

From `<GameDir>/MelonLoader/net6/`:
- MelonLoader.dll
- Il2CppInterop.Runtime.dll
- 0Harmony.dll

From `<GameDir>/MelonLoader/Il2CppAssemblies/` (generated on first game launch):
- Il2Cppmscorlib.dll
- UnityEngine.CoreModule.dll
- UnityEngine.IMGUIModule.dll
- UnityEngine.InputLegacyModule.dll

They are required only at compile time (`<Private>false</Private>`), they are not redistributed.
