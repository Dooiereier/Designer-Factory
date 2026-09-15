# Designer Factory

A Harmony-based Unity mod for [Juno: New Origins](https://junonewhorizons.com/) that replaces the vehicle
Designer's flat background with a walkable 3D hangar.

## Features

- **Procedural hangar** built around whatever craft is currently loaded (floor, walls, roof, trusses,
  catwalks, stairs, a cafeteria annex) - resizes automatically to fit the craft.
- **Walkable first-person camera** for exploring the hangar on foot, independent of the Designer's own
  orbit camera.
- **In-hangar structure editor** for placing, moving, rotating, scaling, and tinting props and stock
  Planet Studio structures directly in the hangar (forklifts, cranes, furniture, vehicles, ...), with
  save/load to disk.
- **Wandering NPCs ("Droods")** that roam the hangar and cafeteria, avoid the craft/structures/each other,
  and occasionally stop to have a conversation.
- **Drivable cars** that loop along a path defined by waypoints placed through the structure editor.

## Controls

| Key | Action |
|---|---|
| `F1` | Toggle the walkable first-person camera |
| `F2` | Toggle the structure editor panel |
| `F3` | Manually refresh/rebuild the hangar |

While the structure editor panel is open: `G`/`R`/`S` grab/rotate/scale the selected structure.

## Building

This project targets Jundroo's **ModTools** framework for Juno: New Origins / Simple Rockets 2. To open
and build it:

1. Install Juno: New Origins and its ModTools SDK (via the in-game mod tools installer).
2. Open this project in the Unity version ModTools expects.
3. `Assets/ModTools/` is **not** included in this repository - it's the game's own SDK (including its
   compiled assemblies) and gets installed per-project by Jundroo's own tooling, not something this repo
   redistributes. Unity will report missing references until it's in place.
4. Build the mod via the ModTools build window (`Tools > Mod Builder` or equivalent) to produce the
   `.sr2-mod` package.

## Project layout

- `Assets/Scripts/` - the mod's own C# source (hangar generation, walk camera, structure editor, Droods,
  cars, Harmony patches).
- `Assets/Editor/` - editor-only tooling for importing custom props/characters into prefabs.
- `Assets/Models/` - imported meshes, textures, and animations for props and characters.
- `Assets/ModData.asset` - the mod's manifest (name, author, bundled prefabs, ...).
