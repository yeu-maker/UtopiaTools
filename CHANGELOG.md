# Changelog

## 0.1.1

**Fixed**

- Dialogue replacement (`say.txt`) no longer overwrites other mods' personality-module
  dialogue during sex scenes. It is now gated by the current game state
  (`GameScript.GameState`) and limited to the room by default (`say_scope.txt`).
  Root cause: the Harmony prefix on `SayDialog.Say(string, …)` replaced the text on every
  call whenever replacement was ON, with no notion of where the line came from.
- `LiveTuner` printed a hardcoded path (`D:\MDRG\emotes_dump.txt`) instead of the real game
  path in its log output.

**Added**

- `say_scope.txt` with presets `room` (default) / `room+` / `room+story` / `all`, or explicit
  comma-separated state names. Also switchable in game: `F8 -> More -> Dialogue replace`.
- `state probe @5s / @20s` startup log lines, and one `SAY state=<name> -> replace|skip` line
  per state transition while replacement is ON, so dialogue-replacement problems are
  diagnosable from `MelonLoader/Latest.log`.
- The dialogue-replace menu page now shows the live game state and the active scope.

**Internals**

- The current state's real class name is read through
  `Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class` + `il2cpp_class_get_name`.
  `object.GetType().Name` is not usable here: Il2CppInterop wraps the value with the
  property's *declared* type (`GameState`), so the derived state (`RoomState`,
  `NewFrickState`, …) is invisible.
- `say.txt` line picking now reuses a single `Random` instance instead of creating one per
  call (two calls in the same tick could otherwise return the same line).

## 0.1

- Initial release: F8 menu, F9 slider panel, custom stream chat, custom dialogue,
  item spawning through the game's cheat workbench, CN/EN interface.
