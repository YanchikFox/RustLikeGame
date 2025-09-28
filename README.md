# RustLikeGame

RustLikeGame is an experimental Unity 6 prototype inspired by survival sandboxes such as Rust. The repository collects purpose-built modules for procedural voxel terrain generation, a modular building system with snap points, a FSM-driven construction flow, and an inventory UI wired through Zenject dependency injection.

## Table of Contents
- [Project Overview](#project-overview)
- [Key Features](#key-features)
- [Requirements](#requirements)
- [Getting Started](#getting-started)
- [Project Structure](#project-structure)
- [Subsystems](#subsystems)
  - [Terrain System](#terrain-system)
  - [Finite State Machines](#finite-state-machines)
  - [Building System](#building-system)
  - [Inventory System](#inventory-system)
- [Additional Components](#additional-components)
- [Extensibility Tips](#extensibility-tips)
- [License](#license)

## Project Overview

The prototype targets desktop platforms and ships with preconfigured Zenject installers, a modular first-person controller, and UI scaffolding. It is meant to serve as a starting point for experimenting with survival mechanics, world streaming, and construction gameplay loops.

## Key Features

- **Hybrid terrain generation:** CPU, GPU, or hybrid voxel pipelines with LOD blending, frustum culling, and optional raycast-based culling.
- **Modular construction flow:** Ghost previews, snap points, stability propagation, and tier progression for placed structures.
- **FSM-driven gameplay states:** Dedicated construction state machine decoupled from player input.
- **Resource-aware inventory:** Stackable slots, hotbar selection, and resource cost validation shared across tools and building logic.
- **Zenject-powered architecture:** Installers configure all services for terrain, construction, UI, and player systems.

## Requirements

- **Unity Editor:** 6000.2.5f1.
- **Unity packages:** TextMesh Pro, Burst, Collections, Jobs (plus other default modules).
- **Third-party:** Zenject (via UPM).
- **Target platform:** PC (Standalone).

## Getting Started

1. Clone the repository and open the `RustLikeGame` folder with Unity 6000.2.5f1.
2. Load the `Assets/Scenes/MainScene.unity` scene.
3. Ensure the following Zenject installers are present in the scene hierarchy:
   - `TerrainSystemInstaller`
   - `ComputeBufferManagerInstaller`
   - `ConstructionInstaller`
   - `PlayerInstaller`
4. Enter Play mode. Default controls:
   - **Movement:** WASD + Mouse look
   - **Inventory:** `E`
   - **Build Menu:** `B`
   - **Placement distance:** Mouse wheel
   - **Interactions:** Left/Right mouse buttons with equipped tools

## Project Structure

```
Assets/
├─ Installers/                # Zenject installers
├─ Scenes/                    # Main scene(s)
├─ Scripts/
│  ├─ TerrainSystem/          # Voxel terrain generation & streaming
│  ├─ Building System/        # Construction logic and runtime objects
│  ├─ FSM 1.0/                # Finite state machine core and states
│  ├─ InventorySystem/        # Inventory logic and UI prefabs
│  └─ ModularFirstPersonController/  # FPS controller modules
└─ Resources, Materials, etc.
```

## Subsystems

### Terrain System

- **TerrainManager** orchestrates chunk generation and streaming, controls CPU/GPU/hybrid modes, manages generation budgets, LOD transitions, and culling (frustum with optional ray checks).
- **TerrainSettingsAsset** implements `ITerrainSettings` and stores chunk dimensions, render distance, LOD thresholds, and other tweakable parameters.
- **BiomeDefinition** scriptable assets feed the `GenerateVoxelDataJob` with biome thresholds, noise parameters, height layers, caves, and domain warps.
- **Generation pipeline:**
  - `GenerateVoxelDataJob` (Burst) produces density fields on the CPU.
  - `VoxelTerrainGPU.compute` handles GPU density generation.
  - `MarchingCubesMeshGenerator` (Burst) converts densities into meshes using gradients.
  - `ComputeBufferManager` (`IComputeBufferPool`) reuses GPU buffers to avoid frequent allocations.
- **Player integration:** `PlayerInteraction` submits terrain modification requests (dig/place) through the manager so neighbors are updated consistently.

### Finite State Machines

- **StateMachine** component runs `Enter/Exit/Update/LateUpdate` for the active state and draws a debug GUI.
- **BaseState** and **StateWithChangeTracking** provide convenient abstractions, with the latter guarding against multiple transitions per frame.
- **BuildSM** divides the construction lifecycle into:
  - `NotInBuilding` – idle while no buildable item is selected.
  - `Start` – initializes placement and activates the ghost preview.
  - `Perform` – updates preview meshes, handles collisions/snap points, awaits confirmation.
  - `Stop` – finalizes placement or cancellation and cleans up.
- The FSM relies on `ConstructionSelector` and `Placer` for concrete actions.

### Building System

- **Data & factories:**
  - `ConstructionFactory` scriptable objects describe building types (IDs, prefabs, costs, appearance variants).
  - `Construction` base class represents runtime structures; derived classes (`Base`, `Wall`, `Door`, etc.) implement placement logic.
  - `ConstructionInstaller` registers factories with Zenject.
- **Placement flow:**
  - `ConstructionSelector` chooses the active factory via UI input.
  - `Placer` controls ghost previews, placement distance, snapping (`UniversalConnector`, `SnapPointHolder`, `SnapPoint`), collision checks, and resource validation through `InventoryManager`.
  - `GhostModeController` toggles materials and colliders for valid/invalid previews.
- **Combat & stability:**
  - `ConstructionHealth` tracks the current `BuildingTier`, hit points, and neighboring elements, responding to damage (`DamageInfo`) and repair/upgrade costs (`TierAppearance`).
  - Foundations marked with `SetAsGroundedFoundation` propagate stability and trigger re-evaluation when destroyed.
- **Tools & UI integration:**
  - `BuildingTool` manages hammer interactions: highlighting via `MaterialPropertyBlock`, damage, repairs, upgrades, and info panels (TextMesh Pro + Image widgets).
  - `InputManager` and `PlayerInput` centralize user input for combat, repairs, and hotbar selection.
  - `UIManager` opens/closes inventory and building canvases while locking the FPS camera when UI is focused.

### Inventory System

- **InventoryManager** controls an array of `InventorySlot` instances, handling add/remove operations, stacking, hotbar selection, and verifying/deducting `ResourceCost` values used by construction and tools.
- **InventorySlot** bridges logic with UI elements, supports drag-and-drop through `IDropHandler`.
- **InventoryItem** MonoBehaviour renders individual items, tracks stack counts, and updates labels.
- **Item** scriptable objects define type (block/tool/resource), tool parameters, sprites, damage definitions, and resource categories.
- **Integration:** Shared `InventoryManager` references are injected into `Placer`, `BuildingTool`, and other subsystems so resource checks stay consistent.
- **Hotbar shortcuts:** Number keys `1–9` swap the active slot; the mouse wheel moves placement previews without consuming items.

## Additional Components

- **ModularFirstPersonController** supplies a configurable first-person movement stack registered through `PlayerInstaller`.
- **UI Prefabs** live under `Assets/Scripts/InventorySystem/Prefabs`, providing HUD and inventory visuals.
- **Resources & Materials** deliver base assets for voxel terrain and construction pieces.

## Extensibility Tips

- Adjust world generation by editing `TerrainSettingsAsset` instances instead of modifying code.
- Add new building pieces by creating `ConstructionFactory` assets and derived `Construction` subclasses.
- Extend the construction FSM by inheriting from `BuildingState` or `StateWithChangeTracking` for new states.
- Define new items through `Item` scriptable objects and reference them in inventories or cost lists.

## License

No explicit license is included. Add one before distributing the project publicly.

