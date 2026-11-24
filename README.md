# Utopia Tools

A quality-of-life / sandbox menu mod for **My Dystopian Robot Girlfriend 0.97.12** (PC).

## Features

- **F8 in-game menu**: money, time, scene/state switching, expressions, item access
- **F9 slider panel**: arm/head pose, model scale & position, expression fine-tuning (2 pages)
- **Custom stream chat**: replaces the game's generated chat lines with the player's own text file
- **Custom dialogue** (optional, off by default): player-written lines
- **Item spawning** through the game's own cheat workbench
- Config files are plain text and are auto-created on first launch
- CN/EN interface

## Requirements

- Windows PC, game version 0.97.12
- [MelonLoader 0.7.3](https://github.com/LavaGang/MelonLoader/releases/tag/v0.7.3)
- ASCII-only game path (MelonLoader limitation)

## Install (players)

1. Install MelonLoader 0.7.3 into the game folder
2. Copy the `Mods` folder from the release archive into the game root
3. Launch the game, load a save, press `F8` / `F9`

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
- The model transform is used for "zoom" effects instead of the camera: moving the
  camera in this game causes black frames.

## Known limitations

- PC only, no Android support
- Panel is custom-drawn and may overlap the game UI on some resolutions (position is configurable)
- Dialogue replacement affects all characters when enabled, so it ships off by default

## License

MIT
