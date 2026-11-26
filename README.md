# Utopia Tools

> ### ⚠️ Downloaded the "Source code" zip? It will NOT work.
> This repository is **source code** — it has to be compiled before the game can load it.
> If you just want to use the mod, grab the release archive:
>
> **[UtopiaTools.zip](https://github.com/yeu-maker/UtopiaTools/releases/latest/download/UtopiaTools.zip)** ·
> [all releases](https://github.com/yeu-maker/UtopiaTools/releases/latest)
>
> You will know you got the wrong file if the folder is named `UtopiaTools-main` and contains
> `ControlPanel/ libs/ LiveTuner/ MenuMod/ README.md` instead of `Mods/*.dll`.

A quality-of-life / sandbox menu mod for **My Dystopian Robot Girlfriend 0.97.12** (PC).

## Features

- **F8 in-game menu**: money, time, scene/state switching, expressions, item access
- **F9 slider panel**: arm/head pose, model scale & position, expression fine-tuning (2 pages)
- **Custom stream chat**: replaces the game's generated chat lines with the player's own text file
- **Custom dialogue** (optional, off by default): player-written lines, **limited to the room by default**
  (`say_scope.txt`), so it does not overwrite personality-module sex dialogue from other mods
- **Item spawning** through the game's own cheat workbench
- Config files are plain text and are auto-created on first launch
- CN/EN interface

## Requirements

- Windows PC, game version 0.97.12
- [MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3)
- ASCII-only game path (MelonLoader limitation)

## Install (players)

1. Install MelonLoader 0.7.3 into the game folder
2. Download the release archive (`UtopiaTools.zip`), open it and copy the **three DLLs**
   from its `Mods` folder into `<GameDir>/Mods/`
3. Launch the game, load a save, press `F8` / `F9`

The result must look like this — the DLLs sit **directly** in `Mods`, no subfolder:

```
<GameDir>/
  My Dystopian Robot Girlfriend.exe
  Mods/
    MenuMod.dll
    ControlPanel.dll
    LiveTuner.dll
  MelonLoader/
    Latest.log          <- search for [MENU] to verify
```

Verify from the log (`<GameDir>/MelonLoader/Latest.log`):

```
[MENU] loaded v3.1 — F8 menu, lang=EN, say scope gate ON
[MENU] ready
[MENU] say patched: Say(String,Boolean,Boolean,Boolean,Boolean,Action)
```

If those lines are missing, MelonLoader did not load the DLLs (wrong folder, wrong game
path, or MelonLoader not installed).

## Dialogue replacement scope (`say_scope.txt`)

`say.txt` is applied only while the game is in one of the listed states. The default is
`room`, which means: the apartment room and in-room interactions. Everything else — sex
scenes, shower, cuddle, changing room, going out, PC, cooking, fishing, menus — keeps its
vanilla lines. That is what keeps other mods' personality-module dialogue intact.

```
room        room + in-room interactions            (default)
room+       also cuddle / changing room
room+story  also story dialogue
all         every state (old 0.1 behaviour)
```

You can also write state names directly, comma separated (`RoomState,InteractState`), or
switch presets in game: `F8 -> More -> Dialogue replace`.

The scope is checked against the real runtime class of `GameScript.GameState`. Note that
`GetType().Name` is useless here: Il2CppInterop wraps returned objects with their
*declared* type, so we read the class name through `il2cpp_object_get_class` instead.

Log lines to look for in `MelonLoader/Latest.log` (prefix `[MENU]`):

```
state probe @5s: MainMenuState                 state lookup works
say scope = InteractState,RoomState (say_scope.txt)
SAY state=RoomState -> replace
SAY state=NewFrickState -> skip                expected, vanilla kept
```

If the log says `SAY: cannot read GameScript.GameState`, replacement disables itself
instead of overwriting unknown lines.

## Build from source

1. Copy the DLLs listed in `libs/PUT_DLLS_HERE.md` into `libs/`
2. Build each project:

```
dotnet build -c Release MenuMod/MenuMod.csproj
dotnet build -c Release ControlPanel/ControlPanel.csproj
dotnet build -c Release LiveTuner/LiveTuner.csproj
```

3. Copy the produced DLLs into `<GameDir>/Mods/`

## Design notes

- `ControlPanel` draws its own sliders with IMGUI because the game's build strips
  `GUILayout` / `GUI.HorizontalSlider` internals.
- `LiveTuner` applies pose values every frame from `pose.txt` with easing; the game's
  own animations otherwise overwrite them.
- Chat/dialogue replacement is implemented with Harmony patches and is therefore more
  version-sensitive than a pure-Lua mod.
- Dialogue replacement is state-gated rather than character-gated: the game renders every
  line through a single `SayDialog.Say`, so the only cheap discriminator is the current
  game state.
- The model transform is used for "zoom" effects instead of the camera: moving the
  camera in this game causes black frames.

## Known limitations

- PC only, no Android support
- Panel is custom-drawn and may overlap the game UI on some resolutions (position is configurable)
- Dialogue replacement is limited to the room by default; set `say_scope.txt` to `all` for
  the old global behaviour
- If a future game version renames the state classes, the scope gate stops replacing
  (fails safe) until the mod is updated

## Changelog

See [CHANGELOG.md](CHANGELOG.md). Latest: **0.1.1** — dialogue replacement no longer
overwrites personality-module sex dialogue.

## License

MIT
