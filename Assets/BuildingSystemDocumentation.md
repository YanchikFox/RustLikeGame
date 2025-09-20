# Building System Documentation

## Table of Contents
1. [System Overview](#system-overview)
2. [Architecture](#architecture)
3. [Core Components](#core-components)
4. [Construction Module](#construction-module)
5. [Snap System](#snap-system)
6. [Health and Damage System](#health-and-damage-system)
7. [Ground Detection System](#ground-detection-system)
8. [Area Detection](#area-detection)
9. [Building UI and Input](#building-ui-and-input)
10. [State Management](#state-management)
11. [Extension Points](#extension-points)
12. [Design Patterns](#design-patterns)
13. [Recommendations](#recommendations)

## System Overview

The building system allows players to place, connect, and interact with various construction elements in a 3D world. The system supports snapping, terrain conformity, stability calculations, tiered building materials, health, and damage mechanics.

Key features:
- Modular construction with snap points
- Tiered building progression (e.g., wood, stone, metal)
- Physics-based stability calculation
- Health and damage system with repair mechanics
- Ground detection for foundation placement
- Visual placement preview with valid/invalid indicators

## Architecture

The system uses a modular component-based architecture with the following main subsystems:

1. **Construction Core**: Base construction types and factory
2. **Placement System**: Logic for placing objects in the world
3. **Snap System**: Connection points and object alignment
4. **Health System**: Building health, damage, and repair
5. **Area Detection**: Floor detection and neighbor analysis
6. **Input Management**: State machine for build mode actions

The system is designed with Unity's component pattern and uses Zenject for dependency injection. Communication between components happens through direct references, component queries, and the state machine.

### Dependencies

- **Zenject**: Dependency injection framework used throughout the system
- **FSM**: State machine implementation for build states
- **Input System**: Custom input handling via `PlayerInput` class
- **Inventory System**: Resource management for building costs

## Core Components

### `Construction` (Abstract Class)
**Purpose**: Base class for all construction objects in the game.

**Serialized Fields**:
- None in the base class (all are properties or fields)

**Properties**:
- `Id`: Unique identifier for the construction
- `IdC`: Construction type identifier
- `objectInScene`: Reference to the GameObject instance
- `wasConnected`: Flag indicating if the object has been snapped
- `stability`: Stability value (0 to 1)
- `sourceFactory`: Reference to the factory that created this construction

**Public Methods**:
- `Add(GameObject gameObject, GameObject caller, ConstructionFactory factory)`: Instantiates the construction
- `Remove()`: Destroys the construction
- `Connect(RaycastHit hit)`: Attempts to connect to another construction via snap points
- `OnInstantiate()`: Called after instantiation (abstract)
- `Place(bool forceRecheck)`: Checks if placement is valid (abstract)
- `AfterPlace()`: Post-placement operations

### `ConstructionFactory` (ScriptableObject)
**Purpose**: Factory for creating different construction types.

**Serialized Fields**:
- `IdC`: Construction type identifier
- `Id`: Unique identifier
- `objectInScene`: Prefab for the construction
- `placementCost`: List of resources required to place the construction
- `appearances`: List of tier appearances for the construction

**Public Methods**:
- `CreateConstruction(int IdC)`: Creates a specific type of construction based on ID

### Derived Construction Classes

The system includes several construction types inheriting from `Construction`:
- `Base`: Foundation construction
- `Wall`: Vertical wall construction
- `Window`: Window construction
- `HalfWall`: Half-height wall construction
- `DoorFrame`: Door frame construction
- `Door`: Door construction
- `Roof`: Roof construction
- `Ceiling`: Ceiling construction

Each has specialized `Place()` and `OnInstantiate()` implementations.

## Construction Module

### `ConstructionHealth`
**Purpose**: Manages health, damage, repair, and tier progression for constructions.

**Serialized Fields**:
- `owner`: Reference to the parent Construction
- `neighbors`: List of connected construction objects
- `currentTier`: Current building tier (material type)
- `currentHealth`: Current health value

**Properties**:
- `maxHealth`: Maximum health based on current tier

**Public Methods**:
- `Initialize(Construction owner, BuildingTier startingTier)`: Set up initial state
- `TakeDamage(List<DamageInfo> damageInfos)`: Apply damage with modifiers
- `Repair(float amountToHeal, InventoryManager inventoryManager)`: Repair the structure
- `Upgrade(InventoryManager inventoryManager)`: Upgrade to the next tier
- `CanUpgrade()`: Check if upgrade is possible
- `CanRepair()`: Check if repair is possible
- `SetAsGroundedFoundation()`: Set as a ground-connected foundation (100% stability)
- `RecalculateStability()`: Update stability based on neighbors
- `RecalculateStabilityForNeighbors()`: Propagate stability changes to neighbors

### `BuildingTier` (ScriptableObject)
**Purpose**: Defines a building material tier (wood, stone, metal, etc.).

**Serialized Fields**:
- `tierName`: Name of the tier
- `nextTier`: Next tier in the upgrade path

### `TierAppearance` (Serializable)
**Purpose**: Defines appearance and properties for a specific building tier.

**Serialized Fields**:
- `tier`: Associated building tier
- `maxHealth`: Maximum health for this tier
- `damageModifiers`: List of damage modifiers for different damage types
- `upgradeCost`: Resources needed to upgrade to this tier
- `fullRepairCost`: Resources needed to fully repair this tier
- `mesh`: Mesh for this tier
- `materials`: Materials for this tier

### `DamageSystem`
**Purpose**: Defines damage types and modifiers.

**Types**:
- `DamageType`: Enum for damage categories (Melee, Bullet, Explosion, Generic)
- `DamageModifier`: Serializable class for damage type modifiers
- `DamageInfo`: Struct for damage amount and type

## Snap System

### `SnapPoint` (Serializable)
**Purpose**: Defines a connection point on a construction object.

**Serialized Fields**:
- `id`: Identifier for the snap point
- `localPosition`: Position relative to the parent
- `localEulerAngles`: Rotation relative to the parent
- `allowedNeighborTags`: Tags of objects that can connect to this point

**Properties**:
- `LocalRotation`: Quaternion rotation from Euler angles

### `SnapPointHolder`
**Purpose**: Container for snap points on a construction object.

**Serialized Fields**:
- `snapPoints`: Array of SnapPoint objects

**Unity Methods**:
- `Awake()`: Initialize snap points
- `OnValidate()`: Update snap points in editor
- `OnDrawGizmosSelected()`: Draw snap point visualizations in editor

**Private Methods**:
- `UpdateSnapPoints()`: Update snap point positions based on collider

### `UniversalConnector`
**Purpose**: Handles snapping logic between construction objects.

**Public Methods**:
- `Connect(RaycastHit hit)`: Attempt to connect to another object

**Private Methods**:
- `FindSnapCandidates()`: Find potential snap connections
- `FindBestCandidate()`: Choose best snap option
- `ApplySnapTransform()`: Position and rotate object for snap
- `RegisterNeighbors()`: Add neighbor relationships after successful snap
- Various helper methods for snap compatibility calculation

## Health and Damage System

### `ConstructionHealth`
**Purpose**: Manages health, damage, tier progression, and stability.

**Serialized Fields**:
- See Construction Module section

**Unity Methods**:
- `OnDestroy()`: Notify neighbors when destroyed

## Ground Detection System

### `GroundIntersectionChecker`
**Purpose**: Verifies that foundations are properly placed on terrain.

**Serialized Fields**:
- `targetObject`: Object to check for ground intersection
- `requiredPercentage`: How much of the base must be on ground (0-1)
- `density`: Resolution of check points
- `surfaceOffset`: Small offset to prevent floating point issues
- `checkInterval`: Performance throttling for checks

**Public Methods**:
- `IsGroundedEnough(bool forceRecheck)`: Check if object is sufficiently grounded
- `PerformCheck()`: Do the actual grounding check
- `IsSurfaceAngleValid()`: Check if the ground slope is acceptable

### `ColliderUtils`
**Purpose**: Helper methods for working with colliders.

**Public Methods**:
- `GetColliderBounds(GameObject, out Vector3, out Vector3)`: Get local bounds for a collider

## Area Detection

### `AreaDetection`
**Purpose**: Detects and categorizes objects within a base's influence.

**Serialized Fields**:
- None (all are constants or properties)

**Properties**:
- `DetectedObjects`: Array of objects in the area
- `ConnectedBases`: Array of connected base objects
- `Floors`: List of floor data

**Public Methods**:
- `InitializeFloors(int count)`: Set up floor tracking
- `GetObjectBounds()`: Calculate bounds of the area
- `FindObjectsInArea()`: Detect objects within the area
- `SortObjectsByY()`: Sort objects by height
- `UpdateFloors()`: Update floor states
- `CreateFoundationPlaces()`: Set up foundation attachment points

### `FloorData`
**Purpose**: Tracks the state of a floor within a base.

**Properties**:
- `WallStates`: Dictionary of wall presence by direction
- `HasCeiling`: Whether the floor has a ceiling
- `Objects`: Dictionary of objects on this floor

**Public Methods**:
- `Reset()`: Clear floor data

## Building UI and Input

### `BuildingTool`
**Purpose**: Player interaction tool for building-related actions.

**Serialized Fields**:
- `buildableLayer`: Layer mask for buildable objects
- `highlightColor`: Color for highlighting objects
- `highlightIntensity`: Intensity of highlight
- `repairAmountPerTick`: Health restored per repair action
- `repairTickRate`: Time between repair actions
- `infoPanel`: UI panel for building info
- `stabilityText`: Text for stability display
- `healthText`: Text for health display
- `healthBar`: Health bar image

**Unity Methods**:
- `Start()`: Set up initial state
- `Update()`: Handle interaction logic

**Private Methods**:
- `HandleHammerInteraction()`: Process hammer tool actions
- `HandleRepair()`: Handle repair actions
- `ApplyHighlight()`: Highlight looked-at object
- `RemoveHighlight()`: Remove highlighting
- `UpdateInfoPanel()`: Update UI with object info
- `ClearHighlight()`: Clear all highlighting

### `GhostModeController`
**Purpose**: Manages preview visualization during placement.

**Serialized Fields**:
- `ghostMaterialValid`: Material for valid placement
- `ghostMaterialInvalid`: Material for invalid placement

**Public Methods**:
- `EnableGhostMode()`: Activate ghost preview mode
- `DisableGhostMode()`: Deactivate ghost preview mode
- `SetPlacementValid(bool)`: Update visuals based on validity

## State Management

### `BuildSM` (State Machine)
**Purpose**: Manages building mode states.

**Fields**:
- `startState`: Start building state
- `stopState`: Stop building state
- `performState`: Perform building action state
- `notInBuildState`: Not in building mode state
- `placementMachine`: Reference to placer
- `constructionSelector`: Reference to selector

**Unity Methods**:
- `Awake()`: Initialize states
- `GetInitialState()`: Return initial state

### Building States
- `NotInBuilding`: Default state when not building
- `Start`: Starting build process
- `Perform`: Actively placing object
- `Stop`: Finishing placement

### `Placer`
**Purpose**: Handles object placement logic.

**Fields**:
- `maxDistance`: Maximum placement distance
- `minDistance`: Minimum placement distance
- `scrollSensitivity`: Scroll wheel sensitivity
- `distanceFromPlayer`: Current distance
- `constructionSelector`: Construction selector reference
- `currentConstruction`: Current construction being placed

**Public Methods**:
- `StartPlacement()`: Begin placement mode
- `StopPlacement()`: End placement mode
- `PerformPlacement()`: Update placement preview

### `ConstructionSelector`
**Purpose**: Manages selection of construction types.

**Serialized Fields**:
- `selectedIdC`: Currently selected construction type
- `selectedObject`: Currently selected construction factory

**Public Methods**:
- `SelectObjectWithIdC(int)`: Select construction by ID

## Extension Points

### Terrain Deformation
- `GroundIntersectionChecker`: Add terrain modification logic in `PerformCheck()`
- `Base.Place()`: Add terrain deformation when placing foundations

### Multiplayer Synchronization
- `Construction.Add()`, `Construction.Remove()`: Add network instantiation/destruction
- `ConstructionHealth`: Add RPC calls for damage, repair, and upgrade
- `UniversalConnector.Connect()`: Add network sync for snapping

### AI Navigation
- `AreaDetection`: Add navmesh updating in `UpdateFloors()`
- `Construction.AfterPlace()`: Trigger navmesh rebuild

### Stability Mechanics
- `ConstructionHealth.RecalculateStability()`: Enhance with physics-based calculations
- Add structural analysis for complex buildings

### Resource Management
- `InventoryManager.ConsumeItems()`: Hook for resource economy events
- `BuildingTier`: Add resource production/processing capabilities

## Design Patterns

1. **Factory Pattern**: `ConstructionFactory` creates different types of constructions.

2. **State Pattern**: `BuildSM` and its states manage building mode transitions.

3. **Strategy Pattern**: Different construction types implement different placement strategies.

4. **Observer Pattern**: Stability recalculation notifies neighbors of changes.

5. **Dependency Injection**: Zenject is used throughout for dependency management.

6. **Component Pattern**: Unity's component system is leveraged for modular functionality.

7. **Template Method**: Base `Construction` class defines template methods implemented by subclasses.

## Recommendations

### Modularity

1. **Interface Segregation**:
   - Create separate interfaces for damage, repair, and stability systems
   - Example: `IDamageable`, `IRepairable`, `IStable`

2. **Event-Based Communication**:
   - Implement an event system for state changes
   - Replace direct references with event subscriptions

3. **Configuration Scriptable Objects**:
   - Move hardcoded values to configuration assets
   - Create a central settings manager

### Networking Readiness

1. **Authority Model**:
   - Clearly define client/server responsibilities
   - Add validation logic for server authority

2. **State Synchronization**:
   - Add serialization for all construction state
   - Implement snapshot and delta compression

3. **Prediction & Reconciliation**:
   - Add client-side prediction for placement
   - Implement server reconciliation

### Performance

1. **Object Pooling**:
   - Pool construction objects instead of instantiating
   - Implement a construction object pool manager

2. **Spatial Partitioning**:
   - Add chunk-based storage for construction objects
   - Implement distance-based loading/unloading

3. **LOD System**:
   - Add LOD for construction visuals
   - Simplify collision when at a distance

### Scalability

1. **Chunk-Based Building**:
   - Divide world into building chunks
   - Load/unload buildings by distance

2. **Building Limits**:
   - Implement per-player or per-area building limits
   - Add performance monitoring

3. **Building Templates**:
   - Allow saving/loading building templates
   - Add blueprint system for faster building
