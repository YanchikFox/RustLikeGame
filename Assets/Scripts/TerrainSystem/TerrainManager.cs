using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;
using Zenject;

namespace TerrainSystem
{
    public enum TerrainProcessingMode
    {
        CPU,
        GPU,
        Hybrid
    }

    public class TerrainManager : MonoBehaviour
    {
        #region Inspector Properties
        [Header("Terrain Settings")]
        [SerializeField] private Vector3 chunkWorldSize = new Vector3(16, 16, 16);
        
        // WARNING: Halving voxelSize will now 8x the number of voxels per chunk,
        // which drastically increases memory usage and processing time.
        // Adjust loadDistance accordingly.
        [SerializeField] private float voxelSize = 1f;
        // Track previous values to detect changes in the Inspector
        private float previousVoxelSize;
        private Vector3 previousChunkWorldSize;
        
        [SerializeField] private Transform playerTransform;
        [SerializeField] private float loadDistance = 64f;

        [Header("LOD Settings")]
        [SerializeField] private bool useLOD = true;
        [SerializeField] private float[] lodDistanceThresholds = new float[] { 32f, 64f, 96f }; // Distance thresholds for each LOD level
        [SerializeField] private int maxLODLevel = 2; // Maximum LOD level (0 = highest detail)
        [SerializeField] private bool transitionLODs = true; // Whether to create transition chunks between LOD levels

        // This property now calculates the number of voxels per chunk dynamically
        public Vector3Int ChunkVoxelDimensions => new Vector3Int(
            Mathf.CeilToInt(chunkWorldSize.x / voxelSize),
            Mathf.CeilToInt(chunkWorldSize.y / voxelSize),
            Mathf.CeilToInt(chunkWorldSize.z / voxelSize)
        );

        [Header("Generation Settings")]
        [SerializeField] private GameObject chunkPrefab;
        [SerializeField] private MarchingCubesMeshGenerator meshGenerator;
        [SerializeField] private ComputeShader voxelTerrainShader;
        [SerializeField] private TerrainProcessingMode processingMode = TerrainProcessingMode.Hybrid;

        [Header("Biome Settings")]
        [SerializeField] private float biomeNoiseScale = 0.001f;
        [SerializeField] private float biomeBlendRange = 0.05f; // How smooth the transition is
        [SerializeField] private List<BiomeDefinition> biomeDefinitions = new List<BiomeDefinition>();

        [Header("Advanced Biome Noise")]
        [SerializeField] private float temperatureNoiseScale = 0.002f;
        [SerializeField] private float humidityNoiseScale = 0.003f;
        [SerializeField] private float riverNoiseScale = 0.008f;
        [SerializeField] private float riverThreshold = 0.02f;
        [SerializeField] private float riverDepth = 5.0f;

        [Header("Performance")]
        [SerializeField] private int maxChunksPerFrame = 4;
        [SerializeField] private int verticalLoadRadius = 1;
        [SerializeField, Tooltip("Maximum number of GPU readback requests allowed to run simultaneously.")]
        private int maxConcurrentGpuReadbacks = 4;
        [SerializeField, Tooltip("Maximum number of chunks that can start GPU density generation in a single frame (0 = unlimited).")]
        private int maxGpuGenerationsPerFrame = 2;

        [Header("Culling Settings")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool enableFrustumCulling = true;
        [SerializeField, Tooltip("Extra padding (in voxels) applied to each chunk's bounds before frustum tests.")]
        private float frustumCullingBoundsPaddingVoxels = 1f;
        [SerializeField, Tooltip("Minimum number of frames to wait before re-testing visibility for hidden chunks.")]
        private int hiddenChunkVisibilityCooldownFrames = 5;
        [SerializeField] private bool useOcclusionRayTest = false;
        [SerializeField] private LayerMask occlusionLayerMask = Physics.DefaultRaycastLayers;
        [SerializeField] private float occlusionRayPadding = 0.25f;

        [Header("Adaptive Generation")]
        [SerializeField] private bool adaptiveChunkBudget = true;
        [SerializeField] private int minChunksPerFrame = 1;
        [SerializeField] private int maxAdaptiveChunksPerFrame = 12;
        [SerializeField] private float targetFrameRate = 60f;
        [SerializeField] private float frameRateBuffer = 5f;
        [SerializeField] private float adaptationInterval = 0.5f;
        [Range(0.01f, 1f)]
        [SerializeField] private float frameTimeSmoothing = 0.1f;

        [Header("Debug Settings")]
        [SerializeField] private bool isRegenerating = false;
        [SerializeField] private bool showLODLevels = false;
        [SerializeField, Tooltip("When enabled, logs per-frame profiling information for dirty chunk processing.")]
        private bool logChunkQueueProfiling = false;
        [SerializeField, Tooltip("Enables verbose diagnostics and structured logging for profiling sessions.")]
        private bool enableDiagnostics = false;
        #endregion

        #region Private Fields
        private ILogger TerrainLogger => Debug.unityLogger;
        private readonly object logLock = new object();
        private StreamWriter logWriter;
        private string logFilePath;
        private string lastLogFilePath;
        private bool logFileInitialized;
        private bool logFileAnnounced;

        /// <summary>
        /// The most recent terrain log file created for this manager.
        /// </summary>
        public string LogFilePath => lastLogFilePath;

        private struct MeshJobHandleData
        {
            public JobHandle handle;
            public NativeList<Vector3> vertices;
            public NativeList<int> triangles;
            public NativeList<Vector3> normals;
            public NativeArray<float> densities;
            public NativeArray<float> gradientDensities;
        }

        private struct VoxelGenJobHandleData
        {
            public JobHandle handle;
            public NativeArray<float> densities;
        }

        private struct GpuGenerationRequestInfo
        {
            public int LodLevel;
            public Vector3Int VoxelDimensions;
            public int ThreadGroupsX;
            public int ThreadGroupsY;
            public int ThreadGroupsZ;
            public int VoxelCount;
            public int BufferCount;
        }

        private struct MeshGpuReadbackRequest
        {
            public AsyncGPUReadbackRequest VertexRequest;
            public AsyncGPUReadbackRequest NormalRequest;
            public AsyncGPUReadbackRequest CounterRequest;
            public ComputeBuffer DensityBuffer;
            public ComputeBuffer GradientBuffer;
            public ComputeBuffer VertexBuffer;
            public ComputeBuffer NormalBuffer;
            public ComputeBuffer CounterBuffer;
            public int LodLevel;
            public Vector3Int VoxelDimensions;
            public Vector3Int ThreadGroups;
            public int VertexCapacity;
            public long CubeCount;
        }

        private struct ModificationGpuReadbackRequest
        {
            public AsyncGPUReadbackRequest Request;
            public ComputeBuffer DensityBuffer;
            public int ExpectedVersion;
            public int LodLevel;
            public Vector3Int VoxelDimensions;
        }

        private struct DensitySummary
        {
            public int SampleCount;
            public int ValidSamples;
            public int InvalidSamples;
            public float Min;
            public float Max;
            public float Average;

            public bool HasSamples => SampleCount > 0;
            public bool HasValidSamples => ValidSamples > 0;
            public bool HasInvalidSamples => InvalidSamples > 0;
        }

        private struct ChunkData
        {
            public TerrainChunk chunk;
            public int lodLevel;
        }

        private struct TransitionKey : IEquatable<TransitionKey>
        {
            public Vector3Int HighDetail;
            public Vector3Int LowDetail;

            public TransitionKey(Vector3Int highDetail, Vector3Int lowDetail)
            {
                HighDetail = highDetail;
                LowDetail = lowDetail;
            }

            public bool Equals(TransitionKey other) => HighDetail == other.HighDetail && LowDetail == other.LowDetail;

            public override bool Equals(object obj) => obj is TransitionKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (HighDetail.GetHashCode() * 397) ^ LowDetail.GetHashCode();
                }
            }

            public bool Involves(Vector3Int chunkPos) => HighDetail == chunkPos || LowDetail == chunkPos;
        }

        private class TransitionMeshData
        {
            public MeshFilter MeshFilter;
            public MeshRenderer MeshRenderer;
            public Mesh Mesh;
            public Vector3Int Direction;
            public int HighLod;
            public int LowLod;
            public bool Dirty;
            public float SkirtDepth;
        }
        private ComputeBuffer biomeThresholdsBuffer;
        private ComputeBuffer biomeGroundLevelsBuffer;
        private ComputeBuffer biomeHeightImpactsBuffer;
        private ComputeBuffer biomeHeightScalesBuffer;
        private ComputeBuffer biomeCaveImpactsBuffer;
        private ComputeBuffer biomeCaveScalesBuffer;
        private ComputeBuffer biomeOctavesBuffer;
        private ComputeBuffer biomeLacunarityBuffer;
        private ComputeBuffer biomePersistenceBuffer;
        private ComputeBuffer biomeHeightWarpEnabledBuffer;
        private ComputeBuffer biomeHeightWarpStrengthsBuffer;
        private ComputeBuffer biomeHeightWarpScalesBuffer;
        private ComputeBuffer biomeHeightWarpRidgedBuffer;
        private ComputeBuffer biomeSecondaryHeightWarpEnabledBuffer;
        private ComputeBuffer biomeSecondaryHeightWarpStrengthsBuffer;
        private ComputeBuffer biomeSecondaryHeightWarpScalesBuffer;
        private ComputeBuffer biomeSecondaryHeightWarpRidgedBuffer;
        private ComputeBuffer biomeCaveWarpEnabledBuffer;
        private ComputeBuffer biomeCaveWarpStrengthsBuffer;
        private ComputeBuffer biomeCaveWarpScalesBuffer;
        private ComputeBuffer biomeCaveWarpRidgedBuffer;
        private ComputeBuffer biomeSecondaryCaveWarpEnabledBuffer;
        private ComputeBuffer biomeSecondaryCaveWarpStrengthsBuffer;
        private ComputeBuffer biomeSecondaryCaveWarpScalesBuffer;
        private ComputeBuffer biomeSecondaryCaveWarpRidgedBuffer;
        private ComputeBuffer biomeExtraHeightLayerEnabledBuffer;
        private ComputeBuffer biomeExtraHeightOctavesBuffer;
        private ComputeBuffer biomeExtraHeightScalesBuffer;
        private ComputeBuffer biomeExtraHeightLacunarityBuffer;
        private ComputeBuffer biomeExtraHeightPersistenceBuffer;
        private ComputeBuffer biomeExtraHeightImpactBuffer;
        private ComputeBuffer biomeExtraCaveLayerEnabledBuffer;
        private ComputeBuffer biomeExtraCaveOctavesBuffer;
        private ComputeBuffer biomeExtraCaveScalesBuffer;
        private ComputeBuffer biomeExtraCaveLacunarityBuffer;
        private ComputeBuffer biomeExtraCavePersistenceBuffer;
        private ComputeBuffer biomeExtraCaveImpactBuffer;
        private ComputeBuffer biomeCliffLayerEnabledBuffer;
        private ComputeBuffer biomeCliffScalesBuffer;
        private ComputeBuffer biomeCliffImpactsBuffer;
        private ComputeBuffer biomeCliffThresholdsBuffer;
        private ComputeBuffer biomeCliffSharpnessBuffer;
        private ComputeBuffer biomePlateauLayerEnabledBuffer;
        private ComputeBuffer biomePlateauScalesBuffer;
        private ComputeBuffer biomePlateauStepHeightsBuffer;
        private ComputeBuffer biomePlateauStrengthsBuffer;
        private ComputeBuffer biomePlateauThresholdsBuffer;
        private ComputeBuffer biomePlateauSharpnessBuffer;
        private ComputeBuffer biomeSurfaceCaveMaskEnabledBuffer;
        private ComputeBuffer biomeSurfaceCaveMaskScalesBuffer;
        private ComputeBuffer biomeSurfaceCaveMaskThresholdsBuffer;
        private ComputeBuffer biomeSurfaceCaveMaskStrengthsBuffer;
        private ComputeBuffer biomeSurfaceCaveMaskFalloffsBuffer;
        private ComputeBuffer triangleTableBuffer;
        private ComputeBuffer edgeConnectionsBuffer;
        private bool gpuBuffersInitialized;
        private bool needsGpuBufferInitRetry;

        private readonly List<BiomeDefinition> activeBiomeDefinitions = new List<BiomeDefinition>();
        private BiomeSettings[] runtimeBiomeSettings = Array.Empty<BiomeSettings>();

        private NativeArray<float> cachedBiomeThresholds;
        private NativeArray<float> cachedBiomeGroundLevels;
        private NativeArray<float> cachedBiomeHeightImpacts;
        private NativeArray<float> cachedBiomeHeightScales;
        private NativeArray<float> cachedBiomeCaveImpacts;
        private NativeArray<float> cachedBiomeCaveScales;
        private NativeArray<int> cachedBiomeOctaves;
        private NativeArray<float> cachedBiomeLacunarity;
        private NativeArray<float> cachedBiomePersistence;
        private NativeArray<int> cachedBiomeHeightWarpEnabled;
        private NativeArray<float> cachedBiomeHeightWarpStrengths;
        private NativeArray<float> cachedBiomeHeightWarpScales;
        private NativeArray<int> cachedBiomeHeightWarpRidged;
        private NativeArray<int> cachedBiomeSecondaryHeightWarpEnabled;
        private NativeArray<float> cachedBiomeSecondaryHeightWarpStrengths;
        private NativeArray<float> cachedBiomeSecondaryHeightWarpScales;
        private NativeArray<int> cachedBiomeSecondaryHeightWarpRidged;
        private NativeArray<int> cachedBiomeCaveWarpEnabled;
        private NativeArray<float> cachedBiomeCaveWarpStrengths;
        private NativeArray<float> cachedBiomeCaveWarpScales;
        private NativeArray<int> cachedBiomeCaveWarpRidged;
        private NativeArray<int> cachedBiomeSecondaryCaveWarpEnabled;
        private NativeArray<float> cachedBiomeSecondaryCaveWarpStrengths;
        private NativeArray<float> cachedBiomeSecondaryCaveWarpScales;
        private NativeArray<int> cachedBiomeSecondaryCaveWarpRidged;
        private NativeArray<int> cachedBiomeExtraHeightLayerEnabled;
        private NativeArray<int> cachedBiomeExtraHeightOctaves;
        private NativeArray<float> cachedBiomeExtraHeightScales;
        private NativeArray<float> cachedBiomeExtraHeightLacunarity;
        private NativeArray<float> cachedBiomeExtraHeightPersistence;
        private NativeArray<float> cachedBiomeExtraHeightImpact;
        private NativeArray<int> cachedBiomeExtraCaveLayerEnabled;
        private NativeArray<int> cachedBiomeExtraCaveOctaves;
        private NativeArray<float> cachedBiomeExtraCaveScales;
        private NativeArray<float> cachedBiomeExtraCaveLacunarity;
        private NativeArray<float> cachedBiomeExtraCavePersistence;
        private NativeArray<float> cachedBiomeExtraCaveImpact;
        private NativeArray<int> cachedBiomeCliffLayerEnabled;
        private NativeArray<float> cachedBiomeCliffScales;
        private NativeArray<float> cachedBiomeCliffImpacts;
        private NativeArray<float> cachedBiomeCliffThresholds;
        private NativeArray<float> cachedBiomeCliffSharpness;
        private NativeArray<int> cachedBiomePlateauLayerEnabled;
        private NativeArray<float> cachedBiomePlateauScales;
        private NativeArray<float> cachedBiomePlateauStepHeights;
        private NativeArray<float> cachedBiomePlateauStrengths;
        private NativeArray<float> cachedBiomePlateauThresholds;
        private NativeArray<float> cachedBiomePlateauSharpness;
        private NativeArray<int> cachedBiomeSurfaceCaveMaskEnabled;
        private NativeArray<float> cachedBiomeSurfaceCaveMaskScales;
        private NativeArray<float> cachedBiomeSurfaceCaveMaskThresholds;
        private NativeArray<float> cachedBiomeSurfaceCaveMaskStrengths;
        private NativeArray<float> cachedBiomeSurfaceCaveMaskFalloffs;
        private int cachedBiomeCount;
        private bool biomeNativeCacheDirty = true;
        private BiomeSettings[] biomeCacheSnapshot = Array.Empty<BiomeSettings>();

        private TerrainProcessingMode runtimeProcessingMode = TerrainProcessingMode.CPU;
        private bool hasLoggedModeFallback;

        private readonly Dictionary<Vector3Int, AsyncGPUReadbackRequest> runningGpuGenRequests =
            new Dictionary<Vector3Int, AsyncGPUReadbackRequest>();
        private readonly Dictionary<Vector3Int, ComputeBuffer> densityBuffers =
            new Dictionary<Vector3Int, ComputeBuffer>();
        private readonly Dictionary<Vector3Int, NativeArray<float>> densityUploadCache =
            new Dictionary<Vector3Int, NativeArray<float>>();
        private readonly Dictionary<Vector3Int, ModificationGpuReadbackRequest> runningGpuModificationRequests =
            new Dictionary<Vector3Int, ModificationGpuReadbackRequest>();
        private readonly Dictionary<Vector3Int, GpuGenerationRequestInfo> gpuGenerationRequestsInfo =
            new Dictionary<Vector3Int, GpuGenerationRequestInfo>();
        private readonly Dictionary<Vector3Int, MeshGpuReadbackRequest> runningGpuMeshRequests =
            new Dictionary<Vector3Int, MeshGpuReadbackRequest>();
        private readonly Dictionary<Vector3Int, ChunkData> chunks = new Dictionary<Vector3Int, ChunkData>();
        private readonly Queue<Vector3Int> dirtyChunkQueue = new Queue<Vector3Int>();
        private readonly HashSet<Vector3Int> dirtyChunkSet = new();
        private readonly List<Vector3Int> reusableChunksToUnload = new();
        private readonly Dictionary<Vector3Int, int> hiddenChunkVisibilityCooldowns = new Dictionary<Vector3Int, int>();
        private readonly HashSet<Vector3Int> chunksPendingVisibilityOverride = new HashSet<Vector3Int>();
        private readonly Dictionary<Vector3Int, MeshJobHandleData> runningMeshJobs = new Dictionary<Vector3Int, MeshJobHandleData>();
        private readonly Dictionary<Vector3Int, VoxelGenJobHandleData> runningGenJobs = new Dictionary<Vector3Int, VoxelGenJobHandleData>();
        
        private IComputeBufferPool computeBufferPool;
        private ITerrainLogger terrainLogger;
        
        private int lastGpuGenerationFrame = -1;
        private int gpuGenerationsThisFrame;
        private readonly Dictionary<int, Vector3> cachedChunkWorldSizesByLod = new Dictionary<int, Vector3>();
        private readonly Queue<TerrainChunk> chunkPool = new Queue<TerrainChunk>();
        private Camera cachedCamera;
        private readonly Plane[] cameraFrustumPlanes = new Plane[6];
        private int lastFrustumCalculationFrame = -1;
        private float smoothedFrameTime = -1f;
        private float adaptiveBudgetTimer;
        private int baseMaxChunksPerFrame;

        private static readonly Vector3Int[] NeighborDirections =
        {
            Vector3Int.right,
            Vector3Int.left,
            Vector3Int.up,
            Vector3Int.down,
            new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, -1)
        };

        private readonly Dictionary<TransitionKey, TransitionMeshData> transitionMeshes = new Dictionary<TransitionKey, TransitionMeshData>();

        private static readonly BiomeSettings[] DefaultBiomeSettingsArray =
        {
            new BiomeSettings
            {
                name = "Default",
                startThreshold = -1f,
                octaves = 4,
                lacunarity = 2f,
                persistence = 0.5f,
                worldGroundLevel = 20f,
                heightImpact = 15f,
                heightNoiseScale = 0.05f,
                caveImpact = 0.3f,
                caveNoiseScale = 0.08f,
                enableHeightDomainWarp = false,
                heightWarpStrength = 0f,
                heightWarpScale = 0.05f,
                heightWarpUseRidged = false,
                enableSecondaryHeightDomainWarp = false,
                secondaryHeightWarpStrength = 0f,
                secondaryHeightWarpScale = 0.025f,
                secondaryHeightWarpUseRidged = false,
                enableCaveDomainWarp = false,
                caveWarpStrength = 0f,
                caveWarpScale = 0.08f,
                caveWarpUseRidged = false,
                enableSecondaryCaveDomainWarp = false,
                secondaryCaveWarpStrength = 0f,
                secondaryCaveWarpScale = 0.04f,
                secondaryCaveWarpUseRidged = false,
                enableAdditionalHeightLayer = false,
                additionalHeightOctaves = 0,
                additionalHeightScale = 0.1f,
                additionalHeightLacunarity = 2f,
                additionalHeightPersistence = 0.5f,
                additionalHeightImpact = 0f,
                enableAdditionalCaveLayer = false,
                additionalCaveOctaves = 0,
                additionalCaveScale = 0.1f,
                additionalCaveLacunarity = 2f,
                additionalCavePersistence = 0.5f,
                additionalCaveImpact = 0f,
                enableCliffLayer = false,
                cliffNoiseScale = 0.05f,
                cliffImpact = 0f,
                cliffThreshold = 0.2f,
                cliffSharpness = 1f,
                enablePlateauLayer = false,
                plateauNoiseScale = 0.04f,
                plateauStepHeight = 3f,
                plateauStrength = 0f,
                plateauThreshold = 0f,
                plateauSharpness = 1f,
                enableSurfaceCaveMask = false,
                surfaceCaveMaskScale = 0.06f,
                surfaceCaveMaskThreshold = 0.1f,
                surfaceCaveMaskStrength = 0f,
                surfaceCaveMaskFalloff = 6f
            }
        };

        private bool hasLoggedDefaultBiomeFallback;

        // Shader property IDs
        private int shaderPropLODLevel;

        private bool ShouldUseGpuForMeshGeneration => runtimeProcessingMode != TerrainProcessingMode.CPU && voxelTerrainShader != null;
        private bool ShouldUseGpuForVoxelGeneration => runtimeProcessingMode == TerrainProcessingMode.GPU && voxelTerrainShader != null;
        private bool ShouldUseGpuForTerrainModification => runtimeProcessingMode == TerrainProcessingMode.GPU && voxelTerrainShader != null;
        #endregion

        #region Unity Lifecycle
        private void RefreshRuntimeProcessingMode(bool logWarnings)
        {
            TerrainProcessingMode requestedMode = processingMode;
            bool computeAvailable = voxelTerrainShader != null;
            TerrainProcessingMode resolvedMode = requestedMode;

            bool fallbackToCpu = !computeAvailable && requestedMode != TerrainProcessingMode.CPU;
            if (fallbackToCpu)
            {
                resolvedMode = TerrainProcessingMode.CPU;
                if (logWarnings && Application.isPlaying && !hasLoggedModeFallback)
                {
                    LogWarning("System", "Compute shader is not available. Falling back to CPU terrain processing mode.");
                    hasLoggedModeFallback = true;
                }
            }
            else
            {
                hasLoggedModeFallback = false;
            }

            if (runtimeProcessingMode != resolvedMode)
            {
                runtimeProcessingMode = resolvedMode;

                if (Application.isPlaying)
                {
                    if (runtimeProcessingMode != TerrainProcessingMode.CPU && computeAvailable)
                    {
                        InitializeStaticGpuBuffers();
                    }
                    else if (runtimeProcessingMode == TerrainProcessingMode.CPU && gpuBuffersInitialized)
                    {
                        ReleaseStaticGpuBuffers();
                    }
                }
            }
        }

        private void OnEnable()
        {
            AnnounceLogFile();
            LogStructured("System", "TerrainManager enabled");
        }
        private void OnDisable()
        {
            LogStructured("System", "TerrainManager disabled");
            CloseLogWriter();
        }

        private void Start()
        {
            if (meshGenerator == null || chunkPrefab == null)
            {
                LogError("System", "TerrainManager is missing required references!");
                enabled = false;
                return;
            }
            if (playerTransform == null && Camera.main != null)
            {
                playerTransform = Camera.main.transform;
            }

            cachedCamera = targetCamera != null ? targetCamera : Camera.main;

            baseMaxChunksPerFrame = Mathf.Clamp(maxChunksPerFrame, minChunksPerFrame, maxAdaptiveChunksPerFrame);
            maxChunksPerFrame = baseMaxChunksPerFrame;
            smoothedFrameTime = Time.unscaledDeltaTime > 0f
                ? Time.unscaledDeltaTime
                : 1f / Mathf.Max(1f, targetFrameRate);

            // Cache shader property IDs
            shaderPropLODLevel = Shader.PropertyToID("_LODLevel");

            // Store initial values for change detection
            previousVoxelSize = voxelSize;
            previousChunkWorldSize = chunkWorldSize;
            InvalidateChunkWorldSizeCache();

            RefreshRuntimeProcessingMode(true);

            LogStructured(
                "System",
                $"Start complete requestedMode={processingMode} runtimeMode={runtimeProcessingMode} chunkWorldSize={FormatVector3(chunkWorldSize)} voxelSize={voxelSize.ToString("F3", CultureInfo.InvariantCulture)} loadDistance={loadDistance.ToString("F1", CultureInfo.InvariantCulture)} maxChunksPerFrame={maxChunksPerFrame}"
            );

            UpdateChunksAroundPlayer();
        }

        private void Update()
        {
            RefreshRuntimeProcessingMode(false);

            if (needsGpuBufferInitRetry && meshGenerator != null && voxelTerrainShader != null &&
                runtimeProcessingMode != TerrainProcessingMode.CPU &&
                meshGenerator.NativeTriangleTable.IsCreated && meshGenerator.NativeTriangleTable.Length > 0 &&
                meshGenerator.NativeEdgeConnections.IsCreated && meshGenerator.NativeEdgeConnections.Length > 0)
            {
                InitializeStaticGpuBuffers();
                if (gpuBuffersInitialized)
                {
                    needsGpuBufferInitRetry = false;
                }
            }

            HandleGeometrySettingsChangeIfNeeded();
            UpdateChunksAroundPlayer();
            UpdateAdaptiveChunkBudget();
            ProcessDirtyChunks();
        }

        private void LateUpdate()
        {
            CompleteGenerationJobs();

            if (runningGpuGenRequests.Count > 0)
            {
                List<Vector3Int> completedRequests = null;
                List<Vector3Int> fallbackToCpu = null;

                foreach (var kvp in runningGpuGenRequests)
                {
                    Vector3Int chunkPos = kvp.Key;
                    AsyncGPUReadbackRequest request = kvp.Value;

                    if (!request.done)
                    {
                        continue;
                    }

                    completedRequests ??= new List<Vector3Int>();
                    completedRequests.Add(chunkPos);

                    if (request.hasError)
                    {
                        GpuGenerationRequestInfo info = GetGpuRequestInfoForLogging(chunkPos);
                        Vector3Int errorThreadGroups = new Vector3Int(info.ThreadGroupsX, info.ThreadGroupsY, info.ThreadGroupsZ);
                        string errorMessage = $"stage=readbackComplete mode=GPU {FormatChunkId(chunkPos, info.LodLevel)} result=error";
                        if (info.BufferCount > 0)
                        {
                            errorMessage = $"{errorMessage} bufferCount={info.BufferCount}";
                        }
                        if (errorThreadGroups != Vector3Int.zero)
                        {
                            errorMessage = $"{errorMessage} threadGroups={FormatThreadGroups(errorThreadGroups.x, errorThreadGroups.y, errorThreadGroups.z)}";
                        }
                        LogError("GpuReadback", errorMessage);

                        if (chunks.TryGetValue(chunkPos, out var existingChunkData) && existingChunkData.chunk != null)
                        {
                            fallbackToCpu ??= new List<Vector3Int>();
                            fallbackToCpu.Add(chunkPos);
                        }

                        gpuGenerationRequestsInfo.Remove(chunkPos);
                        continue;
                    }

                    NativeArray<float> densities = request.GetData<float>();

                    if (!chunks.TryGetValue(chunkPos, out var chunkData) || chunkData.chunk == null)
                    {
                        continue;
                    }

                    Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(chunkData.lodLevel);
                    GpuGenerationRequestInfo requestInfo = EnsureGpuRequestInfo(chunkPos, chunkData.lodLevel, voxelDimensions, densities.Length);

                    DensitySummary summary = CalculateDensitySummary(densities);
                    Vector3Int requestThreadGroups = new Vector3Int(requestInfo.ThreadGroupsX, requestInfo.ThreadGroupsY, requestInfo.ThreadGroupsZ);
                    LogDensitySummary("GpuReadback", "readbackComplete", "GPU", chunkPos, chunkData.lodLevel, voxelDimensions, summary, requestInfo.BufferCount, requestThreadGroups);

                    chunkData.chunk.ApplyDensities(densities);
                    QueueAdjacentChunksForUpdate(chunkPos, true);
                    QueueChunkForUpdate(chunkPos, true);
                }

                if (completedRequests != null)
                {
                    foreach (var pos in completedRequests)
                    {
                        if (densityBuffers.TryGetValue(pos, out ComputeBuffer buffer))
                        {
                            ReleaseBufferSafe(buffer);
                            densityBuffers.Remove(pos);
                        }

                        runningGpuGenRequests.Remove(pos);
                        gpuGenerationRequestsInfo.Remove(pos);
                    }
                }

                if (fallbackToCpu != null)
                {
                    foreach (var pos in fallbackToCpu)
                    {
                        if (chunks.TryGetValue(pos, out var chunkData) && chunkData.chunk != null)
                        {
                            ScheduleGenerateVoxelDataJob(chunkData.chunk, chunkData.lodLevel);
                        }
                    }
                }
            }

            if (runningGpuMeshRequests.Count > 0)
            {
                List<Vector3Int> completedMeshRequests = null;
                List<Vector3Int> fallbackMeshJobs = null;

                foreach (var kvp in runningGpuMeshRequests)
                {
                    Vector3Int chunkPos = kvp.Key;
                    MeshGpuReadbackRequest requestData = kvp.Value;

                    if (!requestData.CounterRequest.done || !requestData.VertexRequest.done || !requestData.NormalRequest.done)
                    {
                        continue;
                    }

                    completedMeshRequests ??= new List<Vector3Int>();
                    completedMeshRequests.Add(chunkPos);

                    if (requestData.CounterRequest.hasError || requestData.VertexRequest.hasError || requestData.NormalRequest.hasError)
                    {
                        Vector3Int tg = requestData.ThreadGroups;
                        LogError(
                            "GpuReadback",
                            $"stage=readbackComplete mode=GPU {FormatChunkId(chunkPos, requestData.LodLevel)} result=error vertexCapacity={requestData.VertexCapacity} threadGroups={FormatThreadGroups(tg.x, tg.y, tg.z)}"
                        );
                        ReleaseMeshRequestBuffers(requestData);
                        gpuGenerationRequestsInfo.Remove(chunkPos);

                        fallbackMeshJobs ??= new List<Vector3Int>();
                        fallbackMeshJobs.Add(chunkPos);
                        continue;
                    }

                    NativeArray<uint> counterData = requestData.CounterRequest.GetData<uint>();
                    long rawVertexCount = counterData.Length > 0 ? counterData[0] : 0u;
                    if (rawVertexCount > int.MaxValue)
                    {
                        LogWarning(
                            "MeshGen",
                            $"{FormatChunkId(chunkPos, requestData.LodLevel)} stage=readbackComplete mode=GPU warning=vertexCountOverflow value={rawVertexCount}"
                        );
                        rawVertexCount = int.MaxValue;
                    }

                    int vertexCount = (int)rawVertexCount;

                    if (vertexCount > requestData.VertexCapacity)
                    {
                        LogWarning(
                            "MeshGen",
                            $"{FormatChunkId(chunkPos, requestData.LodLevel)} stage=readbackComplete mode=GPU warning=vertexOverflow vertices={vertexCount} capacity={requestData.VertexCapacity}"
                        );
                        vertexCount = requestData.VertexCapacity;
                    }

                    Mesh mesh = null;
                    if (vertexCount > 0)
                    {
                        NativeArray<Vector3> vertexData = requestData.VertexRequest.GetData<Vector3>();
                        NativeArray<Vector3> normalData = requestData.NormalRequest.GetData<Vector3>();
                        int availableCount = Mathf.Min(vertexCount, Mathf.Min(vertexData.Length, normalData.Length));

                        if (availableCount < vertexCount)
                        {
                            LogWarning(
                                "MeshGen",
                                $"{FormatChunkId(chunkPos, requestData.LodLevel)} stage=readbackComplete mode=GPU warning=dataClamped requested={vertexCount} available={availableCount}"
                            );
                            vertexCount = availableCount;
                        }

                        NativeSlice<Vector3> vertexSlice = vertexData.Slice(0, vertexCount);
                        NativeSlice<Vector3> normalSlice = normalData.Slice(0, vertexCount);

                        mesh = meshGenerator != null
                            ? meshGenerator.CreateMeshFromNativeSlices(vertexSlice, default, normalSlice, useSequentialIndices: true, sequentialIndexCount: vertexCount)
                            : null;

                        if (mesh == null && meshGenerator == null)
                        {
                            LogError(
                                "MeshGen",
                                $"{FormatChunkId(chunkPos, requestData.LodLevel)} stage=readbackComplete mode=GPU reason=missingMeshGenerator"
                            );
                        }
                    }
                    else
                    {
                        mesh = new Mesh();
                    }

                    if (mesh != null && chunks.TryGetValue(chunkPos, out var chunkData) && chunkData.chunk != null)
                    {
                        ApplyMesh(chunkData.chunk, mesh);

                        int lodForLog = chunkData.lodLevel;
                        Vector3Int voxelDimensions = requestData.VoxelDimensions;
                        Vector3Int tg = requestData.ThreadGroups;
                        int indexCount = vertexCount;
                        int triangleCount = vertexCount / 3;
                        int densitySamples = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

                        LogStructured(
                            "MeshGen",
                            $"stage=completed mode=GPU {FormatChunkId(chunkPos, lodForLog)} voxels={FormatVoxelDimensions(voxelDimensions)} cubes={requestData.CubeCount} densitySamples={densitySamples} vertexCapacity={requestData.VertexCapacity} vertices={vertexCount} indices={indexCount} triangles={triangleCount} threadGroups={FormatThreadGroups(tg.x, tg.y, tg.z)}"
                        );
                    }

                    ReleaseMeshRequestBuffers(requestData);
                    gpuGenerationRequestsInfo.Remove(chunkPos);
                }

                if (completedMeshRequests != null)
                {
                    foreach (var pos in completedMeshRequests)
                    {
                        runningGpuMeshRequests.Remove(pos);
                    }
                }

                if (fallbackMeshJobs != null)
                {
                    foreach (var pos in fallbackMeshJobs)
                    {
                        if (chunks.TryGetValue(pos, out var chunkData) && chunkData.chunk != null)
                        {
                            ScheduleMarchingCubesJob(chunkData.chunk, chunkData.lodLevel);
                        }
                    }
                }
            }

            CompleteRunningMeshJobs();
        }

        private void OnDestroy()
        {
            CloseLogWriter();
            CleanupAllJobs();
            ReleaseStaticGpuBuffers();
            DisposeBiomeNativeCaches();

            while (chunkPool.Count > 0)
            {
                TerrainChunk pooledChunk = chunkPool.Dequeue();
                if (pooledChunk != null)
                {
                    Destroy(pooledChunk.gameObject);
                }
            }
        }
        
        // Detect changes in the Inspector during Play Mode
        private void OnValidate()
        {
            minChunksPerFrame = Mathf.Max(0, minChunksPerFrame);
            maxAdaptiveChunksPerFrame = Mathf.Max(minChunksPerFrame, maxAdaptiveChunksPerFrame);
            maxChunksPerFrame = Mathf.Clamp(maxChunksPerFrame, minChunksPerFrame, maxAdaptiveChunksPerFrame);
            frameRateBuffer = Mathf.Max(0f, frameRateBuffer);
            targetFrameRate = Mathf.Max(1f, targetFrameRate);
            adaptationInterval = Mathf.Max(0.05f, adaptationInterval);
            frameTimeSmoothing = Mathf.Clamp(frameTimeSmoothing, 0.01f, 1f);
            occlusionRayPadding = Mathf.Max(0f, occlusionRayPadding);

            if (!Application.isPlaying)
            {
                baseMaxChunksPerFrame = maxChunksPerFrame;
            }

            RefreshRuntimeProcessingMode(Application.isPlaying);
            if (Application.isPlaying)
            {
                HandleGeometrySettingsChangeIfNeeded();
            }

            MarkBiomeCacheDirty();
        }
        
        private void InitializeStaticGpuBuffers()
        {
            if (gpuBuffersInitialized || voxelTerrainShader == null || meshGenerator == null)
            {
                return;
            }

            if (!meshGenerator.NativeTriangleTable.IsCreated || meshGenerator.NativeTriangleTable.Length <= 0 ||
                !meshGenerator.NativeEdgeConnections.IsCreated || meshGenerator.NativeEdgeConnections.Length <= 0)
            {
                meshGenerator.EnsureTablesInitialized();
            }

            if (!meshGenerator.NativeTriangleTable.IsCreated || meshGenerator.NativeTriangleTable.Length <= 0 ||
                !meshGenerator.NativeEdgeConnections.IsCreated || meshGenerator.NativeEdgeConnections.Length <= 0)
            {
                LogWarning(
                    "GpuBuffers",
                    "TerrainManager attempted to initialize GPU buffers before Marching Cubes lookup tables were ready. Initialization will be retried once the tables are generated.");
                needsGpuBufferInitRetry = true;
                return;
            }

            needsGpuBufferInitRetry = false;

            BiomeSettings[] activeBiomes = GetActiveBiomes(true);
            int biomeCount = activeBiomes.Length;
            if (biomeCount > 0)
            {
                var biomeThresholds = new float[biomeCount];
                var biomeGroundLevels = new float[biomeCount];
                var biomeHeightImpacts = new float[biomeCount];
                var biomeHeightScales = new float[biomeCount];
                var biomeCaveImpacts = new float[biomeCount];
                var biomeCaveScales = new float[biomeCount];
                var biomeOctaves = new int[biomeCount];
                var biomeLacunarity = new float[biomeCount];
                var biomePersistence = new float[biomeCount];
                var biomeHeightWarpEnabled = new int[biomeCount];
                var biomeHeightWarpStrengths = new float[biomeCount];
                var biomeHeightWarpScales = new float[biomeCount];
                var biomeHeightWarpRidged = new int[biomeCount];
                var biomeSecondaryHeightWarpEnabled = new int[biomeCount];
                var biomeSecondaryHeightWarpStrengths = new float[biomeCount];
                var biomeSecondaryHeightWarpScales = new float[biomeCount];
                var biomeSecondaryHeightWarpRidged = new int[biomeCount];
                var biomeCaveWarpEnabled = new int[biomeCount];
                var biomeCaveWarpStrengths = new float[biomeCount];
                var biomeCaveWarpScales = new float[biomeCount];
                var biomeCaveWarpRidged = new int[biomeCount];
                var biomeSecondaryCaveWarpEnabled = new int[biomeCount];
                var biomeSecondaryCaveWarpStrengths = new float[biomeCount];
                var biomeSecondaryCaveWarpScales = new float[biomeCount];
                var biomeSecondaryCaveWarpRidged = new int[biomeCount];
                var biomeExtraHeightLayerEnabled = new int[biomeCount];
                var biomeExtraHeightOctaves = new int[biomeCount];
                var biomeExtraHeightScales = new float[biomeCount];
                var biomeExtraHeightLacunarity = new float[biomeCount];
                var biomeExtraHeightPersistence = new float[biomeCount];
                var biomeExtraHeightImpact = new float[biomeCount];
                var biomeExtraCaveLayerEnabled = new int[biomeCount];
                var biomeExtraCaveOctaves = new int[biomeCount];
                var biomeExtraCaveScales = new float[biomeCount];
                var biomeExtraCaveLacunarity = new float[biomeCount];
                var biomeExtraCavePersistence = new float[biomeCount];
                var biomeExtraCaveImpact = new float[biomeCount];
                var biomeCliffLayerEnabled = new int[biomeCount];
                var biomeCliffScales = new float[biomeCount];
                var biomeCliffImpacts = new float[biomeCount];
                var biomeCliffThresholds = new float[biomeCount];
                var biomeCliffSharpness = new float[biomeCount];
                var biomePlateauLayerEnabled = new int[biomeCount];
                var biomePlateauScales = new float[biomeCount];
                var biomePlateauStepHeights = new float[biomeCount];
                var biomePlateauStrengths = new float[biomeCount];
                var biomePlateauThresholds = new float[biomeCount];
                var biomePlateauSharpness = new float[biomeCount];
                var biomeSurfaceCaveMaskEnabled = new int[biomeCount];
                var biomeSurfaceCaveMaskScales = new float[biomeCount];
                var biomeSurfaceCaveMaskThresholds = new float[biomeCount];
                var biomeSurfaceCaveMaskStrengths = new float[biomeCount];
                var biomeSurfaceCaveMaskFalloffs = new float[biomeCount];

                for (int i = 0; i < biomeCount; i++)
                {
                    BiomeSettings biome = activeBiomes[i];
                    biomeThresholds[i] = biome.startThreshold;
                    biomeGroundLevels[i] = biome.worldGroundLevel;
                    biomeHeightImpacts[i] = biome.heightImpact;
                    biomeHeightScales[i] = biome.heightNoiseScale;
                    biomeCaveImpacts[i] = biome.caveImpact;
                    biomeCaveScales[i] = biome.caveNoiseScale;
                    biomeOctaves[i] = biome.octaves;
                    biomeLacunarity[i] = biome.lacunarity;
                    biomePersistence[i] = biome.persistence;
                    biomeHeightWarpEnabled[i] = biome.enableHeightDomainWarp ? 1 : 0;
                    biomeHeightWarpStrengths[i] = biome.heightWarpStrength;
                    biomeHeightWarpScales[i] = biome.heightWarpScale;
                    biomeHeightWarpRidged[i] = biome.heightWarpUseRidged ? 1 : 0;
                    biomeSecondaryHeightWarpEnabled[i] = biome.enableSecondaryHeightDomainWarp ? 1 : 0;
                    biomeSecondaryHeightWarpStrengths[i] = biome.secondaryHeightWarpStrength;
                    biomeSecondaryHeightWarpScales[i] = biome.secondaryHeightWarpScale;
                    biomeSecondaryHeightWarpRidged[i] = biome.secondaryHeightWarpUseRidged ? 1 : 0;
                    biomeCaveWarpEnabled[i] = biome.enableCaveDomainWarp ? 1 : 0;
                    biomeCaveWarpStrengths[i] = biome.caveWarpStrength;
                    biomeCaveWarpScales[i] = biome.caveWarpScale;
                    biomeCaveWarpRidged[i] = biome.caveWarpUseRidged ? 1 : 0;
                    biomeSecondaryCaveWarpEnabled[i] = biome.enableSecondaryCaveDomainWarp ? 1 : 0;
                    biomeSecondaryCaveWarpStrengths[i] = biome.secondaryCaveWarpStrength;
                    biomeSecondaryCaveWarpScales[i] = biome.secondaryCaveWarpScale;
                    biomeSecondaryCaveWarpRidged[i] = biome.secondaryCaveWarpUseRidged ? 1 : 0;
                    biomeExtraHeightLayerEnabled[i] = biome.enableAdditionalHeightLayer ? 1 : 0;
                    biomeExtraHeightOctaves[i] = biome.additionalHeightOctaves;
                    biomeExtraHeightScales[i] = biome.additionalHeightScale;
                    biomeExtraHeightLacunarity[i] = biome.additionalHeightLacunarity;
                    biomeExtraHeightPersistence[i] = biome.additionalHeightPersistence;
                    biomeExtraHeightImpact[i] = biome.additionalHeightImpact;
                    biomeExtraCaveLayerEnabled[i] = biome.enableAdditionalCaveLayer ? 1 : 0;
                    biomeExtraCaveOctaves[i] = biome.additionalCaveOctaves;
                    biomeExtraCaveScales[i] = biome.additionalCaveScale;
                    biomeExtraCaveLacunarity[i] = biome.additionalCaveLacunarity;
                    biomeExtraCavePersistence[i] = biome.additionalCavePersistence;
                    biomeExtraCaveImpact[i] = biome.additionalCaveImpact;
                    biomeCliffLayerEnabled[i] = biome.enableCliffLayer ? 1 : 0;
                    biomeCliffScales[i] = biome.cliffNoiseScale;
                    biomeCliffImpacts[i] = biome.cliffImpact;
                    biomeCliffThresholds[i] = biome.cliffThreshold;
                    biomeCliffSharpness[i] = biome.cliffSharpness;
                    biomePlateauLayerEnabled[i] = biome.enablePlateauLayer ? 1 : 0;
                    biomePlateauScales[i] = biome.plateauNoiseScale;
                    biomePlateauStepHeights[i] = biome.plateauStepHeight;
                    biomePlateauStrengths[i] = biome.plateauStrength;
                    biomePlateauThresholds[i] = biome.plateauThreshold;
                    biomePlateauSharpness[i] = biome.plateauSharpness;
                    biomeSurfaceCaveMaskEnabled[i] = biome.enableSurfaceCaveMask ? 1 : 0;
                    biomeSurfaceCaveMaskScales[i] = biome.surfaceCaveMaskScale;
                    biomeSurfaceCaveMaskThresholds[i] = biome.surfaceCaveMaskThreshold;
                    biomeSurfaceCaveMaskStrengths[i] = biome.surfaceCaveMaskStrength;
                    biomeSurfaceCaveMaskFalloffs[i] = biome.surfaceCaveMaskFalloff;
                }

                biomeThresholdsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeGroundLevelsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeHeightImpactsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeHeightScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCaveImpactsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCaveScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeOctavesBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeLacunarityBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomePersistenceBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeHeightWarpEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeHeightWarpStrengthsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeHeightWarpScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeHeightWarpRidgedBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeSecondaryHeightWarpEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeSecondaryHeightWarpStrengthsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSecondaryHeightWarpScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSecondaryHeightWarpRidgedBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeCaveWarpEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeCaveWarpStrengthsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCaveWarpScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCaveWarpRidgedBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeSecondaryCaveWarpEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeSecondaryCaveWarpStrengthsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSecondaryCaveWarpScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSecondaryCaveWarpRidgedBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeExtraHeightLayerEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeExtraHeightOctavesBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeExtraHeightScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeExtraHeightLacunarityBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeExtraHeightPersistenceBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeExtraHeightImpactBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeExtraCaveLayerEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeExtraCaveOctavesBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeExtraCaveScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeExtraCaveLacunarityBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeExtraCavePersistenceBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeExtraCaveImpactBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCliffLayerEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeCliffScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCliffImpactsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCliffThresholdsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeCliffSharpnessBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomePlateauLayerEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomePlateauScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomePlateauStepHeightsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomePlateauStrengthsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomePlateauThresholdsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomePlateauSharpnessBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSurfaceCaveMaskEnabledBuffer = new ComputeBuffer(biomeCount, sizeof(int));
                biomeSurfaceCaveMaskScalesBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSurfaceCaveMaskThresholdsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSurfaceCaveMaskStrengthsBuffer = new ComputeBuffer(biomeCount, sizeof(float));
                biomeSurfaceCaveMaskFalloffsBuffer = new ComputeBuffer(biomeCount, sizeof(float));

                biomeThresholdsBuffer.SetData(biomeThresholds);
                biomeGroundLevelsBuffer.SetData(biomeGroundLevels);
                biomeHeightImpactsBuffer.SetData(biomeHeightImpacts);
                biomeHeightScalesBuffer.SetData(biomeHeightScales);
                biomeCaveImpactsBuffer.SetData(biomeCaveImpacts);
                biomeCaveScalesBuffer.SetData(biomeCaveScales);
                biomeOctavesBuffer.SetData(biomeOctaves);
                biomeLacunarityBuffer.SetData(biomeLacunarity);
                biomePersistenceBuffer.SetData(biomePersistence);
                biomeHeightWarpEnabledBuffer.SetData(biomeHeightWarpEnabled);
                biomeHeightWarpStrengthsBuffer.SetData(biomeHeightWarpStrengths);
                biomeHeightWarpScalesBuffer.SetData(biomeHeightWarpScales);
                biomeHeightWarpRidgedBuffer.SetData(biomeHeightWarpRidged);
                biomeSecondaryHeightWarpEnabledBuffer.SetData(biomeSecondaryHeightWarpEnabled);
                biomeSecondaryHeightWarpStrengthsBuffer.SetData(biomeSecondaryHeightWarpStrengths);
                biomeSecondaryHeightWarpScalesBuffer.SetData(biomeSecondaryHeightWarpScales);
                biomeSecondaryHeightWarpRidgedBuffer.SetData(biomeSecondaryHeightWarpRidged);
                biomeCaveWarpEnabledBuffer.SetData(biomeCaveWarpEnabled);
                biomeCaveWarpStrengthsBuffer.SetData(biomeCaveWarpStrengths);
                biomeCaveWarpScalesBuffer.SetData(biomeCaveWarpScales);
                biomeCaveWarpRidgedBuffer.SetData(biomeCaveWarpRidged);
                biomeSecondaryCaveWarpEnabledBuffer.SetData(biomeSecondaryCaveWarpEnabled);
                biomeSecondaryCaveWarpStrengthsBuffer.SetData(biomeSecondaryCaveWarpStrengths);
                biomeSecondaryCaveWarpScalesBuffer.SetData(biomeSecondaryCaveWarpScales);
                biomeSecondaryCaveWarpRidgedBuffer.SetData(biomeSecondaryCaveWarpRidged);
                biomeExtraHeightLayerEnabledBuffer.SetData(biomeExtraHeightLayerEnabled);
                biomeExtraHeightOctavesBuffer.SetData(biomeExtraHeightOctaves);
                biomeExtraHeightScalesBuffer.SetData(biomeExtraHeightScales);
                biomeExtraHeightLacunarityBuffer.SetData(biomeExtraHeightLacunarity);
                biomeExtraHeightPersistenceBuffer.SetData(biomeExtraHeightPersistence);
                biomeExtraHeightImpactBuffer.SetData(biomeExtraHeightImpact);
                biomeExtraCaveLayerEnabledBuffer.SetData(biomeExtraCaveLayerEnabled);
                biomeExtraCaveOctavesBuffer.SetData(biomeExtraCaveOctaves);
                biomeExtraCaveScalesBuffer.SetData(biomeExtraCaveScales);
                biomeExtraCaveLacunarityBuffer.SetData(biomeExtraCaveLacunarity);
                biomeExtraCavePersistenceBuffer.SetData(biomeExtraCavePersistence);
                biomeExtraCaveImpactBuffer.SetData(biomeExtraCaveImpact);
                biomeCliffLayerEnabledBuffer.SetData(biomeCliffLayerEnabled);
                biomeCliffScalesBuffer.SetData(biomeCliffScales);
                biomeCliffImpactsBuffer.SetData(biomeCliffImpacts);
                biomeCliffThresholdsBuffer.SetData(biomeCliffThresholds);
                biomeCliffSharpnessBuffer.SetData(biomeCliffSharpness);
                biomePlateauLayerEnabledBuffer.SetData(biomePlateauLayerEnabled);
                biomePlateauScalesBuffer.SetData(biomePlateauScales);
                biomePlateauStepHeightsBuffer.SetData(biomePlateauStepHeights);
                biomePlateauStrengthsBuffer.SetData(biomePlateauStrengths);
                biomePlateauThresholdsBuffer.SetData(biomePlateauThresholds);
                biomePlateauSharpnessBuffer.SetData(biomePlateauSharpness);
                biomeSurfaceCaveMaskEnabledBuffer.SetData(biomeSurfaceCaveMaskEnabled);
                biomeSurfaceCaveMaskScalesBuffer.SetData(biomeSurfaceCaveMaskScales);
                biomeSurfaceCaveMaskThresholdsBuffer.SetData(biomeSurfaceCaveMaskThresholds);
                biomeSurfaceCaveMaskStrengthsBuffer.SetData(biomeSurfaceCaveMaskStrengths);
                biomeSurfaceCaveMaskFalloffsBuffer.SetData(biomeSurfaceCaveMaskFalloffs);
            }

            triangleTableBuffer = new ComputeBuffer(meshGenerator.NativeTriangleTable.Length, sizeof(int));
            triangleTableBuffer.SetData(meshGenerator.NativeTriangleTable);
            edgeConnectionsBuffer = new ComputeBuffer(meshGenerator.NativeEdgeConnections.Length, sizeof(int));
            edgeConnectionsBuffer.SetData(meshGenerator.NativeEdgeConnections);

            gpuBuffersInitialized = true;
        }

        private void ReleaseStaticGpuBuffers()
        {
            ClearResolutionDependentCaches();

            if (!gpuBuffersInitialized)
            {
                return;
            }

            biomeThresholdsBuffer?.Release();
            biomeThresholdsBuffer = null;
            biomeGroundLevelsBuffer?.Release();
            biomeGroundLevelsBuffer = null;
            biomeHeightImpactsBuffer?.Release();
            biomeHeightImpactsBuffer = null;
            biomeHeightScalesBuffer?.Release();
            biomeHeightScalesBuffer = null;
            biomeCaveImpactsBuffer?.Release();
            biomeCaveImpactsBuffer = null;
            biomeCaveScalesBuffer?.Release();
            biomeCaveScalesBuffer = null;
            biomeOctavesBuffer?.Release();
            biomeOctavesBuffer = null;
            biomeLacunarityBuffer?.Release();
            biomeLacunarityBuffer = null;
            biomePersistenceBuffer?.Release();
            biomePersistenceBuffer = null;
            biomeHeightWarpEnabledBuffer?.Release();
            biomeHeightWarpEnabledBuffer = null;
            biomeHeightWarpStrengthsBuffer?.Release();
            biomeHeightWarpStrengthsBuffer = null;
            biomeHeightWarpScalesBuffer?.Release();
            biomeHeightWarpScalesBuffer = null;
            biomeHeightWarpRidgedBuffer?.Release();
            biomeHeightWarpRidgedBuffer = null;
            biomeSecondaryHeightWarpEnabledBuffer?.Release();
            biomeSecondaryHeightWarpEnabledBuffer = null;
            biomeSecondaryHeightWarpStrengthsBuffer?.Release();
            biomeSecondaryHeightWarpStrengthsBuffer = null;
            biomeSecondaryHeightWarpScalesBuffer?.Release();
            biomeSecondaryHeightWarpScalesBuffer = null;
            biomeSecondaryHeightWarpRidgedBuffer?.Release();
            biomeSecondaryHeightWarpRidgedBuffer = null;
            biomeCaveWarpEnabledBuffer?.Release();
            biomeCaveWarpEnabledBuffer = null;
            biomeCaveWarpStrengthsBuffer?.Release();
            biomeCaveWarpStrengthsBuffer = null;
            biomeCaveWarpScalesBuffer?.Release();
            biomeCaveWarpScalesBuffer = null;
            biomeCaveWarpRidgedBuffer?.Release();
            biomeCaveWarpRidgedBuffer = null;
            biomeSecondaryCaveWarpEnabledBuffer?.Release();
            biomeSecondaryCaveWarpEnabledBuffer = null;
            biomeSecondaryCaveWarpStrengthsBuffer?.Release();
            biomeSecondaryCaveWarpStrengthsBuffer = null;
            biomeSecondaryCaveWarpScalesBuffer?.Release();
            biomeSecondaryCaveWarpScalesBuffer = null;
            biomeSecondaryCaveWarpRidgedBuffer?.Release();
            biomeSecondaryCaveWarpRidgedBuffer = null;
            biomeExtraHeightLayerEnabledBuffer?.Release();
            biomeExtraHeightLayerEnabledBuffer = null;
            biomeExtraHeightOctavesBuffer?.Release();
            biomeExtraHeightOctavesBuffer = null;
            biomeExtraHeightScalesBuffer?.Release();
            biomeExtraHeightScalesBuffer = null;
            biomeExtraHeightLacunarityBuffer?.Release();
            biomeExtraHeightLacunarityBuffer = null;
            biomeExtraHeightPersistenceBuffer?.Release();
            biomeExtraHeightPersistenceBuffer = null;
            biomeExtraHeightImpactBuffer?.Release();
            biomeExtraHeightImpactBuffer = null;
            biomeExtraCaveLayerEnabledBuffer?.Release();
            biomeExtraCaveLayerEnabledBuffer = null;
            biomeExtraCaveOctavesBuffer?.Release();
            biomeExtraCaveOctavesBuffer = null;
            biomeExtraCaveScalesBuffer?.Release();
            biomeExtraCaveScalesBuffer = null;
            biomeExtraCaveLacunarityBuffer?.Release();
            biomeExtraCaveLacunarityBuffer = null;
            biomeExtraCavePersistenceBuffer?.Release();
            biomeExtraCavePersistenceBuffer = null;
            biomeExtraCaveImpactBuffer?.Release();
            biomeExtraCaveImpactBuffer = null;
            biomeCliffLayerEnabledBuffer?.Release();
            biomeCliffLayerEnabledBuffer = null;
            biomeCliffScalesBuffer?.Release();
            biomeCliffScalesBuffer = null;
            biomeCliffImpactsBuffer?.Release();
            biomeCliffImpactsBuffer = null;
            biomeCliffThresholdsBuffer?.Release();
            biomeCliffThresholdsBuffer = null;
            biomeCliffSharpnessBuffer?.Release();
            biomeCliffSharpnessBuffer = null;
            biomePlateauLayerEnabledBuffer?.Release();
            biomePlateauLayerEnabledBuffer = null;
            biomePlateauScalesBuffer?.Release();
            biomePlateauScalesBuffer = null;
            biomePlateauStepHeightsBuffer?.Release();
            biomePlateauStepHeightsBuffer = null;
            biomePlateauStrengthsBuffer?.Release();
            biomePlateauStrengthsBuffer = null;
            biomePlateauThresholdsBuffer?.Release();
            biomePlateauThresholdsBuffer = null;
            biomePlateauSharpnessBuffer?.Release();
            biomePlateauSharpnessBuffer = null;
            biomeSurfaceCaveMaskEnabledBuffer?.Release();
            biomeSurfaceCaveMaskEnabledBuffer = null;
            biomeSurfaceCaveMaskScalesBuffer?.Release();
            biomeSurfaceCaveMaskScalesBuffer = null;
            biomeSurfaceCaveMaskThresholdsBuffer?.Release();
            biomeSurfaceCaveMaskThresholdsBuffer = null;
            biomeSurfaceCaveMaskStrengthsBuffer?.Release();
            biomeSurfaceCaveMaskStrengthsBuffer = null;
            biomeSurfaceCaveMaskFalloffsBuffer?.Release();
            biomeSurfaceCaveMaskFalloffsBuffer = null;
            triangleTableBuffer?.Release();
            triangleTableBuffer = null;
            edgeConnectionsBuffer?.Release();
            edgeConnectionsBuffer = null;

            gpuBuffersInitialized = false;
        }

        private NativeArray<float> GetDensityUploadArray(Vector3Int voxelDimensions)
        {
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

            if (densityUploadCache.TryGetValue(voxelDimensions, out NativeArray<float> cachedArray))
            {
                bool needsResize = !cachedArray.IsCreated || cachedArray.Length != voxelCount;
                if (needsResize)
                {
                    if (cachedArray.IsCreated)
                    {
                        cachedArray.Dispose();
                    }

                    cachedArray = new NativeArray<float>(voxelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    densityUploadCache[voxelDimensions] = cachedArray;
                }

                return cachedArray;
            }

            NativeArray<float> newArray = new NativeArray<float>(voxelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            densityUploadCache[voxelDimensions] = newArray;
            return newArray;
        }

        private void ClearDensityUploadCache()
        {
            foreach (var kvp in densityUploadCache)
            {
                if (kvp.Value.IsCreated)
                {
                    kvp.Value.Dispose();
                }
            }

            densityUploadCache.Clear();
        }

        private void ClearResolutionDependentCaches()
        {
            ClearDensityUploadCache();

            computeBufferPool?.ReleaseStructuredBuffers();
        }

        private BiomeSettings[] GetActiveBiomes(bool logWarningIfFallback)
        {
            activeBiomeDefinitions.Clear();

            if (biomeDefinitions != null)
            {
                for (int i = 0; i < biomeDefinitions.Count; i++)
                {
                    BiomeDefinition definition = biomeDefinitions[i];
                    if (definition != null)
                    {
                        activeBiomeDefinitions.Add(definition);
                    }
                }
            }

            int definitionCount = activeBiomeDefinitions.Count;
            if (definitionCount > 0)
            {
                if (runtimeBiomeSettings.Length != definitionCount)
                {
                    runtimeBiomeSettings = new BiomeSettings[definitionCount];
                }

                for (int i = 0; i < definitionCount; i++)
                {
                    runtimeBiomeSettings[i] = activeBiomeDefinitions[i].ToSettings();
                }

                hasLoggedDefaultBiomeFallback = false;
                return runtimeBiomeSettings;
            }

            if (logWarningIfFallback && !hasLoggedDefaultBiomeFallback)
            {
                LogWarning(
                    "Biome",
                    "TerrainManager has no biomes configured. Using a default biome profile for terrain generation.");
                hasLoggedDefaultBiomeFallback = true;
            }

            return DefaultBiomeSettingsArray;
        }

        private void MarkBiomeCacheDirty()
        {
            biomeNativeCacheDirty = true;
        }

        private BiomeSettings[] EnsureBiomeNativeCaches(bool logWarningIfFallback)
        {
            BiomeSettings[] activeBiomes = GetActiveBiomes(logWarningIfFallback);
            if (ShouldRebuildBiomeNativeCache(activeBiomes))
            {
                RebuildBiomeNativeCaches(activeBiomes);
            }

            return activeBiomes;
        }

        private bool ShouldRebuildBiomeNativeCache(BiomeSettings[] activeBiomes)
        {
            if (biomeNativeCacheDirty)
            {
                return true;
            }

            if (cachedBiomeCount != activeBiomes.Length)
            {
                return true;
            }

            if (biomeCacheSnapshot.Length != activeBiomes.Length)
            {
                return true;
            }

            for (int i = 0; i < activeBiomes.Length; i++)
            {
                if (!BiomeSettingsApproximatelyEqual(activeBiomes[i], biomeCacheSnapshot[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private void RebuildBiomeNativeCaches(BiomeSettings[] activeBiomes)
        {
            CompleteAllGenerationJobsImmediate();
            DisposeBiomeNativeCaches();

            cachedBiomeCount = activeBiomes.Length;
            if (cachedBiomeCount == 0)
            {
                biomeCacheSnapshot = Array.Empty<BiomeSettings>();
                biomeNativeCacheDirty = false;
                return;
            }

            cachedBiomeThresholds = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeGroundLevels = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeHeightImpacts = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeHeightScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCaveImpacts = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCaveScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeOctaves = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeLacunarity = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomePersistence = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeHeightWarpEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeHeightWarpStrengths = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeHeightWarpScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeHeightWarpRidged = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryHeightWarpEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryHeightWarpStrengths = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryHeightWarpScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryHeightWarpRidged = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCaveWarpEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCaveWarpStrengths = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCaveWarpScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCaveWarpRidged = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryCaveWarpEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryCaveWarpStrengths = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryCaveWarpScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSecondaryCaveWarpRidged = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraHeightLayerEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraHeightOctaves = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraHeightScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraHeightLacunarity = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraHeightPersistence = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraHeightImpact = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraCaveLayerEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraCaveOctaves = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraCaveScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraCaveLacunarity = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraCavePersistence = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeExtraCaveImpact = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCliffLayerEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCliffScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCliffImpacts = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCliffThresholds = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeCliffSharpness = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomePlateauLayerEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomePlateauScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomePlateauStepHeights = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomePlateauStrengths = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomePlateauThresholds = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomePlateauSharpness = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSurfaceCaveMaskEnabled = new NativeArray<int>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSurfaceCaveMaskScales = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSurfaceCaveMaskThresholds = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSurfaceCaveMaskStrengths = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);
            cachedBiomeSurfaceCaveMaskFalloffs = new NativeArray<float>(cachedBiomeCount, Allocator.Persistent);

            for (int i = 0; i < cachedBiomeCount; i++)
            {
                BiomeSettings biome = activeBiomes[i];
                cachedBiomeThresholds[i] = biome.startThreshold;
                cachedBiomeGroundLevels[i] = biome.worldGroundLevel;
                cachedBiomeHeightImpacts[i] = biome.heightImpact;
                cachedBiomeHeightScales[i] = biome.heightNoiseScale;
                cachedBiomeCaveImpacts[i] = biome.caveImpact;
                cachedBiomeCaveScales[i] = biome.caveNoiseScale;
                cachedBiomeOctaves[i] = biome.octaves;
                cachedBiomeLacunarity[i] = biome.lacunarity;
                cachedBiomePersistence[i] = biome.persistence;
                cachedBiomeHeightWarpEnabled[i] = biome.enableHeightDomainWarp ? 1 : 0;
                cachedBiomeHeightWarpStrengths[i] = biome.heightWarpStrength;
                cachedBiomeHeightWarpScales[i] = biome.heightWarpScale;
                cachedBiomeHeightWarpRidged[i] = biome.heightWarpUseRidged ? 1 : 0;
                cachedBiomeSecondaryHeightWarpEnabled[i] = biome.enableSecondaryHeightDomainWarp ? 1 : 0;
                cachedBiomeSecondaryHeightWarpStrengths[i] = biome.secondaryHeightWarpStrength;
                cachedBiomeSecondaryHeightWarpScales[i] = biome.secondaryHeightWarpScale;
                cachedBiomeSecondaryHeightWarpRidged[i] = biome.secondaryHeightWarpUseRidged ? 1 : 0;
                cachedBiomeCaveWarpEnabled[i] = biome.enableCaveDomainWarp ? 1 : 0;
                cachedBiomeCaveWarpStrengths[i] = biome.caveWarpStrength;
                cachedBiomeCaveWarpScales[i] = biome.caveWarpScale;
                cachedBiomeCaveWarpRidged[i] = biome.caveWarpUseRidged ? 1 : 0;
                cachedBiomeSecondaryCaveWarpEnabled[i] = biome.enableSecondaryCaveDomainWarp ? 1 : 0;
                cachedBiomeSecondaryCaveWarpStrengths[i] = biome.secondaryCaveWarpStrength;
                cachedBiomeSecondaryCaveWarpScales[i] = biome.secondaryCaveWarpScale;
                cachedBiomeSecondaryCaveWarpRidged[i] = biome.secondaryCaveWarpUseRidged ? 1 : 0;
                cachedBiomeExtraHeightLayerEnabled[i] = biome.enableAdditionalHeightLayer ? 1 : 0;
                cachedBiomeExtraHeightOctaves[i] = biome.additionalHeightOctaves;
                cachedBiomeExtraHeightScales[i] = biome.additionalHeightScale;
                cachedBiomeExtraHeightLacunarity[i] = biome.additionalHeightLacunarity;
                cachedBiomeExtraHeightPersistence[i] = biome.additionalHeightPersistence;
                cachedBiomeExtraHeightImpact[i] = biome.additionalHeightImpact;
                cachedBiomeExtraCaveLayerEnabled[i] = biome.enableAdditionalCaveLayer ? 1 : 0;
                cachedBiomeExtraCaveOctaves[i] = biome.additionalCaveOctaves;
                cachedBiomeExtraCaveScales[i] = biome.additionalCaveScale;
                cachedBiomeExtraCaveLacunarity[i] = biome.additionalCaveLacunarity;
                cachedBiomeExtraCavePersistence[i] = biome.additionalCavePersistence;
                cachedBiomeExtraCaveImpact[i] = biome.additionalCaveImpact;
                cachedBiomeCliffLayerEnabled[i] = biome.enableCliffLayer ? 1 : 0;
                cachedBiomeCliffScales[i] = biome.cliffNoiseScale;
                cachedBiomeCliffImpacts[i] = biome.cliffImpact;
                cachedBiomeCliffThresholds[i] = biome.cliffThreshold;
                cachedBiomeCliffSharpness[i] = biome.cliffSharpness;
                cachedBiomePlateauLayerEnabled[i] = biome.enablePlateauLayer ? 1 : 0;
                cachedBiomePlateauScales[i] = biome.plateauNoiseScale;
                cachedBiomePlateauStepHeights[i] = biome.plateauStepHeight;
                cachedBiomePlateauStrengths[i] = biome.plateauStrength;
                cachedBiomePlateauThresholds[i] = biome.plateauThreshold;
                cachedBiomePlateauSharpness[i] = biome.plateauSharpness;
                cachedBiomeSurfaceCaveMaskEnabled[i] = biome.enableSurfaceCaveMask ? 1 : 0;
                cachedBiomeSurfaceCaveMaskScales[i] = biome.surfaceCaveMaskScale;
                cachedBiomeSurfaceCaveMaskThresholds[i] = biome.surfaceCaveMaskThreshold;
                cachedBiomeSurfaceCaveMaskStrengths[i] = biome.surfaceCaveMaskStrength;
                cachedBiomeSurfaceCaveMaskFalloffs[i] = biome.surfaceCaveMaskFalloff;
            }

            biomeCacheSnapshot = new BiomeSettings[cachedBiomeCount];
            Array.Copy(activeBiomes, biomeCacheSnapshot, cachedBiomeCount);
            biomeNativeCacheDirty = false;
        }

        private static bool BiomeSettingsApproximatelyEqual(BiomeSettings lhs, BiomeSettings rhs)
        {
            if (!string.Equals(lhs.name, rhs.name, StringComparison.Ordinal))
            {
                return false;
            }

            if (!Mathf.Approximately(lhs.startThreshold, rhs.startThreshold)) return false;
            if (lhs.octaves != rhs.octaves) return false;
            if (!Mathf.Approximately(lhs.lacunarity, rhs.lacunarity)) return false;
            if (!Mathf.Approximately(lhs.persistence, rhs.persistence)) return false;
            if (!Mathf.Approximately(lhs.worldGroundLevel, rhs.worldGroundLevel)) return false;
            if (!Mathf.Approximately(lhs.heightImpact, rhs.heightImpact)) return false;
            if (!Mathf.Approximately(lhs.heightNoiseScale, rhs.heightNoiseScale)) return false;
            if (!Mathf.Approximately(lhs.caveImpact, rhs.caveImpact)) return false;
            if (!Mathf.Approximately(lhs.caveNoiseScale, rhs.caveNoiseScale)) return false;
            if (lhs.enableHeightDomainWarp != rhs.enableHeightDomainWarp) return false;
            if (!Mathf.Approximately(lhs.heightWarpStrength, rhs.heightWarpStrength)) return false;
            if (!Mathf.Approximately(lhs.heightWarpScale, rhs.heightWarpScale)) return false;
            if (lhs.heightWarpUseRidged != rhs.heightWarpUseRidged) return false;
            if (lhs.enableSecondaryHeightDomainWarp != rhs.enableSecondaryHeightDomainWarp) return false;
            if (!Mathf.Approximately(lhs.secondaryHeightWarpStrength, rhs.secondaryHeightWarpStrength)) return false;
            if (!Mathf.Approximately(lhs.secondaryHeightWarpScale, rhs.secondaryHeightWarpScale)) return false;
            if (lhs.secondaryHeightWarpUseRidged != rhs.secondaryHeightWarpUseRidged) return false;
            if (lhs.enableCaveDomainWarp != rhs.enableCaveDomainWarp) return false;
            if (!Mathf.Approximately(lhs.caveWarpStrength, rhs.caveWarpStrength)) return false;
            if (!Mathf.Approximately(lhs.caveWarpScale, rhs.caveWarpScale)) return false;
            if (lhs.caveWarpUseRidged != rhs.caveWarpUseRidged) return false;
            if (lhs.enableSecondaryCaveDomainWarp != rhs.enableSecondaryCaveDomainWarp) return false;
            if (!Mathf.Approximately(lhs.secondaryCaveWarpStrength, rhs.secondaryCaveWarpStrength)) return false;
            if (!Mathf.Approximately(lhs.secondaryCaveWarpScale, rhs.secondaryCaveWarpScale)) return false;
            if (lhs.secondaryCaveWarpUseRidged != rhs.secondaryCaveWarpUseRidged) return false;
            if (lhs.enableAdditionalHeightLayer != rhs.enableAdditionalHeightLayer) return false;
            if (lhs.additionalHeightOctaves != rhs.additionalHeightOctaves) return false;
            if (!Mathf.Approximately(lhs.additionalHeightScale, rhs.additionalHeightScale)) return false;
            if (!Mathf.Approximately(lhs.additionalHeightLacunarity, rhs.additionalHeightLacunarity)) return false;
            if (!Mathf.Approximately(lhs.additionalHeightPersistence, rhs.additionalHeightPersistence)) return false;
            if (!Mathf.Approximately(lhs.additionalHeightImpact, rhs.additionalHeightImpact)) return false;
            if (lhs.enableAdditionalCaveLayer != rhs.enableAdditionalCaveLayer) return false;
            if (lhs.additionalCaveOctaves != rhs.additionalCaveOctaves) return false;
            if (!Mathf.Approximately(lhs.additionalCaveScale, rhs.additionalCaveScale)) return false;
            if (!Mathf.Approximately(lhs.additionalCaveLacunarity, rhs.additionalCaveLacunarity)) return false;
            if (!Mathf.Approximately(lhs.additionalCavePersistence, rhs.additionalCavePersistence)) return false;
            if (!Mathf.Approximately(lhs.additionalCaveImpact, rhs.additionalCaveImpact)) return false;
            if (lhs.enableCliffLayer != rhs.enableCliffLayer) return false;
            if (!Mathf.Approximately(lhs.cliffNoiseScale, rhs.cliffNoiseScale)) return false;
            if (!Mathf.Approximately(lhs.cliffImpact, rhs.cliffImpact)) return false;
            if (!Mathf.Approximately(lhs.cliffThreshold, rhs.cliffThreshold)) return false;
            if (!Mathf.Approximately(lhs.cliffSharpness, rhs.cliffSharpness)) return false;
            if (lhs.enablePlateauLayer != rhs.enablePlateauLayer) return false;
            if (!Mathf.Approximately(lhs.plateauNoiseScale, rhs.plateauNoiseScale)) return false;
            if (!Mathf.Approximately(lhs.plateauStepHeight, rhs.plateauStepHeight)) return false;
            if (!Mathf.Approximately(lhs.plateauStrength, rhs.plateauStrength)) return false;
            if (!Mathf.Approximately(lhs.plateauThreshold, rhs.plateauThreshold)) return false;
            if (!Mathf.Approximately(lhs.plateauSharpness, rhs.plateauSharpness)) return false;
            if (lhs.enableSurfaceCaveMask != rhs.enableSurfaceCaveMask) return false;
            if (!Mathf.Approximately(lhs.surfaceCaveMaskScale, rhs.surfaceCaveMaskScale)) return false;
            if (!Mathf.Approximately(lhs.surfaceCaveMaskThreshold, rhs.surfaceCaveMaskThreshold)) return false;
            if (!Mathf.Approximately(lhs.surfaceCaveMaskStrength, rhs.surfaceCaveMaskStrength)) return false;
            if (!Mathf.Approximately(lhs.surfaceCaveMaskFalloff, rhs.surfaceCaveMaskFalloff)) return false;

            return true;
        }

        private void CompleteAllGenerationJobsImmediate()
        {
            if (runningGenJobs.Count == 0)
            {
                return;
            }

            var pendingJobs = new List<KeyValuePair<Vector3Int, VoxelGenJobHandleData>>(runningGenJobs);
            runningGenJobs.Clear();

            foreach (var kvp in pendingJobs)
            {
                var pos = kvp.Key;
                var data = kvp.Value;
                data.handle.Complete();

                if (chunks.TryGetValue(pos, out var chunkData))
                {
                    chunkData.chunk.ApplyDensities(data.densities);
                    if (!isRegenerating)
                    {
                        QueueChunkForUpdate(pos, true);
                    }
                }

                if (data.densities.IsCreated)
                {
                    data.densities.Dispose();
                }
            }
        }

        private void DisposeBiomeNativeCaches()
        {
            if (cachedBiomeThresholds.IsCreated) cachedBiomeThresholds.Dispose();
            if (cachedBiomeGroundLevels.IsCreated) cachedBiomeGroundLevels.Dispose();
            if (cachedBiomeHeightImpacts.IsCreated) cachedBiomeHeightImpacts.Dispose();
            if (cachedBiomeHeightScales.IsCreated) cachedBiomeHeightScales.Dispose();
            if (cachedBiomeCaveImpacts.IsCreated) cachedBiomeCaveImpacts.Dispose();
            if (cachedBiomeCaveScales.IsCreated) cachedBiomeCaveScales.Dispose();
            if (cachedBiomeOctaves.IsCreated) cachedBiomeOctaves.Dispose();
            if (cachedBiomeLacunarity.IsCreated) cachedBiomeLacunarity.Dispose();
            if (cachedBiomePersistence.IsCreated) cachedBiomePersistence.Dispose();
            if (cachedBiomeHeightWarpEnabled.IsCreated) cachedBiomeHeightWarpEnabled.Dispose();
            if (cachedBiomeHeightWarpStrengths.IsCreated) cachedBiomeHeightWarpStrengths.Dispose();
            if (cachedBiomeHeightWarpScales.IsCreated) cachedBiomeHeightWarpScales.Dispose();
            if (cachedBiomeHeightWarpRidged.IsCreated) cachedBiomeHeightWarpRidged.Dispose();
            if (cachedBiomeSecondaryHeightWarpEnabled.IsCreated) cachedBiomeSecondaryHeightWarpEnabled.Dispose();
            if (cachedBiomeSecondaryHeightWarpStrengths.IsCreated) cachedBiomeSecondaryHeightWarpStrengths.Dispose();
            if (cachedBiomeSecondaryHeightWarpScales.IsCreated) cachedBiomeSecondaryHeightWarpScales.Dispose();
            if (cachedBiomeSecondaryHeightWarpRidged.IsCreated) cachedBiomeSecondaryHeightWarpRidged.Dispose();
            if (cachedBiomeCaveWarpEnabled.IsCreated) cachedBiomeCaveWarpEnabled.Dispose();
            if (cachedBiomeCaveWarpStrengths.IsCreated) cachedBiomeCaveWarpStrengths.Dispose();
            if (cachedBiomeCaveWarpScales.IsCreated) cachedBiomeCaveWarpScales.Dispose();
            if (cachedBiomeCaveWarpRidged.IsCreated) cachedBiomeCaveWarpRidged.Dispose();
            if (cachedBiomeSecondaryCaveWarpEnabled.IsCreated) cachedBiomeSecondaryCaveWarpEnabled.Dispose();
            if (cachedBiomeSecondaryCaveWarpStrengths.IsCreated) cachedBiomeSecondaryCaveWarpStrengths.Dispose();
            if (cachedBiomeSecondaryCaveWarpScales.IsCreated) cachedBiomeSecondaryCaveWarpScales.Dispose();
            if (cachedBiomeSecondaryCaveWarpRidged.IsCreated) cachedBiomeSecondaryCaveWarpRidged.Dispose();
            if (cachedBiomeExtraHeightLayerEnabled.IsCreated) cachedBiomeExtraHeightLayerEnabled.Dispose();
            if (cachedBiomeExtraHeightOctaves.IsCreated) cachedBiomeExtraHeightOctaves.Dispose();
            if (cachedBiomeExtraHeightScales.IsCreated) cachedBiomeExtraHeightScales.Dispose();
            if (cachedBiomeExtraHeightLacunarity.IsCreated) cachedBiomeExtraHeightLacunarity.Dispose();
            if (cachedBiomeExtraHeightPersistence.IsCreated) cachedBiomeExtraHeightPersistence.Dispose();
            if (cachedBiomeExtraHeightImpact.IsCreated) cachedBiomeExtraHeightImpact.Dispose();
            if (cachedBiomeExtraCaveLayerEnabled.IsCreated) cachedBiomeExtraCaveLayerEnabled.Dispose();
            if (cachedBiomeExtraCaveOctaves.IsCreated) cachedBiomeExtraCaveOctaves.Dispose();
            if (cachedBiomeExtraCaveScales.IsCreated) cachedBiomeExtraCaveScales.Dispose();
            if (cachedBiomeExtraCaveLacunarity.IsCreated) cachedBiomeExtraCaveLacunarity.Dispose();
            if (cachedBiomeExtraCavePersistence.IsCreated) cachedBiomeExtraCavePersistence.Dispose();
            if (cachedBiomeExtraCaveImpact.IsCreated) cachedBiomeExtraCaveImpact.Dispose();
            if (cachedBiomeCliffLayerEnabled.IsCreated) cachedBiomeCliffLayerEnabled.Dispose();
            if (cachedBiomeCliffScales.IsCreated) cachedBiomeCliffScales.Dispose();
            if (cachedBiomeCliffImpacts.IsCreated) cachedBiomeCliffImpacts.Dispose();
            if (cachedBiomeCliffThresholds.IsCreated) cachedBiomeCliffThresholds.Dispose();
            if (cachedBiomeCliffSharpness.IsCreated) cachedBiomeCliffSharpness.Dispose();
            if (cachedBiomePlateauLayerEnabled.IsCreated) cachedBiomePlateauLayerEnabled.Dispose();
            if (cachedBiomePlateauScales.IsCreated) cachedBiomePlateauScales.Dispose();
            if (cachedBiomePlateauStepHeights.IsCreated) cachedBiomePlateauStepHeights.Dispose();
            if (cachedBiomePlateauStrengths.IsCreated) cachedBiomePlateauStrengths.Dispose();
            if (cachedBiomePlateauThresholds.IsCreated) cachedBiomePlateauThresholds.Dispose();
            if (cachedBiomePlateauSharpness.IsCreated) cachedBiomePlateauSharpness.Dispose();
            if (cachedBiomeSurfaceCaveMaskEnabled.IsCreated) cachedBiomeSurfaceCaveMaskEnabled.Dispose();
            if (cachedBiomeSurfaceCaveMaskScales.IsCreated) cachedBiomeSurfaceCaveMaskScales.Dispose();
            if (cachedBiomeSurfaceCaveMaskThresholds.IsCreated) cachedBiomeSurfaceCaveMaskThresholds.Dispose();
            if (cachedBiomeSurfaceCaveMaskStrengths.IsCreated) cachedBiomeSurfaceCaveMaskStrengths.Dispose();
            if (cachedBiomeSurfaceCaveMaskFalloffs.IsCreated) cachedBiomeSurfaceCaveMaskFalloffs.Dispose();

            cachedBiomeCount = 0;
            biomeCacheSnapshot = Array.Empty<BiomeSettings>();
        }

        #endregion

        #region World Regeneration
        /// <summary>
        /// Completely regenerates the terrain with the current settings.
        /// This completes all running jobs, destroys all chunks, and restarts generation.
        /// </summary>
        public void RegenerateWorld()
        {
            if (isRegenerating)
            {
                LogWarning("Regeneration", "World regeneration already in progress!");
                return;
            }

            MarkBiomeCacheDirty();
            isRegenerating = true;
            StartCoroutine(RegenerateWorldCoroutine());
        }

        private System.Collections.IEnumerator RegenerateWorldCoroutine()
        {
            InvalidateChunkWorldSizeCache();

            // Step 1: Complete all running jobs and dispose resources
            CleanupAllJobs();
            ClearResolutionDependentCaches();

            // Give a frame to ensure all jobs have completed
            yield return null;
            
            // Step 2: Destroy all existing chunks
            foreach (var chunkData in chunks.Values)
            {
                if (chunkData.chunk != null)
                {
                    CancelChunkProcessing(chunkData.chunk.ChunkPosition);
                    ReturnChunkToPool(chunkData.chunk);
                }
            }

            // Step 3: Clear all internal state
            chunks.Clear();
            dirtyChunkQueue.Clear();
            dirtyChunkSet.Clear();
            hiddenChunkVisibilityCooldowns.Clear();

            // Step 4: Store the current settings values
            previousVoxelSize = voxelSize;
            previousChunkWorldSize = chunkWorldSize;

            // Step 5: Start regenerating the world
            LogStructured("Regeneration", "World regeneration: Starting new terrain generation...");
            UpdateChunksAroundPlayer();
            
            isRegenerating = false;
        }

        /// <summary>
        /// Completes all running jobs and disposes of their resources safely.
        /// </summary>
        private void CleanupAllJobs()
        {
            LogStructured("Regeneration", "World regeneration: Cleaning up jobs...");
            
            // Complete and dispose all mesh jobs
            foreach (var jobData in runningMeshJobs.Values)
            {
                jobData.handle.Complete();
                
                if (jobData.vertices.IsCreated)
                    jobData.vertices.Dispose();
                    
                if (jobData.triangles.IsCreated)
                    jobData.triangles.Dispose();
                    
                if (jobData.densities.IsCreated)
                    jobData.densities.Dispose();
            }
            runningMeshJobs.Clear();
            
            // Complete and dispose all generation jobs
            foreach (var genJob in runningGenJobs.Values)
            {
                genJob.handle.Complete();

                if (genJob.densities.IsCreated)
                    genJob.densities.Dispose();
            }
            runningGenJobs.Clear();

            foreach (var kvp in runningGpuGenRequests)
            {
                AsyncGPUReadbackRequest request = kvp.Value;
                if (!request.done)
                {
                    request.WaitForCompletion();
                }

                if (densityBuffers.TryGetValue(kvp.Key, out var buffer))
                {
                    ComputeBufferManager.Instance.ReleaseBuffer(buffer);
                    densityBuffers.Remove(kvp.Key);
                }

                gpuGenerationRequestsInfo.Remove(kvp.Key);
            }
            runningGpuGenRequests.Clear();

            foreach (var kvp in runningGpuModificationRequests)
            {
                ModificationGpuReadbackRequest modificationRequest = kvp.Value;
                if (!modificationRequest.Request.done)
                {
                    modificationRequest.Request.WaitForCompletion();
                }

                if (modificationRequest.DensityBuffer != null)
                {
                    ComputeBufferManager.Instance.ReleaseBuffer(modificationRequest.DensityBuffer);
                }
            }
            runningGpuModificationRequests.Clear();

            foreach (var kvp in runningGpuMeshRequests)
            {
                MeshGpuReadbackRequest meshRequest = kvp.Value;
                if (!meshRequest.CounterRequest.done)
                {
                    meshRequest.CounterRequest.WaitForCompletion();
                }
                if (!meshRequest.VertexRequest.done)
                {
                    meshRequest.VertexRequest.WaitForCompletion();
                }
                if (!meshRequest.NormalRequest.done)
                {
                    meshRequest.NormalRequest.WaitForCompletion();
                }

                ReleaseMeshRequestBuffers(meshRequest);
                gpuGenerationRequestsInfo.Remove(kvp.Key);
            }
            runningGpuMeshRequests.Clear();

            ClearTransitionMeshes();

            LogStructured("Regeneration", "World regeneration: All jobs completed and resources disposed.");
        }
        #endregion

        #region LOD Management
        /// <summary>
        /// Calculates the LOD level based on distance from player
        /// </summary>
        private int CalculateLODLevel(Vector3Int chunkPosition)
        {
            if (!useLOD)
                return 0;

            Vector3 chunkWorldPos = ChunkToWorldPosition(chunkPosition);
            Vector3 baseChunkSize = GetActualChunkWorldSizeForLOD(0);
            Vector3 chunkCenter = chunkWorldPos + baseChunkSize * 0.5f;
            Vector3 offset = playerTransform.position - chunkCenter;
            float distanceSqr = offset.sqrMagnitude;

            for (int i = 0; i < lodDistanceThresholds.Length; i++)
            {
                float threshold = lodDistanceThresholds[i];
                if (distanceSqr <= threshold * threshold)
                    return i;
            }

            return Mathf.Min(maxLODLevel, lodDistanceThresholds.Length);
        }

        
        /// <summary>
        /// Calculates the adjusted voxel size based on LOD level
        /// </summary>
        public float GetVoxelSizeForLOD(int lodLevel)
        {
            // Base size for LOD 0, doubled for each additional level
            return voxelSize * Mathf.Pow(2, lodLevel);
        }
        
        /// <summary>
        /// Calculates the voxel dimensions for a chunk at a specific LOD level
        /// </summary>
        public Vector3Int GetChunkVoxelDimensionsForLOD(int lodLevel)
        {
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);
            return new Vector3Int(
                Mathf.CeilToInt(chunkWorldSize.x / adjustedVoxelSize),
                Mathf.CeilToInt(chunkWorldSize.y / adjustedVoxelSize),
                Mathf.CeilToInt(chunkWorldSize.z / adjustedVoxelSize)
            );
        }

        private Vector3 GetActualChunkWorldSizeForLOD(int lodLevel)
        {
            if (!cachedChunkWorldSizesByLod.TryGetValue(lodLevel, out Vector3 worldSize))
            {
                Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
                float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);
                worldSize = new Vector3(
                    voxelDimensions.x * adjustedVoxelSize,
                    voxelDimensions.y * adjustedVoxelSize,
                    voxelDimensions.z * adjustedVoxelSize
                );
                cachedChunkWorldSizesByLod[lodLevel] = worldSize;
            }

            return worldSize;
        }
        #endregion

        #region Chunk Management
        private void UpdateChunksAroundPlayer()
        {
            if (playerTransform == null || isRegenerating) return;

            Vector3Int playerChunkPos = WorldToChunkPosition(playerTransform.position);
            Vector3 baseChunkSize = GetActualChunkWorldSizeForLOD(0);
            float chunkSizeX = Mathf.Approximately(baseChunkSize.x, 0f) ? 1f : baseChunkSize.x;
            int loadRadius = Mathf.CeilToInt(loadDistance / chunkSizeX);
            int unloadRadius = loadRadius + 2;
            int unloadRadiusSqr = unloadRadius * unloadRadius;

            reusableChunksToUnload.Clear();
            foreach (var chunkPos in chunks.Keys)
            {
                Vector3Int delta = chunkPos - playerChunkPos;
                int distanceSqr = delta.x * delta.x + delta.y * delta.y + delta.z * delta.z;
                if (distanceSqr > unloadRadiusSqr)
                {
                    reusableChunksToUnload.Add(chunkPos);
                }
            }

            foreach (var pos in reusableChunksToUnload)
            {
                if (chunks.TryGetValue(pos, out var chunkData))
                {
                    RemoveTransitionsForChunk(pos);
                    CancelChunkProcessing(pos);
                    ReturnChunkToPool(chunkData.chunk);
                    chunks.Remove(pos);
                }
            }

            for (int x = -loadRadius; x <= loadRadius; x++)
            for (int z = -loadRadius; z <= loadRadius; z++)
            for (int y = -verticalLoadRadius; y <= verticalLoadRadius; y++)
            {
                Vector3Int chunkPos = playerChunkPos + new Vector3Int(x, y, z);

                int lodLevel = CalculateLODLevel(chunkPos);
                bool chunkVisible = IsChunkVisible(chunkPos, lodLevel);

                if (!chunks.TryGetValue(chunkPos, out var existingData))
                {
                    if (chunkVisible)
                    {
                        CreateChunk(chunkPos, lodLevel);
                    }
                    continue;
                }

                if (existingData.lodLevel != lodLevel)
                {
                    if (!chunkVisible && enableFrustumCulling)
                    {
                        continue;
                    }

                    TerrainChunk oldChunk = existingData.chunk;
                    RemoveTransitionsForChunk(chunkPos);
                    CancelChunkProcessing(chunkPos);
                    chunks.Remove(chunkPos);
                    ReturnChunkToPool(oldChunk);
                    CreateChunk(chunkPos, lodLevel);
                }
            }

            if (transitionLODs)
            {
                UpdateTransitionMeshes();
            }
            else if (transitionMeshes.Count > 0)
            {
                ClearTransitionMeshes();
            }
        }


        private void CreateChunk(Vector3Int position, int lodLevel)
        {
            if (enableFrustumCulling && !IsChunkVisible(position, lodLevel))
            {
                return;
            }

            hiddenChunkVisibilityCooldowns.Remove(position);
            Vector3 worldPos = ChunkToWorldPosition(position);
            TerrainChunk chunk = GetChunkFromPool(worldPos, out bool reusedFromPool);
            GameObject chunkObject = chunk.gameObject;
            chunkObject.name = $"Chunk_{position.x}_{position.y}_{position.z}_LOD{lodLevel}";

            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);

            chunk.Initialize(position, voxelDimensions, adjustedVoxelSize, worldPos, lodLevel);

            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);
            string source = reusedFromPool ? "pooled" : "new";
            LogStructured(
                "ChunkLifecycle",
                $"action=create {FormatChunkId(position, lodLevel)} source={source} worldPos={FormatVector3(worldPos)} voxels={FormatVoxelDimensions(voxelDimensions)} sampleCount={voxelCount}"
            );

            // Store chunk with LOD level
            chunks[position] = new ChunkData
            {
                chunk = chunk,
                lodLevel = lodLevel
            };

            // Schedule voxel data generation in background or on GPU
            if (ShouldUseGpuForVoxelGeneration)
            {
                ScheduleGenerateVoxelDataGPU(chunk, lodLevel);
            }
            else
            {
                ScheduleGenerateVoxelDataJob(chunk, lodLevel);
            }

            MarkTransitionsDirtyForChunk(position);
        }

        private TerrainChunk GetChunkFromPool(Vector3 worldPosition, out bool reused)
        {
            TerrainChunk chunk = null;

            while (chunkPool.Count > 0 && chunk == null)
            {
                chunk = chunkPool.Dequeue();
            }

            if (chunk == null)
            {
                GameObject chunkObject = Instantiate(chunkPrefab, worldPosition, Quaternion.identity, transform);
                chunk = chunkObject.GetComponent<TerrainChunk>();
                reused = false;
            }
            else
            {
                reused = true;
            }

            Transform chunkTransform = chunk.transform;
            chunkTransform.SetParent(transform, false);
            chunkTransform.SetPositionAndRotation(worldPosition, Quaternion.identity);
            chunkTransform.localScale = Vector3.one;
            chunk.gameObject.SetActive(true);

            return chunk;
        }

        private void ReturnChunkToPool(TerrainChunk chunk)
        {
            if (chunk == null)
            {
                return;
            }

            chunk.PrepareForReuse();
            Transform chunkTransform = chunk.transform;
            chunkTransform.SetParent(transform, false);
            chunkTransform.localPosition = Vector3.zero;
            chunkTransform.localRotation = Quaternion.identity;
            chunkTransform.localScale = Vector3.one;
            chunk.gameObject.SetActive(false);
            chunkPool.Enqueue(chunk);

            LogStructured(
                "ChunkLifecycle",
                $"action=recycle {FormatChunkId(chunk.ChunkPosition, chunk.LODLevel)} poolSize={chunkPool.Count}"
            );
        }
        
        private void CancelChunkProcessing(Vector3Int chunkPos)
        {
            if (runningMeshJobs.TryGetValue(chunkPos, out var meshJob))
            {
                meshJob.handle.Complete();
                meshJob.vertices.Dispose();
                meshJob.triangles.Dispose();
                meshJob.normals.Dispose();
                if (meshJob.densities.IsCreated) meshJob.densities.Dispose();
                if (meshJob.gradientDensities.IsCreated) meshJob.gradientDensities.Dispose();
                runningMeshJobs.Remove(chunkPos);
            }

            if (runningGenJobs.TryGetValue(chunkPos, out var genJob))
            {
                genJob.handle.Complete();
                if (genJob.densities.IsCreated) genJob.densities.Dispose();
                runningGenJobs.Remove(chunkPos);
            }

            if (runningGpuGenRequests.TryGetValue(chunkPos, out var gpuRequest))
            {
                if (!gpuRequest.done)
                {
                    gpuRequest.WaitForCompletion();
                }

                runningGpuGenRequests.Remove(chunkPos);
                gpuGenerationRequestsInfo.Remove(chunkPos);

                if (densityBuffers.TryGetValue(chunkPos, out var buffer))
                {
                    ReleaseBufferSafe(buffer);
                    densityBuffers.Remove(chunkPos);
                }
                else
                {
                    LogWarning("GpuBuffers", $"Cancelled GPU generation for chunk {chunkPos}, but no density buffer was registered.");
                }
            }
            else if (densityBuffers.TryGetValue(chunkPos, out var buffer))
            {
                LogWarning("GpuBuffers", $"CancelChunkProcessing found a density buffer for chunk {chunkPos} without a matching GPU request. Releasing buffer defensively.");
                ReleaseBufferSafe(buffer);
                densityBuffers.Remove(chunkPos);
                gpuGenerationRequestsInfo.Remove(chunkPos);
            }

            if (runningGpuModificationRequests.TryGetValue(chunkPos, out var modificationRequest))
            {
                if (!modificationRequest.Request.done)
                {
                    modificationRequest.Request.WaitForCompletion();
                }

                if (modificationRequest.DensityBuffer != null)
                {
                    ReleaseBufferSafe(modificationRequest.DensityBuffer);
                }

                runningGpuModificationRequests.Remove(chunkPos);
            }

            if (runningGpuMeshRequests.TryGetValue(chunkPos, out var meshRequest))
            {
                if (!meshRequest.CounterRequest.done)
                {
                    meshRequest.CounterRequest.WaitForCompletion();
                }
                if (!meshRequest.VertexRequest.done)
                {
                    meshRequest.VertexRequest.WaitForCompletion();
                }
                if (!meshRequest.NormalRequest.done)
                {
                    meshRequest.NormalRequest.WaitForCompletion();
                }

                ReleaseMeshRequestBuffers(meshRequest);
                runningGpuMeshRequests.Remove(chunkPos);
                gpuGenerationRequestsInfo.Remove(chunkPos);
            }

            dirtyChunkSet.Remove(chunkPos);
            hiddenChunkVisibilityCooldowns.Remove(chunkPos);
            chunksPendingVisibilityOverride.Remove(chunkPos);
        }


        #region Visibility Checks
        private Camera GetActiveCamera()
        {
            if (targetCamera != null)
            {
                return targetCamera;
            }

            if (cachedCamera == null || !cachedCamera.isActiveAndEnabled)
            {
                cachedCamera = Camera.main;
            }

            return cachedCamera;
        }

        private void UpdateCameraFrustumData()
        {
            if (!enableFrustumCulling)
            {
                return;
            }

            Camera camera = GetActiveCamera();
            if (camera == null)
            {
                return;
            }

            int currentFrame = Time.frameCount;
            if (currentFrame == lastFrustumCalculationFrame)
            {
                return;
            }

            GeometryUtility.CalculateFrustumPlanes(camera, cameraFrustumPlanes);
            lastFrustumCalculationFrame = currentFrame;
        }

        private bool IsChunkVisible(Vector3Int chunkPos, int lodLevel)
        {
            if (!enableFrustumCulling)
            {
                return true;
            }

            Camera camera = GetActiveCamera();
            if (camera == null)
            {
                return true;
            }

            UpdateCameraFrustumData();

            Vector3 worldSize = GetActualChunkWorldSizeForLOD(lodLevel);
            Vector3 worldOrigin = ChunkToWorldPosition(chunkPos);
            if (worldSize.sqrMagnitude <= 0f)
            {
                return true;
            }

            Bounds bounds = new Bounds(worldOrigin + worldSize * 0.5f, worldSize);

            float paddingInVoxels = Mathf.Max(0f, frustumCullingBoundsPaddingVoxels);
            if (paddingInVoxels > 0f)
            {
                float padding = paddingInVoxels * GetVoxelSizeForLOD(lodLevel);
                if (padding > 0f)
                {
                    bounds.Expand(padding * 2f);
                }
            }

            if (cameraFrustumPlanes == null || cameraFrustumPlanes.Length != 6)
            {
                return true;
            }

            if (!GeometryUtility.TestPlanesAABB(cameraFrustumPlanes, bounds))
            {
                return false;
            }

            if (!useOcclusionRayTest)
            {
                return true;
            }

            Vector3 cameraPosition = camera.transform.position;
            Vector3 direction = bounds.center - cameraPosition;
            float distance = direction.magnitude;

            if (distance <= Mathf.Epsilon)
            {
                return true;
            }

            float maxDistance = Mathf.Max(0f, distance - occlusionRayPadding);
            if (maxDistance <= 0f)
            {
                return true;
            }

            direction /= distance;

            if (Physics.Raycast(cameraPosition, direction, out RaycastHit hit, maxDistance, occlusionLayerMask, QueryTriggerInteraction.Ignore))
            {
                if (chunks.TryGetValue(chunkPos, out var chunkData) && chunkData.chunk != null && hit.collider != null)
                {
                    if (hit.collider.transform.IsChildOf(chunkData.chunk.transform))
                    {
                        return true;
                    }
                }

                return false;
            }

            return true;
        }
        #endregion

        #region Transition Mesh Management

        private void UpdateTransitionMeshes()
        {
            if (!transitionLODs || meshGenerator == null)
            {
                return;
            }

            var requiredTransitions = new HashSet<TransitionKey>();

            foreach (var entry in chunks)
            {
                var chunkPos = entry.Key;
                var chunkData = entry.Value;
                if (chunkData.chunk == null)
                {
                    continue;
                }

                foreach (var direction in NeighborDirections)
                {
                    Vector3Int neighborPos = chunkPos + direction;
                    if (!chunks.TryGetValue(neighborPos, out var neighborData) || neighborData.chunk == null)
                    {
                        continue;
                    }

                    if (chunkData.lodLevel >= neighborData.lodLevel)
                    {
                        continue;
                    }

                    var key = new TransitionKey(chunkData.chunk.ChunkPosition, neighborData.chunk.ChunkPosition);
                    requiredTransitions.Add(key);
                    EnsureTransitionMesh(key, chunkData.chunk, neighborData.chunk, direction);
                }
            }

            if (transitionMeshes.Count == 0)
            {
                return;
            }

            var toRemove = new List<TransitionKey>();
            foreach (var kvp in transitionMeshes)
            {
                if (!requiredTransitions.Contains(kvp.Key))
                {
                    DestroyTransitionMesh(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                transitionMeshes.Remove(toRemove[i]);
            }
        }

        private void EnsureTransitionMesh(TransitionKey key, TerrainChunk highChunk, TerrainChunk lowChunk, Vector3Int direction)
        {
            if (!transitionLODs || meshGenerator == null || highChunk == null || lowChunk == null)
            {
                return;
            }

            if (!transitionMeshes.TryGetValue(key, out var data) || data == null || data.MeshFilter == null || data.Mesh == null)
            {
                if (data != null)
                {
                    DestroyTransitionMesh(data);
                }

                var transitionObject = new GameObject($"Transition_{key.HighDetail}_{key.LowDetail}");
                transitionObject.transform.SetParent(highChunk.transform, false);
                transitionObject.layer = highChunk.gameObject.layer;

                var meshFilter = transitionObject.AddComponent<MeshFilter>();
                var meshRenderer = transitionObject.AddComponent<MeshRenderer>();

                var sourceRenderer = highChunk.GetComponent<MeshRenderer>();
                if (sourceRenderer != null)
                {
                    meshRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
                }

                var mesh = new Mesh
                {
                    name = $"TransitionMesh_{key.HighDetail}_{key.LowDetail}"
                };

                meshFilter.sharedMesh = mesh;

                data = new TransitionMeshData
                {
                    MeshFilter = meshFilter,
                    MeshRenderer = meshRenderer,
                    Mesh = mesh,
                    Direction = direction,
                    HighLod = highChunk.LODLevel,
                    LowLod = lowChunk.LODLevel,
                    Dirty = true,
                    SkirtDepth = meshGenerator.TransitionSkirtDepth
                };

                transitionMeshes[key] = data;
            }
            else
            {
                if (data.Direction != direction)
                {
                    data.Direction = direction;
                    data.Dirty = true;
                }

                if (data.MeshFilter.transform.parent != highChunk.transform)
                {
                    data.MeshFilter.transform.SetParent(highChunk.transform, false);
                }

                data.MeshFilter.gameObject.layer = highChunk.gameObject.layer;

                if (data.HighLod != highChunk.LODLevel || data.LowLod != lowChunk.LODLevel)
                {
                    data.HighLod = highChunk.LODLevel;
                    data.LowLod = lowChunk.LODLevel;
                    data.Dirty = true;
                }

                float generatorSkirtDepth = meshGenerator.TransitionSkirtDepth;
                if (!Mathf.Approximately(data.SkirtDepth, generatorSkirtDepth))
                {
                    data.SkirtDepth = generatorSkirtDepth;
                    data.Dirty = true;
                }
            }

            if (!transitionLODs)
            {
                return;
            }

            if (data.Dirty)
            {
                float skirtDepth = meshGenerator.TransitionSkirtDepth;
                if (skirtDepth <= 0f)
                {
                    data.Mesh.Clear();
                    data.Dirty = false;
                    data.SkirtDepth = skirtDepth;
                    return;
                }

                if (highChunk.IsDirty || lowChunk.IsDirty
                    || runningMeshJobs.ContainsKey(highChunk.ChunkPosition)
                    || runningMeshJobs.ContainsKey(lowChunk.ChunkPosition))
                {
                    return;
                }

                bool generated = meshGenerator.GenerateTransitionMesh(highChunk, lowChunk, direction, data.Mesh);
                if (!generated)
                {
                    data.Mesh.Clear();
                }

                data.SkirtDepth = meshGenerator.TransitionSkirtDepth;
                data.Dirty = false;
            }
        }

        private void MarkTransitionsDirtyForChunk(Vector3Int chunkPos)
        {
            if (transitionMeshes.Count == 0)
            {
                return;
            }

            foreach (var kvp in transitionMeshes)
            {
                if (kvp.Key.Involves(chunkPos))
                {
                    kvp.Value.Dirty = true;
                }
            }
        }

        private void RemoveTransitionsForChunk(Vector3Int chunkPos)
        {
            if (transitionMeshes.Count == 0)
            {
                return;
            }

            var toRemove = new List<TransitionKey>();
            foreach (var kvp in transitionMeshes)
            {
                if (kvp.Key.Involves(chunkPos))
                {
                    DestroyTransitionMesh(kvp.Value);
                    toRemove.Add(kvp.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                transitionMeshes.Remove(toRemove[i]);
            }
        }

        private void DestroyTransitionMesh(TransitionMeshData data)
        {
            if (data == null)
            {
                return;
            }

            if (data.MeshFilter != null)
            {
                var go = data.MeshFilter.gameObject;
                if (go != null)
                {
                    Destroy(go);
                }
            }

            if (data.Mesh != null)
            {
                Destroy(data.Mesh);
            }
        }

        private void ClearTransitionMeshes()
        {
            foreach (var entry in transitionMeshes.Values)
            {
                DestroyTransitionMesh(entry);
            }

            transitionMeshes.Clear();
        }

        #endregion

        private void ScheduleGenerateVoxelDataJob(TerrainChunk chunk, int lodLevel)
        {
            if (runningGenJobs.ContainsKey(chunk.ChunkPosition) || isRegenerating) return;

            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);
            var densities = new NativeArray<float>(voxelCount, Allocator.Persistent);

            EnsureBiomeNativeCaches(true);
            int activeBiomeCount = cachedBiomeCount;
            if (activeBiomeCount == 0)
            {
                LogWarning("VoxelGen", "No biome data available for voxel generation.");
                densities.Dispose();
                return;
            }

            // Get the LOD-adjusted voxel size
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);

            var job = new GenerateVoxelDataJob
            {
                densities = densities,
                chunkSize = new int3(voxelDimensions.x, voxelDimensions.y, voxelDimensions.z),
                chunkWorldOrigin = (float3)chunk.WorldPosition,
                voxelSize = adjustedVoxelSize, // Use LOD-adjusted voxel size

                biomeNoiseScale = biomeNoiseScale,
                biomeBlendRange = biomeBlendRange,
                biomeCount = activeBiomeCount,
                biomeThresholds = cachedBiomeThresholds,
                biomeGroundLevels = cachedBiomeGroundLevels,
                biomeHeightImpacts = cachedBiomeHeightImpacts,
                biomeHeightScales = cachedBiomeHeightScales,
                biomeCaveImpacts = cachedBiomeCaveImpacts,
                biomeCaveScales = cachedBiomeCaveScales,

                biomeOctaves = cachedBiomeOctaves,
                biomeLacunarity = cachedBiomeLacunarity,
                biomePersistence = cachedBiomePersistence,
                biomeHeightWarpEnabled = cachedBiomeHeightWarpEnabled,
                biomeHeightWarpStrengths = cachedBiomeHeightWarpStrengths,
                biomeHeightWarpScales = cachedBiomeHeightWarpScales,
                biomeHeightWarpRidged = cachedBiomeHeightWarpRidged,
                biomeSecondaryHeightWarpEnabled = cachedBiomeSecondaryHeightWarpEnabled,
                biomeSecondaryHeightWarpStrengths = cachedBiomeSecondaryHeightWarpStrengths,
                biomeSecondaryHeightWarpScales = cachedBiomeSecondaryHeightWarpScales,
                biomeSecondaryHeightWarpRidged = cachedBiomeSecondaryHeightWarpRidged,
                biomeCaveWarpEnabled = cachedBiomeCaveWarpEnabled,
                biomeCaveWarpStrengths = cachedBiomeCaveWarpStrengths,
                biomeCaveWarpScales = cachedBiomeCaveWarpScales,
                biomeCaveWarpRidged = cachedBiomeCaveWarpRidged,
                biomeSecondaryCaveWarpEnabled = cachedBiomeSecondaryCaveWarpEnabled,
                biomeSecondaryCaveWarpStrengths = cachedBiomeSecondaryCaveWarpStrengths,
                biomeSecondaryCaveWarpScales = cachedBiomeSecondaryCaveWarpScales,
                biomeSecondaryCaveWarpRidged = cachedBiomeSecondaryCaveWarpRidged,
                biomeExtraHeightLayerEnabled = cachedBiomeExtraHeightLayerEnabled,
                biomeExtraHeightOctaves = cachedBiomeExtraHeightOctaves,
                biomeExtraHeightScales = cachedBiomeExtraHeightScales,
                biomeExtraHeightLacunarity = cachedBiomeExtraHeightLacunarity,
                biomeExtraHeightPersistence = cachedBiomeExtraHeightPersistence,
                biomeExtraHeightImpact = cachedBiomeExtraHeightImpact,
                biomeExtraCaveLayerEnabled = cachedBiomeExtraCaveLayerEnabled,
                biomeExtraCaveOctaves = cachedBiomeExtraCaveOctaves,
                biomeExtraCaveScales = cachedBiomeExtraCaveScales,
                biomeExtraCaveLacunarity = cachedBiomeExtraCaveLacunarity,
                biomeExtraCavePersistence = cachedBiomeExtraCavePersistence,
                biomeExtraCaveImpact = cachedBiomeExtraCaveImpact,
                biomeCliffLayerEnabled = cachedBiomeCliffLayerEnabled,
                biomeCliffScales = cachedBiomeCliffScales,
                biomeCliffImpacts = cachedBiomeCliffImpacts,
                biomeCliffThresholds = cachedBiomeCliffThresholds,
                biomeCliffSharpness = cachedBiomeCliffSharpness,
                biomePlateauLayerEnabled = cachedBiomePlateauLayerEnabled,
                biomePlateauScales = cachedBiomePlateauScales,
                biomePlateauStepHeights = cachedBiomePlateauStepHeights,
                biomePlateauStrengths = cachedBiomePlateauStrengths,
                biomePlateauThresholds = cachedBiomePlateauThresholds,
                biomePlateauSharpness = cachedBiomePlateauSharpness,
                biomeSurfaceCaveMaskEnabled = cachedBiomeSurfaceCaveMaskEnabled,
                biomeSurfaceCaveMaskScales = cachedBiomeSurfaceCaveMaskScales,
                biomeSurfaceCaveMaskThresholds = cachedBiomeSurfaceCaveMaskThresholds,
                biomeSurfaceCaveMaskStrengths = cachedBiomeSurfaceCaveMaskStrengths,
                biomeSurfaceCaveMaskFalloffs = cachedBiomeSurfaceCaveMaskFalloffs,

                temperatureNoiseScale = temperatureNoiseScale,
                humidityNoiseScale = humidityNoiseScale,
                riverNoiseScale = riverNoiseScale,
                riverThreshold = riverThreshold,
                riverDepth = riverDepth
            };

            var handle = job.Schedule();

            runningGenJobs[chunk.ChunkPosition] = new VoxelGenJobHandleData
            {
                handle = handle,
                densities = densities
            };

            LogStructured(
                "VoxelGen",
                $"stage=queued mode=CPU {FormatChunkId(chunk.ChunkPosition, lodLevel)} voxels={FormatVoxelDimensions(voxelDimensions)} sampleCount={(voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1)} biomeCount={activeBiomeCount}"
            );
        }
        
                private void ScheduleGenerateVoxelDataGPU(TerrainChunk chunk, int lodLevel)
        {
            if (!ShouldUseGpuForVoxelGeneration || voxelTerrainShader == null || chunk == null)
            {
                ScheduleGenerateVoxelDataJob(chunk, lodLevel);
                return;
            }

            if (!HasGpuCapacityForRequest() || !TryConsumeGpuGenerationBudget())
            {
                ScheduleGenerateVoxelDataJob(chunk, lodLevel);
                return;
            }

            Vector3Int chunkPos = chunk.ChunkPosition;
            if (densityBuffers.ContainsKey(chunkPos) || runningGpuGenRequests.ContainsKey(chunkPos))
            {
                return;
            }

            int kernel = voxelTerrainShader.FindKernel("GenerateVoxelData");

            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

            ComputeBuffer densityBuffer = computeBufferPool.GetBuffer(voxelCount, sizeof(float));
            densityBuffers.Add(chunkPos, densityBuffer);

            voxelTerrainShader.SetBuffer(kernel, "_DensityValues", densityBuffer);
            voxelTerrainShader.SetVector("_ChunkWorldOrigin", chunk.WorldPosition);
            voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
            voxelTerrainShader.SetFloat("_VoxelSize", voxelSize);
            voxelTerrainShader.SetInt("_LODLevel", lodLevel);

            BiomeSettings[] activeBiomes = GetActiveBiomes(false);
            int biomeCount = activeBiomes.Length;
            voxelTerrainShader.SetFloat("_BiomeNoiseScale", biomeNoiseScale);
            voxelTerrainShader.SetInt("_BiomeCount", biomeCount);
            if (biomeCount > 0)
            {
                voxelTerrainShader.SetBuffer(kernel, "_BiomeThresholds", biomeThresholdsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeGroundLevels", biomeGroundLevelsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightImpacts", biomeHeightImpactsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightScales", biomeHeightScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveImpacts", biomeCaveImpactsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveScales", biomeCaveScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeOctaves", biomeOctavesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeLacunarity", biomeLacunarityBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomePersistence", biomePersistenceBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightWarpEnabled", biomeHeightWarpEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightWarpStrengths", biomeHeightWarpStrengthsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightWarpScales", biomeHeightWarpScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightWarpRidged", biomeHeightWarpRidgedBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryHeightWarpEnabled", biomeSecondaryHeightWarpEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryHeightWarpStrengths", biomeSecondaryHeightWarpStrengthsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryHeightWarpScales", biomeSecondaryHeightWarpScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryHeightWarpRidged", biomeSecondaryHeightWarpRidgedBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveWarpEnabled", biomeCaveWarpEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveWarpStrengths", biomeCaveWarpStrengthsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveWarpScales", biomeCaveWarpScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveWarpRidged", biomeCaveWarpRidgedBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryCaveWarpEnabled", biomeSecondaryCaveWarpEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryCaveWarpStrengths", biomeSecondaryCaveWarpStrengthsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryCaveWarpScales", biomeSecondaryCaveWarpScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSecondaryCaveWarpRidged", biomeSecondaryCaveWarpRidgedBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraHeightLayerEnabled", biomeExtraHeightLayerEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraHeightOctaves", biomeExtraHeightOctavesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraHeightScales", biomeExtraHeightScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraHeightLacunarity", biomeExtraHeightLacunarityBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraHeightPersistence", biomeExtraHeightPersistenceBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraHeightImpact", biomeExtraHeightImpactBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraCaveLayerEnabled", biomeExtraCaveLayerEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraCaveOctaves", biomeExtraCaveOctavesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraCaveScales", biomeExtraCaveScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraCaveLacunarity", biomeExtraCaveLacunarityBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraCavePersistence", biomeExtraCavePersistenceBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeExtraCaveImpact", biomeExtraCaveImpactBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCliffLayerEnabled", biomeCliffLayerEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCliffScales", biomeCliffScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCliffImpacts", biomeCliffImpactsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCliffThresholds", biomeCliffThresholdsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeCliffSharpness", biomeCliffSharpnessBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomePlateauLayerEnabled", biomePlateauLayerEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomePlateauScales", biomePlateauScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomePlateauStepHeights", biomePlateauStepHeightsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomePlateauStrengths", biomePlateauStrengthsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomePlateauThresholds", biomePlateauThresholdsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomePlateauSharpness", biomePlateauSharpnessBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSurfaceCaveMaskEnabled", biomeSurfaceCaveMaskEnabledBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSurfaceCaveMaskScales", biomeSurfaceCaveMaskScalesBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSurfaceCaveMaskThresholds", biomeSurfaceCaveMaskThresholdsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSurfaceCaveMaskStrengths", biomeSurfaceCaveMaskStrengthsBuffer);
                voxelTerrainShader.SetBuffer(kernel, "_BiomeSurfaceCaveMaskFalloffs", biomeSurfaceCaveMaskFalloffsBuffer);
            }

            voxelTerrainShader.SetFloat("_TemperatureNoiseScale", temperatureNoiseScale);
            voxelTerrainShader.SetFloat("_HumidityNoiseScale", humidityNoiseScale);
            voxelTerrainShader.SetFloat("_RiverNoiseScale", riverNoiseScale);
            voxelTerrainShader.SetFloat("_RiverThreshold", riverThreshold);
            voxelTerrainShader.SetFloat("_RiverDepth", riverDepth);

            Vector3Int threadGroups = CalculateThreadGroups(voxelDimensions);
            voxelTerrainShader.Dispatch(kernel, threadGroups.x, threadGroups.y, threadGroups.z);

            var request = AsyncGPUReadback.Request(densityBuffer);
            runningGpuGenRequests.Add(chunkPos, request);

            gpuGenerationRequestsInfo[chunkPos] = new GpuGenerationRequestInfo
            {
                LodLevel = lodLevel,
                VoxelDimensions = voxelDimensions,
                ThreadGroupsX = threadGroups.x,
                ThreadGroupsY = threadGroups.y,
                ThreadGroupsZ = threadGroups.z,
                VoxelCount = voxelCount,
                BufferCount = densityBuffer.count
            };

            LogStructured(
                "VoxelGen",
                $"stage=queued mode=GPU {FormatChunkId(chunkPos, lodLevel)} voxels={FormatVoxelDimensions(voxelDimensions)} sampleCount={voxelCount} bufferCount={densityBuffer.count} threadGroups={FormatThreadGroups(threadGroups.x, threadGroups.y, threadGroups.z)} biomeCount={biomeCount}"
            );
        }
                
        #endregion

        #region Job-Based Mesh Generation
        private void ProcessDirtyChunks()
        {
            if (isRegenerating) return;

            int currentFrame = Time.frameCount;
            int requeuedFromCooldown;

            if (enableFrustumCulling)
            {
                requeuedFromCooldown = RequeueChunksWhoseVisibilityCooldownExpired(currentFrame);
            }
            else
            {
                requeuedFromCooldown = hiddenChunkVisibilityCooldowns.Count > 0
                    ? RequeueChunksWhoseVisibilityCooldownExpired(int.MaxValue)
                    : 0;
            }

            int chunkBudget = Mathf.Max(0, maxChunksPerFrame);
            if (chunkBudget <= 0)
            {
                return;
            }

            int processedCount = 0;
            int visibilityChecks = 0;
            int culledHiddenChunks = 0;
            int cooldownSkips = 0;
            int bypassedChunks = 0;

            float processingStartTime = Time.realtimeSinceStartup;
            int initialQueueCount = dirtyChunkQueue.Count;

            while (processedCount < chunkBudget && dirtyChunkQueue.Count > 0)
            {
                Vector3Int chunkPos = dirtyChunkQueue.Dequeue();
                dirtyChunkSet.Remove(chunkPos);
                bool bypassCulling = chunksPendingVisibilityOverride.Contains(chunkPos);
                if (bypassCulling)
                {
                    bypassedChunks++;
                }

                if (runningGenJobs.ContainsKey(chunkPos))
                {
                    continue;
                }

                if (!chunks.TryGetValue(chunkPos, out var chunkData) || chunkData.chunk == null)
                {
                    chunksPendingVisibilityOverride.Remove(chunkPos);
                    hiddenChunkVisibilityCooldowns.Remove(chunkPos);
                    continue;
                }

                if (enableFrustumCulling
                    && hiddenChunkVisibilityCooldowns.TryGetValue(chunkPos, out int nextCheckFrame)
                    && currentFrame < nextCheckFrame
                    && !bypassCulling)
                {
                    cooldownSkips++;
                    continue;
                }

                if (runningMeshJobs.ContainsKey(chunkPos) || runningGpuMeshRequests.ContainsKey(chunkPos))
                {
                    chunksPendingVisibilityOverride.Remove(chunkPos);
                    continue;
                }

                if (enableFrustumCulling && !bypassCulling)
                {
                    visibilityChecks++;
                    if (!IsChunkVisible(chunkPos, chunkData.lodLevel))
                    {
                        culledHiddenChunks++;
                        hiddenChunkVisibilityCooldowns[chunkPos] =
                            currentFrame + Mathf.Max(1, hiddenChunkVisibilityCooldownFrames);
                        continue;
                    }
                }

                hiddenChunkVisibilityCooldowns.Remove(chunkPos);

                bool jobScheduled = false;

                if (ShouldUseGpuForMeshGeneration)
                {
                    jobScheduled = DispatchMarchingCubesGPU(chunkData.chunk, chunkData.lodLevel);
                }

                if (!jobScheduled)
                {
                    ScheduleMarchingCubesJob(chunkData.chunk, chunkData.lodLevel);
                    jobScheduled = true;
                }

                if (jobScheduled && bypassCulling)
                {
                    chunksPendingVisibilityOverride.Remove(chunkPos);
                }

                processedCount++;
            }

            if (logChunkQueueProfiling && (processedCount > 0 || visibilityChecks > 0 || culledHiddenChunks > 0 || cooldownSkips > 0 || requeuedFromCooldown > 0))
            {
                float elapsedMs = (Time.realtimeSinceStartup - processingStartTime) * 1000f;
                int remainingQueueCount = dirtyChunkQueue.Count;
                LogStructured(
                    "ChunkQueue",
                    $"frame={currentFrame} culling={(enableFrustumCulling ? 1 : 0)} queuedStart={initialQueueCount} queuedEnd={remainingQueueCount} processed={processedCount} visibilityTests={visibilityChecks} culled={culledHiddenChunks} cooldownSkips={cooldownSkips} requeued={requeuedFromCooldown} bypass={bypassedChunks} durationMs={elapsedMs:F3}"
                );
            }
        }

        private int RequeueChunksWhoseVisibilityCooldownExpired(int currentFrame)
        {
            if (hiddenChunkVisibilityCooldowns.Count == 0)
            {
                return 0;
            }

            List<Vector3Int> readyChunks = null;

            foreach (var kvp in hiddenChunkVisibilityCooldowns)
            {
                if (currentFrame >= kvp.Value)
                {
                    readyChunks ??= new List<Vector3Int>();
                    readyChunks.Add(kvp.Key);
                }
            }

            if (readyChunks == null)
            {
                return 0;
            }

            for (int i = 0; i < readyChunks.Count; i++)
            {
                Vector3Int chunkPos = readyChunks[i];
                hiddenChunkVisibilityCooldowns.Remove(chunkPos);
                QueueChunkForUpdate(chunkPos);
            }

            return readyChunks.Count;
        }

        private void UpdateAdaptiveChunkBudget()
        {
            int minBudget = Mathf.Max(0, minChunksPerFrame);
            int maxBudget = Mathf.Max(minBudget, maxAdaptiveChunksPerFrame);

            if (!adaptiveChunkBudget)
            {
                baseMaxChunksPerFrame = Mathf.Clamp(baseMaxChunksPerFrame, minBudget, maxBudget);
                maxChunksPerFrame = baseMaxChunksPerFrame;
                return;
            }

            maxChunksPerFrame = Mathf.Clamp(maxChunksPerFrame, minBudget, maxBudget);
            baseMaxChunksPerFrame = Mathf.Clamp(baseMaxChunksPerFrame, minBudget, maxBudget);

            float deltaTime = Time.unscaledDeltaTime;
            if (deltaTime <= 0f)
            {
                return;
            }

            float smoothingFactor = Mathf.Clamp01(frameTimeSmoothing);
            if (smoothedFrameTime < 0f)
            {
                smoothedFrameTime = deltaTime;
            }
            else
            {
                smoothedFrameTime = Mathf.Lerp(smoothedFrameTime, deltaTime, smoothingFactor);
            }

            adaptiveBudgetTimer += deltaTime;
            if (adaptiveBudgetTimer < Mathf.Max(0.05f, adaptationInterval))
            {
                return;
            }

            adaptiveBudgetTimer = 0f;

            float fps = 1f / Mathf.Max(0.0001f, smoothedFrameTime);
            float lowerThreshold = Mathf.Max(1f, targetFrameRate - frameRateBuffer);
            float upperThreshold = targetFrameRate + frameRateBuffer;

            if (fps < lowerThreshold && maxChunksPerFrame > minBudget)
            {
                maxChunksPerFrame = Mathf.Max(minBudget, maxChunksPerFrame - 1);
            }
            else if (fps > upperThreshold && maxChunksPerFrame < maxBudget)
            {
                maxChunksPerFrame = Mathf.Min(maxBudget, maxChunksPerFrame + 1);
            }
        }

        private void ScheduleMarchingCubesJob(TerrainChunk chunk, int lodLevel)
        {
            if (isRegenerating) return;

            meshGenerator?.EnsureTablesInitialized();

            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);
            
            int densityArraySize = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);
            var densitiesForJob = new NativeArray<float>(densityArraySize, Allocator.Persistent);

            chunk.CopyVoxelDataTo(densitiesForJob);

            int gradientArraySize = (voxelDimensions.x + 3) * (voxelDimensions.y + 3) * (voxelDimensions.z + 3);
            var gradientDensities = new NativeArray<float>(gradientArraySize, Allocator.Persistent);
            FillGradientDensities(chunk, lodLevel, gradientDensities);

            var job = new MarchingCubesJob
            {
                densities = densitiesForJob,
                gradientDensities = gradientDensities,
                vertices = new NativeList<Vector3>(Allocator.Persistent),
                triangles = new NativeList<int>(Allocator.Persistent),
                normals = new NativeList<Vector3>(Allocator.Persistent),
                chunkSize = voxelDimensions,
                surfaceLevel = meshGenerator.surfaceLevel,
                voxelSize = adjustedVoxelSize, // Use LOD-adjusted voxel size
                triangleTable = meshGenerator.NativeTriangleTable,
                edgeConnections = meshGenerator.NativeEdgeConnections
            };

            runningMeshJobs[chunk.ChunkPosition] = new MeshJobHandleData
            {
                handle = job.Schedule(),
                vertices = job.vertices,
                triangles = job.triangles,
                normals = job.normals,
                densities = job.densities,
                gradientDensities = job.gradientDensities
            };
        }

        private void FillGradientDensities(TerrainChunk chunk, int lodLevel, NativeArray<float> destination)
        {
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            int index = 0;

            for (int z = -1; z <= voxelDimensions.z + 1; z++)
            {
                for (int y = -1; y <= voxelDimensions.y + 1; y++)
                {
                    for (int x = -1; x <= voxelDimensions.x + 1; x++)
                    {
                        float density = SampleDensityWithNeighbors(chunk, lodLevel, new Vector3Int(x, y, z));
                        destination[index++] = density;
                    }
                }
            }
        }

        private float SampleDensityWithNeighbors(TerrainChunk chunk, int lodLevel, Vector3Int localCoord)
        {
            float voxelScale = GetVoxelSizeForLOD(lodLevel);
            Vector3 worldPos = chunk.WorldPosition + new Vector3(localCoord.x, localCoord.y, localCoord.z) * voxelScale;

            Vector3Int targetChunkPos = WorldToChunkPosition(worldPos);
            if (chunks.TryGetValue(targetChunkPos, out var targetChunkData) && targetChunkData.chunk != null)
            {
                return SampleChunkDensityInterpolated(targetChunkData.chunk, targetChunkData.lodLevel, worldPos);
            }

            return SampleChunkDensityInterpolated(chunk, lodLevel, worldPos);
        }

        private float SampleChunkDensityInterpolated(TerrainChunk chunk, int lodLevel, Vector3 worldPos)
        {
            float voxelScale = GetVoxelSizeForLOD(lodLevel);
            Vector3 localPos = (worldPos - chunk.WorldPosition) / voxelScale;
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);

            float x = Mathf.Clamp(localPos.x, 0f, voxelDimensions.x);
            float y = Mathf.Clamp(localPos.y, 0f, voxelDimensions.y);
            float z = Mathf.Clamp(localPos.z, 0f, voxelDimensions.z);

            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int z0 = Mathf.FloorToInt(z);

            int x1 = Mathf.Min(x0 + 1, voxelDimensions.x);
            int y1 = Mathf.Min(y0 + 1, voxelDimensions.y);
            int z1 = Mathf.Min(z0 + 1, voxelDimensions.z);

            float tx = x - x0;
            float ty = y - y0;
            float tz = z - z0;

            float c000 = chunk.GetVoxel(x0, y0, z0).density;
            float c100 = chunk.GetVoxel(x1, y0, z0).density;
            float c010 = chunk.GetVoxel(x0, y1, z0).density;
            float c110 = chunk.GetVoxel(x1, y1, z0).density;
            float c001 = chunk.GetVoxel(x0, y0, z1).density;
            float c101 = chunk.GetVoxel(x1, y0, z1).density;
            float c011 = chunk.GetVoxel(x0, y1, z1).density;
            float c111 = chunk.GetVoxel(x1, y1, z1).density;

            float c00 = Mathf.Lerp(c000, c100, tx);
            float c10 = Mathf.Lerp(c010, c110, tx);
            float c01 = Mathf.Lerp(c001, c101, tx);
            float c11 = Mathf.Lerp(c011, c111, tx);
            float c0 = Mathf.Lerp(c00, c10, ty);
            float c1 = Mathf.Lerp(c01, c11, ty);
            return Mathf.Lerp(c0, c1, tz);
        }

        private bool DispatchMarchingCubesGPU(TerrainChunk chunk, int lodLevel)
        {
            if (!ShouldUseGpuForMeshGeneration || chunk == null || voxelTerrainShader == null || !gpuBuffersInitialized || triangleTableBuffer == null || edgeConnectionsBuffer == null)
            {
                return false;
            }

            Vector3Int chunkPos = chunk.ChunkPosition;
            if (runningGpuMeshRequests.ContainsKey(chunkPos))
            {
                return true;
            }

            if (!HasGpuCapacityForRequest())
            {
                return false;
            }

            ComputeBuffer densityBuffer = null;
            ComputeBuffer gradientDensityBuffer = null;
            ComputeBuffer vertexBuffer = null;
            ComputeBuffer normalBuffer = null;
            ComputeBuffer counterBuffer = null;
            bool requestQueued = false;

            try
            {
                int kernelIndex = voxelTerrainShader.FindKernel("MarchingCubes");

                Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
                long cubeCount = (long)voxelDimensions.x * voxelDimensions.y * voxelDimensions.z;
                int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

                densityBuffer = computeBufferPool.GetBuffer(voxelCount, sizeof(float));

                NativeArray<float> densities = GetDensityUploadArray(voxelDimensions);
                chunk.CopyVoxelDataTo(densities);
                densityBuffer.SetData(densities, 0, 0, voxelCount);

                int gradientCount = (voxelDimensions.x + 3) * (voxelDimensions.y + 3) * (voxelDimensions.z + 3);
                gradientDensityBuffer = computeBufferPool.GetBuffer(gradientCount, sizeof(float), ComputeBufferType.Structured);
                using (var gradientNative = new NativeArray<float>(gradientCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                {
                    FillGradientDensities(chunk, lodLevel, gradientNative);
                    gradientDensityBuffer.SetData(gradientNative);
                }

                const int maxVerticesPerCube = 15;
                const int maxVertexBufferCapacity = int.MaxValue - (int.MaxValue % 3);
                long safeCubeCount = Math.Max(0L, cubeCount);
                long requestedVertexCapacity = safeCubeCount > 0 && safeCubeCount > long.MaxValue / maxVerticesPerCube
                    ? long.MaxValue
                    : safeCubeCount * maxVerticesPerCube;

                int vertexCapacity;
                if (requestedVertexCapacity > maxVertexBufferCapacity)
                {
                    vertexCapacity = maxVertexBufferCapacity;
                    LogWarning("MeshGen", $"Requested marching cubes vertex capacity ({requestedVertexCapacity}) exceeds vertex buffer limit. Clamping to {vertexCapacity}.");
                }
                else
                {
                    vertexCapacity = (int)requestedVertexCapacity;
                }

                vertexBuffer = computeBufferPool.GetBuffer(vertexCapacity, sizeof(float) * 3, ComputeBufferType.Structured);
                normalBuffer = computeBufferPool.GetBuffer(vertexCapacity, sizeof(float) * 3, ComputeBufferType.Structured);
                counterBuffer = computeBufferPool.GetBuffer(1, sizeof(uint), ComputeBufferType.Structured);

                computeBufferPool.ResetCounterBuffer(counterBuffer);

                voxelTerrainShader.SetBuffer(kernelIndex, "_DensitiesForMC", densityBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_GradientDensities", gradientDensityBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_VertexBuffer", vertexBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_NormalBuffer", normalBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_VertexCountBuffer", counterBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_TriangleTable", triangleTableBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_EdgeConnections", edgeConnectionsBuffer);
                voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
                voxelTerrainShader.SetFloat("_VoxelSize", voxelSize);
                voxelTerrainShader.SetInt("_LODLevel", lodLevel);
                voxelTerrainShader.SetFloat("_SurfaceLevel", meshGenerator.surfaceLevel);
                voxelTerrainShader.SetInt("_MaxVertexCount", vertexCapacity);

                int threadGroupsX = Mathf.CeilToInt(Mathf.Max(1f, voxelDimensions.x) / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(Mathf.Max(1f, voxelDimensions.y) / 8.0f);
                int threadGroupsZ = Mathf.CeilToInt(Mathf.Max(1f, voxelDimensions.z) / 8.0f);
                voxelTerrainShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);

                var counterRequest = AsyncGPUReadback.Request(counterBuffer);
                var vertexRequest = AsyncGPUReadback.Request(vertexBuffer);
                var normalRequest = AsyncGPUReadback.Request(normalBuffer);

                Vector3Int threadGroups = new Vector3Int(threadGroupsX, threadGroupsY, threadGroupsZ);
                runningGpuMeshRequests[chunkPos] = new MeshGpuReadbackRequest
                {
                    VertexRequest = vertexRequest,
                    NormalRequest = normalRequest,
                    CounterRequest = counterRequest,
                    DensityBuffer = densityBuffer,
                    GradientBuffer = gradientDensityBuffer,
                    VertexBuffer = vertexBuffer,
                    NormalBuffer = normalBuffer,
                    CounterBuffer = counterBuffer,
                    LodLevel = lodLevel,
                    VoxelDimensions = voxelDimensions,
                    ThreadGroups = threadGroups,
                    VertexCapacity = vertexCapacity,
                    CubeCount = cubeCount
                };

                gpuGenerationRequestsInfo[chunkPos] = new GpuGenerationRequestInfo
                {
                    LodLevel = lodLevel,
                    VoxelDimensions = voxelDimensions,
                    ThreadGroupsX = threadGroups.x,
                    ThreadGroupsY = threadGroups.y,
                    ThreadGroupsZ = threadGroups.z,
                    VoxelCount = voxelCount,
                    BufferCount = vertexCapacity
                };

                LogStructured(
                    "MeshGen",
                    $"stage=queued mode=GPU {FormatChunkId(chunkPos, lodLevel)} voxels={FormatVoxelDimensions(voxelDimensions)} cubes={cubeCount} densitySamples={voxelCount} vertexCapacity={vertexCapacity} threadGroups={FormatThreadGroups(threadGroups.x, threadGroups.y, threadGroups.z)}"
                );

                requestQueued = true;
                return true;
            }
            catch (Exception ex)
            {
                Vector3Int failedChunkPos = chunk != null ? chunk.ChunkPosition : Vector3Int.zero;
                LogWarning(
                    "MeshGen",
                    $"{FormatChunkId(failedChunkPos, lodLevel)} stage=failed mode=GPU reason={ex.Message} action=fallbackToCPU"
                );
                return false;
            }
            finally
            {
                if (!requestQueued)
                {
                    ReleaseBufferSafe(densityBuffer);
                    ReleaseBufferSafe(gradientDensityBuffer);
                    ReleaseBufferSafe(vertexBuffer);
                    ReleaseBufferSafe(normalBuffer);
                    ReleaseBufferSafe(counterBuffer);
                }
            }
        }

        private void ApplyMesh(TerrainChunk chunk, Mesh mesh)
        {
            if (chunk == null)
            {
                return;
            }

            chunk.ApplyMesh(mesh);
            MarkTransitionsDirtyForChunk(chunk.ChunkPosition);
        }

        private void ReleaseMeshRequestBuffers(in MeshGpuReadbackRequest request)
        {
            ReleaseBufferSafe(request.DensityBuffer);
            ReleaseBufferSafe(request.GradientBuffer);
            ReleaseBufferSafe(request.VertexBuffer);
            ReleaseBufferSafe(request.NormalBuffer);
            ReleaseBufferSafe(request.CounterBuffer);
        }

        private void CompleteGenerationJobs()
        {
            if (runningGenJobs.Count == 0 || isRegenerating) return;

            var completed = new List<Vector3Int>();
            var chunksToQueue = new List<Vector3Int>();
            foreach (var kvp in runningGenJobs)
            {
                Vector3Int pos = kvp.Key;
                var data = kvp.Value;

                if (!data.handle.IsCompleted) continue;

                data.handle.Complete();

                if (chunks.TryGetValue(pos, out var chunkData) && chunkData.chunk != null)
                {
                    Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(chunkData.lodLevel);
                    DensitySummary summary = CalculateDensitySummary(data.densities);
                    LogDensitySummary("VoxelGen", "completed", "CPU", pos, chunkData.lodLevel, voxelDimensions, summary);

                    chunkData.chunk.ApplyDensities(data.densities);
                    if (!isRegenerating)
                    {
                        QueueAdjacentChunksForUpdate(pos, true);
                        chunksToQueue.Add(pos);
                    }
                }

                if (data.densities.IsCreated) data.densities.Dispose();
                completed.Add(pos);
            }

            foreach (var pos in completed) runningGenJobs.Remove(pos);
            foreach (var pos in chunksToQueue) QueueChunkForUpdate(pos, true);
        }

        private void CompleteRunningMeshJobs()
        {
            if (runningMeshJobs.Count == 0 || isRegenerating) return;

            var completedJobs = new List<Vector3Int>();
            foreach (var jobEntry in runningMeshJobs)
            {
                MeshJobHandleData jobData = jobEntry.Value;
                if (!jobData.handle.IsCompleted)
                {
                    continue;
                }

                jobData.handle.Complete();

                Mesh mesh = null;
                if (chunks.TryGetValue(jobEntry.Key, out var chunkData) && chunkData.chunk != null)
                {
                    mesh = meshGenerator.CreateMeshFromJob(jobData.vertices, jobData.triangles, jobData.normals);
                    ApplyMesh(chunkData.chunk, mesh);

                    Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(chunkData.lodLevel);
                    int vertexCount = mesh != null ? mesh.vertexCount : 0;
                    int indexCount = mesh != null ? mesh.triangles.Length : 0;
                    int triangleCount = indexCount / 3;
                    int densitySamples = jobData.densities.IsCreated ? jobData.densities.Length : 0;
                    LogStructured(
                        "MeshGen",
                        $"stage=completed mode=CPU {FormatChunkId(jobEntry.Key, chunkData.lodLevel)} voxels={FormatVoxelDimensions(voxelDimensions)} vertices={vertexCount} indices={indexCount} triangles={triangleCount} densitySamples={densitySamples}"
                    );
                }

                jobData.vertices.Dispose();
                jobData.triangles.Dispose();
                jobData.normals.Dispose();
                if (jobData.densities.IsCreated) jobData.densities.Dispose();
                if (jobData.gradientDensities.IsCreated) jobData.gradientDensities.Dispose();
                completedJobs.Add(jobEntry.Key);
            }
            foreach (var pos in completedJobs) runningMeshJobs.Remove(pos);
        }
        #endregion

        #region Public Terrain Modification API
        public void RequestTerrainModification(Vector3 worldPosition, float radius, float strength)
        {
            if (isRegenerating) return;
            ModifyTerrainInternal(worldPosition, radius, strength);
        }

        
private void ModifyTerrainInternal(Vector3 worldPosition, float radius, float strength)
        {
            Vector3Int min = WorldToVoxelCoordinates(worldPosition - Vector3.one * radius);
            Vector3Int max = WorldToVoxelCoordinates(worldPosition + Vector3.one * radius);

            if (radius <= 0f)
            {
                return;
            }

            HashSet<Vector3Int> modifiedChunks;

            if (ShouldUseGpuForTerrainModification)
            {
                var candidateChunks = CollectChunksForGpu(min, max, worldPosition, radius);
                modifiedChunks = new HashSet<Vector3Int>();

                foreach (var chunkPos in candidateChunks)
                {
                    if (!chunks.TryGetValue(chunkPos, out var chunkData) || chunkData.chunk == null)
                    {
                        continue;
                    }

                    bool applied = false;
                    bool versionMismatch;
                    int attempt = 0;

                    do
                    {
                        attempt++;

                        if (!chunks.TryGetValue(chunkPos, out chunkData) || chunkData.chunk == null)
                        {
                            versionMismatch = false;
                            break;
                        }

                        float lodAdjustedStrength = GetLodAdjustedStrength(strength, chunkData.lodLevel);
                        int expectedVersion = chunkData.chunk.DensityVersion;
                        applied = DispatchModifyDensityGPU(chunkData.chunk, chunkData.lodLevel, worldPosition, radius, lodAdjustedStrength, expectedVersion, out versionMismatch);
                    }
                    while (!applied && versionMismatch && attempt < 3);

                    if (!applied)
                    {
                        var whitelist = new HashSet<Vector3Int> { chunkPos };
                        var cpuModified = ApplyCpuTerrainModification(min, max, worldPosition, radius, strength, whitelist);
                        foreach (var modified in cpuModified)
                        {
                            modifiedChunks.Add(modified);
                        }
                    }
                }
            }
            else
            {
                modifiedChunks = ApplyCpuTerrainModification(min, max, worldPosition, radius, strength);
            }

            foreach (var chunkPos in modifiedChunks)
            {
                for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                for (int z = -1; z <= 1; z++)
                {
                    QueueChunkForUpdate(chunkPos + new Vector3Int(x, y, z));
                }
            }
        }

        private float GetLodAdjustedStrength(float baseStrength, int lodLevel)
        {
            if (lodLevel <= 0)
            {
                return baseStrength;
            }

            return baseStrength * Mathf.Pow(2f, lodLevel);
        }

        private HashSet<Vector3Int> CollectChunksForGpu(Vector3Int min, Vector3Int max, Vector3 worldPosition, float radius)
        {
            var chunksToModify = new HashSet<Vector3Int>();
            float radiusSquared = radius * radius;

            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                Vector3 voxelWorldPos = new Vector3(x, y, z) * voxelSize + Vector3.one * (voxelSize * 0.5f);
                float sqrDist = (voxelWorldPos - worldPosition).sqrMagnitude;
                if (sqrDist > radiusSquared) continue;

                var (chunkPos, _) = WorldToVoxelPosition(voxelWorldPos);
                if (chunks.ContainsKey(chunkPos))
                {
                    chunksToModify.Add(chunkPos);
                }
            }

            return chunksToModify;
        }

        private HashSet<Vector3Int> ApplyCpuTerrainModification(Vector3Int min, Vector3Int max, Vector3 worldPosition, float radius, float strength, HashSet<Vector3Int> chunkWhitelist = null)
        {
            var modifiedChunks = new HashSet<Vector3Int>();

            if (radius <= 0f)
            {
                return modifiedChunks;
            }

            float radiusSquared = radius * radius;

            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                Vector3 voxelWorldPos = new Vector3(x, y, z) * voxelSize + Vector3.one * (voxelSize * 0.5f);
                float sqrDist = (voxelWorldPos - worldPosition).sqrMagnitude;
                if (sqrDist > radiusSquared) continue;

                var (chunkPos, voxelPos) = WorldToVoxelPosition(voxelWorldPos);
                if (!chunks.TryGetValue(chunkPos, out var chunkData))
                {
                    continue;
                }

                if (chunkWhitelist != null && !chunkWhitelist.Contains(chunkPos))
                {
                    continue;
                }

                float lodAdjustedStrength = GetLodAdjustedStrength(strength, chunkData.lodLevel);

                VoxelData currentVoxel = chunkData.chunk.GetVoxel(voxelPos.x, voxelPos.y, voxelPos.z);
                float modificationAmount = lodAdjustedStrength * (1f - Mathf.Sqrt(sqrDist) / radius);
                float newDensity = Mathf.Clamp(currentVoxel.density - modificationAmount, -1f, 1f);

                if (chunkData.chunk.SetVoxel(voxelPos.x, voxelPos.y, voxelPos.z, new VoxelData(newDensity)))
                {
                    modifiedChunks.Add(chunkPos);
                }
            }

            return modifiedChunks;
        }
        
        private bool DispatchModifyDensityGPU(TerrainChunk chunk, int lodLevel, Vector3 modificationPosition, float radius, float lodAdjustedStrength, int expectedVersion, out bool versionMismatch)
        {
            versionMismatch = false;

            if (!ShouldUseGpuForTerrainModification || voxelTerrainShader == null || chunk == null)
            {
                return false;
            }

            Vector3Int chunkPos = chunk.ChunkPosition;

            if (runningGpuModificationRequests.ContainsKey(chunkPos))
            {
                return false;
            }

            if (chunk.DensityVersion != expectedVersion)
            {
                versionMismatch = true;
                return false;
            }

            int kernelIndex = voxelTerrainShader.FindKernel("ModifyDensity");
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

            ComputeBuffer densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));
            bool requestScheduled = false;

            try
            {
                NativeArray<float> uploadBuffer = GetDensityUploadArray(voxelDimensions);
                chunk.CopyVoxelDataTo(uploadBuffer);

                if (chunk.DensityVersion != expectedVersion)
                {
                    versionMismatch = true;
                    return false;
                }

                densityBuffer.SetData(uploadBuffer);

                voxelTerrainShader.SetBuffer(kernelIndex, "_ModifiedDensities", densityBuffer);
                voxelTerrainShader.SetVector("_ChunkWorldOrigin", chunk.WorldPosition);
                voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
                voxelTerrainShader.SetFloat("_VoxelSize", voxelSize);
                voxelTerrainShader.SetInt("_LODLevel", lodLevel);
                voxelTerrainShader.SetVector("_ModificationPosition", modificationPosition);
                voxelTerrainShader.SetFloat("_ModificationRadius", radius);
                voxelTerrainShader.SetFloat("_ModificationStrength", lodAdjustedStrength);

                Vector3Int threadGroups = CalculateThreadGroups(voxelDimensions);
                voxelTerrainShader.Dispatch(kernelIndex, threadGroups.x, threadGroups.y, threadGroups.z);

                ModificationGpuReadbackRequest requestData = new ModificationGpuReadbackRequest
                {
                    DensityBuffer = densityBuffer,
                    ExpectedVersion = expectedVersion,
                    LodLevel = lodLevel,
                    VoxelDimensions = voxelDimensions
                };

                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(
                    densityBuffer,
                    r => OnGpuModifyDensityReadbackComplete(chunkPos, r)
                );

                requestData.Request = request;
                runningGpuModificationRequests[chunkPos] = requestData;
                requestScheduled = true;
                return true;
            }
            finally
            {
                if (!requestScheduled)
                {
                    ComputeBufferManager.Instance.ReleaseBuffer(densityBuffer);
                }
            }
        }


        private void OnGpuModifyDensityReadbackComplete(Vector3Int chunkPos, AsyncGPUReadbackRequest request)
        {
            if (!runningGpuModificationRequests.TryGetValue(chunkPos, out ModificationGpuReadbackRequest requestData))
            {
                return;
            }

            runningGpuModificationRequests.Remove(chunkPos);

            if (requestData.DensityBuffer != null)
            {
                ReleaseBufferSafe(requestData.DensityBuffer);
            }

            if (request.hasError)
            {
                LogError(
                    "GpuReadback",
                    $"stage=readbackComplete mode=GPU {FormatChunkId(chunkPos, requestData.LodLevel)} result=error"
                );
                QueueChunkForUpdate(chunkPos, true);
                return;
            }

            if (!chunks.TryGetValue(chunkPos, out var chunkData) || chunkData.chunk == null)
            {
                return;
            }

            if (chunkData.chunk.DensityVersion != requestData.ExpectedVersion)
            {
                return;
            }

            NativeArray<float> densities = request.GetData<float>();
            chunkData.chunk.ApplyDensities(densities);
            QueueChunkForUpdate(chunkPos, true);
            QueueAdjacentChunksForUpdate(chunkPos, true);
        }

        #endregion

        #region Debug Logging Helpers
        public bool EnableDiagnostics
        {
            get => enableDiagnostics;
            set
            {
                if (enableDiagnostics == value)
                {
                    return;
                }

                enableDiagnostics = value;

                if (enableDiagnostics)
                {
                    AnnounceLogFile();
                }
                else
                {
                    CloseLogWriter();
                }
            }
        }

        [ContextMenu("Toggle Terrain Diagnostics Logging")]
        private void ToggleDiagnosticsLogging()
        {
            EnableDiagnostics = !EnableDiagnostics;
        }

        private void LogStructured(string category, string message)
        {
            if (!enableDiagnostics)
            {
                return;
            }

            string formattedMessage = FormatLog(category, message);
            terrainLogger?.Log(LogType.Log, formattedMessage, this);
            WriteLogToFile("INFO", formattedMessage);
        }

        private void LogWarning(string category, string message)
        {
            if (!enableDiagnostics)
            {
                return;
            }

            string formattedMessage = FormatLog(category, message);
            terrainLogger?.Log(LogType.Warning, formattedMessage, this);
            WriteLogToFile("WARN", formattedMessage);
        }

        private void LogError(string category, string message)
        {
            string formattedMessage = FormatLog(category, message);
            terrainLogger?.Log(LogType.Error, formattedMessage, this);

            if (!enableDiagnostics)
            {
                return;
            }

            WriteLogToFile("ERROR", formattedMessage);
        }

        private void WriteLogToFile(string severity, string message)
        {
            if (!enableDiagnostics)
            {
                return;
            }

            lock (logLock)
            {
                EnsureLogWriter();

                if (logWriter == null)
                {
                    return;
                }

                string timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                logWriter.WriteLine($"{timestamp} [{severity}] {message}");
            }
        }

        private void EnsureLogWriter()
        {
            if (!enableDiagnostics)
            {
                return;
            }

            if (logWriter != null)
            {
                return;
            }

            string directory = Path.Combine(Application.persistentDataPath, "TerrainLogs");
            Directory.CreateDirectory(directory);

            if (string.IsNullOrEmpty(logFilePath))
            {
                logFilePath = Path.Combine(directory, $"Terrain_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
                lastLogFilePath = logFilePath;
            }

            bool append = logFileInitialized && File.Exists(logFilePath);
            logWriter = new StreamWriter(logFilePath, append, Encoding.UTF8)
            {
                AutoFlush = true
            };

            string header = logFileInitialized
                ? $"# TerrainManager logging resumed at {DateTime.UtcNow:O}"
                : $"# TerrainManager log started at {DateTime.UtcNow:O}";
            logWriter.WriteLine(header);
            logFileInitialized = true;
        }

        private void CloseLogWriter()
        {
            lock (logLock)
            {
                if (logWriter != null)
                {
                    logWriter.Flush();
                    logWriter.Dispose();
                    logWriter = null;
                }

                logFilePath = null;
                logFileInitialized = false;
                logFileAnnounced = false;
            }
        }

        private void AnnounceLogFile()
        {
            if (!enableDiagnostics)
            {
                return;
            }

            string path = null;
            lock (logLock)
            {
                EnsureLogWriter();
                if (!logFileAnnounced && !string.IsNullOrEmpty(logFilePath))
                {
                    logFileAnnounced = true;
                    path = logFilePath;
                }
            }

            if (!string.IsNullOrEmpty(path))
            {
                LogStructured("System", $"Detailed terrain diagnostics will be written to {path}");
            }
        }

        private static string FormatLog(string category, string message) => $"[Terrain][{category}] {message}";

        private static string FormatChunkId(Vector3Int chunkPos, int lodLevel) => $"chunk=({chunkPos.x},{chunkPos.y},{chunkPos.z}) lod={lodLevel}";

        private static string FormatVoxelDimensions(Vector3Int dims) => $"{dims.x}x{dims.y}x{dims.z}";

        private static string FormatVector3(Vector3 value) => string.Format(CultureInfo.InvariantCulture, "({0:F2},{1:F2},{2:F2})", value.x, value.y, value.z);

        private static string FormatThreadGroups(int x, int y, int z) => $"({x},{y},{z})";

        private static string FormatDensitySummary(DensitySummary summary)
        {
            string min = summary.HasValidSamples ? summary.Min.ToString("F3", CultureInfo.InvariantCulture) : "n/a";
            string max = summary.HasValidSamples ? summary.Max.ToString("F3", CultureInfo.InvariantCulture) : "n/a";
            string avg = summary.HasValidSamples ? summary.Average.ToString("F3", CultureInfo.InvariantCulture) : "n/a";
            return $"samples={summary.SampleCount} valid={summary.ValidSamples} invalid={summary.InvalidSamples} min={min} max={max} avg={avg}";
        }

        private DensitySummary CalculateDensitySummary(NativeArray<float> densities)
        {
            if (!densities.IsCreated || densities.Length == 0)
            {
                return new DensitySummary
                {
                    SampleCount = densities.IsCreated ? densities.Length : 0,
                    ValidSamples = 0,
                    InvalidSamples = 0,
                    Min = 0f,
                    Max = 0f,
                    Average = 0f
                };
            }

            double total = 0d;
            int validCount = 0;
            int invalidCount = 0;
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;

            for (int i = 0; i < densities.Length; i++)
            {
                float value = densities[i];
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    invalidCount++;
                    continue;
                }

                if (value < min) min = value;
                if (value > max) max = value;
                total += value;
                validCount++;
            }

            return new DensitySummary
            {
                SampleCount = densities.Length,
                ValidSamples = validCount,
                InvalidSamples = invalidCount,
                Min = validCount > 0 ? min : 0f,
                Max = validCount > 0 ? max : 0f,
                Average = validCount > 0 ? (float)(total / validCount) : 0f
            };
        }

        private void LogDensitySummary(string category, string stage, string mode, Vector3Int chunkPos, int lodLevel, Vector3Int voxelDimensions, DensitySummary summary, int bufferCount = 0, Vector3Int threadGroups = default)
        {
            string messageBase = $"stage={stage} mode={mode} {FormatChunkId(chunkPos, lodLevel)} voxels={FormatVoxelDimensions(voxelDimensions)}";
            if (bufferCount > 0)
            {
                messageBase = $"{messageBase} bufferCount={bufferCount}";
            }
            if (threadGroups != Vector3Int.zero)
            {
                messageBase = $"{messageBase} threadGroups={FormatThreadGroups(threadGroups.x, threadGroups.y, threadGroups.z)}";
            }

            if (!summary.HasSamples)
            {
                LogStructured(category, $"{messageBase} samples=0");
                return;
            }

            string fullMessage = $"{messageBase} {FormatDensitySummary(summary)}";

            if (summary.HasInvalidSamples)
            {
                LogWarning(category, $"{fullMessage} anomaly=invalidDensity");
                return;
            }

            LogStructured(category, fullMessage);
        }

        private static Vector3Int CalculateThreadGroups(Vector3Int voxelDimensions)
        {
            int threadGroupsX = Mathf.CeilToInt((voxelDimensions.x + 1) / 8.0f);
            int threadGroupsY = Mathf.CeilToInt((voxelDimensions.y + 1) / 8.0f);
            int threadGroupsZ = Mathf.CeilToInt((voxelDimensions.z + 1) / 8.0f);

            threadGroupsX = Mathf.Max(1, threadGroupsX);
            threadGroupsY = Mathf.Max(1, threadGroupsY);
            threadGroupsZ = Mathf.Max(1, threadGroupsZ);

            return new Vector3Int(threadGroupsX, threadGroupsY, threadGroupsZ);
        }

        private GpuGenerationRequestInfo EnsureGpuRequestInfo(Vector3Int chunkPos, int lodLevel, Vector3Int voxelDimensions, int sampleCount)
        {
            if (gpuGenerationRequestsInfo.TryGetValue(chunkPos, out var info))
            {
                return info;
            }

            Vector3Int threadGroups = CalculateThreadGroups(voxelDimensions);
            info = new GpuGenerationRequestInfo
            {
                LodLevel = lodLevel,
                VoxelDimensions = voxelDimensions,
                ThreadGroupsX = threadGroups.x,
                ThreadGroupsY = threadGroups.y,
                ThreadGroupsZ = threadGroups.z,
                VoxelCount = sampleCount,
                BufferCount = sampleCount
            };
            gpuGenerationRequestsInfo[chunkPos] = info;
            return info;
        }

        private GpuGenerationRequestInfo GetGpuRequestInfoForLogging(Vector3Int chunkPos)
        {
            if (gpuGenerationRequestsInfo.TryGetValue(chunkPos, out var info))
            {
                return info;
            }

            int lodLevel = 0;
            Vector3Int voxelDimensions = Vector3Int.zero;
            if (chunks.TryGetValue(chunkPos, out var chunkData) && chunkData.chunk != null)
            {
                lodLevel = chunkData.lodLevel;
                voxelDimensions = GetChunkVoxelDimensionsForLOD(chunkData.lodLevel);
            }

            Vector3Int threadGroups = voxelDimensions == Vector3Int.zero ? Vector3Int.zero : CalculateThreadGroups(voxelDimensions);
            int bufferCount = densityBuffers.TryGetValue(chunkPos, out var buffer) ? buffer.count : 0;

            return new GpuGenerationRequestInfo
            {
                LodLevel = lodLevel,
                VoxelDimensions = voxelDimensions,
                ThreadGroupsX = threadGroups.x,
                ThreadGroupsY = threadGroups.y,
                ThreadGroupsZ = threadGroups.z,
                VoxelCount = 0,
                BufferCount = bufferCount
            };
        }
        #endregion

        #region Utility Methods
        private void InvalidateChunkWorldSizeCache()
        {
            cachedChunkWorldSizesByLod.Clear();
        }

        private void HandleGeometrySettingsChangeIfNeeded()
        {
            bool voxelSizeChanged = !Mathf.Approximately(previousVoxelSize, voxelSize);
            bool chunkWorldSizeChanged = (previousChunkWorldSize - chunkWorldSize).sqrMagnitude > 0.0001f;

            if ((voxelSizeChanged || chunkWorldSizeChanged) && !isRegenerating)
            {
                string changeDescription;
                if (voxelSizeChanged && chunkWorldSizeChanged)
                    changeDescription = $"VoxelSize {previousVoxelSize}→{voxelSize} and chunkWorldSize {previousChunkWorldSize}→{chunkWorldSize}";
                else if (voxelSizeChanged)
                    changeDescription = $"VoxelSize {previousVoxelSize}→{voxelSize}";
                else
                    changeDescription = $"chunkWorldSize {previousChunkWorldSize}→{chunkWorldSize}";

                LogStructured("System", $"Terrain settings changed ({changeDescription}). Regenerating world...");
                InvalidateChunkWorldSizeCache();
                RegenerateWorld();
            }
        }

        private static readonly Vector3Int[] FaceNeighborOffsets =
        {
            Vector3Int.right,
            Vector3Int.left,
            Vector3Int.up,
            Vector3Int.down,
            Vector3Int.forward,
            Vector3Int.back
        };

        private void QueueAdjacentChunksForUpdate(Vector3Int chunkPos, bool allowCullingBypass = true)
        {
            foreach (Vector3Int offset in FaceNeighborOffsets)
            {
                Vector3Int neighborPos = chunkPos + offset;
                if (!chunks.ContainsKey(neighborPos))
                {
                    continue;
                }

                if (runningGenJobs.ContainsKey(neighborPos) || runningMeshJobs.ContainsKey(neighborPos))
                {
                    continue;
                }

                QueueChunkForUpdate(neighborPos, allowCullingBypass);
            }
        }

        public void QueueChunkForUpdate(Vector3Int chunkPos, bool allowCullingBypass = false)
        {
            if (isRegenerating) return;

            bool chunkExists = chunks.ContainsKey(chunkPos);
            bool bypassCulling = allowCullingBypass && chunkExists;

            if (allowCullingBypass)
            {
                if (chunkExists)
                {
                    chunksPendingVisibilityOverride.Add(chunkPos);
                }
                else
                {
                    chunksPendingVisibilityOverride.Remove(chunkPos);
                }

                hiddenChunkVisibilityCooldowns.Remove(chunkPos);
            }

            bool chunkOnCooldown = false;
            if (enableFrustumCulling && chunkExists && !bypassCulling &&
                hiddenChunkVisibilityCooldowns.TryGetValue(chunkPos, out int nextCheckFrame))
            {
                if (Time.frameCount < nextCheckFrame)
                {
                    chunkOnCooldown = true;
                }
                else
                {
                    hiddenChunkVisibilityCooldowns.Remove(chunkPos);
                }
            }

            bool shouldAttemptEnqueue = chunkExists && (!chunkOnCooldown || bypassCulling);

            if (shouldAttemptEnqueue
                && !dirtyChunkSet.Contains(chunkPos)
                && !runningMeshJobs.ContainsKey(chunkPos)
                && !runningGenJobs.ContainsKey(chunkPos))
            {
                dirtyChunkQueue.Enqueue(chunkPos);
                dirtyChunkSet.Add(chunkPos);
                MarkTransitionsDirtyForChunk(chunkPos);
            }
            else if (chunkExists)
            {
                MarkTransitionsDirtyForChunk(chunkPos);
            }
        }

        public Vector3Int WorldToChunkPosition(Vector3 worldPos)
        {
            Vector3 baseChunkSize = GetActualChunkWorldSizeForLOD(0);
            float sizeX = Mathf.Approximately(baseChunkSize.x, 0f) ? 1f : baseChunkSize.x;
            float sizeY = Mathf.Approximately(baseChunkSize.y, 0f) ? 1f : baseChunkSize.y;
            float sizeZ = Mathf.Approximately(baseChunkSize.z, 0f) ? 1f : baseChunkSize.z;

            return new Vector3Int(
                Mathf.FloorToInt(worldPos.x / sizeX),
                Mathf.FloorToInt(worldPos.y / sizeY),
                Mathf.FloorToInt(worldPos.z / sizeZ)
            );
        }

        public Vector3 ChunkToWorldPosition(Vector3Int chunkPos)
        {
            Vector3 baseChunkSize = GetActualChunkWorldSizeForLOD(0);
            float sizeX = Mathf.Approximately(baseChunkSize.x, 0f) ? 0f : baseChunkSize.x;
            float sizeY = Mathf.Approximately(baseChunkSize.y, 0f) ? 0f : baseChunkSize.y;
            float sizeZ = Mathf.Approximately(baseChunkSize.z, 0f) ? 0f : baseChunkSize.z;
            return new Vector3(
                chunkPos.x * sizeX,
                chunkPos.y * sizeY,
                chunkPos.z * sizeZ
            );
        }

        public (Vector3Int chunkPos, Vector3Int voxelPos) WorldToVoxelPosition(Vector3 worldPos)
        {
            var chunkPos = WorldToChunkPosition(worldPos);
            var chunkWorldOrigin = ChunkToWorldPosition(chunkPos);
            var localPos = worldPos - chunkWorldOrigin;
            
            // Check if this chunk exists and get its LOD level
            float actualVoxelSize = voxelSize;
            if (chunks.TryGetValue(chunkPos, out var chunkData))
            {
                actualVoxelSize = GetVoxelSizeForLOD(chunkData.lodLevel);
            }
            
            return (chunkPos, new Vector3Int(
                Mathf.FloorToInt(localPos.x / actualVoxelSize),
                Mathf.FloorToInt(localPos.y / actualVoxelSize),
                Mathf.FloorToInt(localPos.z / actualVoxelSize)
            ));
        }

        private Vector3Int WorldToVoxelCoordinates(Vector3 worldPos) => new Vector3Int(
            Mathf.FloorToInt(worldPos.x / voxelSize),
            Mathf.FloorToInt(worldPos.y / voxelSize),
            Mathf.FloorToInt(worldPos.z / voxelSize)
        );
        #endregion
        
        #region Debug Visualization
        private void OnDrawGizmos()
        {
            if (Application.isPlaying && showLODLevels && playerTransform != null)
            {
                // Visualize LOD levels if enabled
                foreach (var entry in chunks)
                {
                    Vector3Int chunkPos = entry.Key;
                    ChunkData chunkData = entry.Value;
                    
                    // Only draw if chunk exists
                    if (chunkData.chunk == null) continue;
                    
                    // Choose color based on LOD level
                    Color lodColor = Color.white;
                    switch (chunkData.lodLevel)
                    {
                        case 0: lodColor = Color.green; break;   // Highest detail
                        case 1: lodColor = Color.yellow; break;  // Medium detail
                        case 2: lodColor = Color.red; break;     // Low detail
                        default: lodColor = Color.gray; break;   // Lowest detail
                    }
                    
                    Gizmos.color = lodColor;
                    Vector3 worldPos = ChunkToWorldPosition(chunkPos);
                    Vector3 chunkSize = GetActualChunkWorldSizeForLOD(chunkData.lodLevel);
                    Gizmos.DrawWireCube(worldPos + chunkSize * 0.5f, chunkSize);
                }
                
                // Draw LOD distance thresholds
                if (lodDistanceThresholds != null && lodDistanceThresholds.Length > 0)
                {
                    Gizmos.color = new Color(0, 1, 1, 0.2f); // Cyan with transparency
                    foreach (float distance in lodDistanceThresholds)
                    {
                        Gizmos.DrawWireSphere(playerTransform.position, distance);
                    }
                }
            }
        }
        #endregion
        [Inject]
        public void Construct(
            IComputeBufferPool bufferPool,
            ITerrainLogger logger,
            [InjectOptional] ITerrainSettings settings = null)
        {
            computeBufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
            terrainLogger = logger ?? new UnityTerrainLogger();

            if (settings != null)
            {
                ApplySettings(settings);
            }
        }

        private void ApplySettings(ITerrainSettings settings)
        {
            chunkWorldSize = settings.ChunkWorldSize;
            voxelSize = settings.VoxelSize;
            loadDistance = settings.LoadDistance;
            useLOD = settings.UseLod;

            var thresholds = settings.LodDistanceThresholds;
            if (thresholds != null)
            {
                if (lodDistanceThresholds == null || lodDistanceThresholds.Length != thresholds.Count)
                {
                    lodDistanceThresholds = new float[thresholds.Count];
                }

                for (int i = 0; i < thresholds.Count; i++)
                {
                    lodDistanceThresholds[i] = thresholds[i];
                }
            }

            maxLODLevel = settings.MaxLodLevel;
            transitionLODs = settings.TransitionLods;
            processingMode = settings.ProcessingMode;
            maxChunksPerFrame = settings.MaxChunksPerFrame;
            verticalLoadRadius = settings.VerticalLoadRadius;
            adaptiveChunkBudget = settings.AdaptiveChunkBudget;
            minChunksPerFrame = settings.MinChunksPerFrame;
            maxAdaptiveChunksPerFrame = settings.MaxAdaptiveChunksPerFrame;
            targetFrameRate = settings.TargetFrameRate;
            frameRateBuffer = settings.FrameRateBuffer;
            adaptationInterval = settings.AdaptationInterval;
            frameTimeSmoothing = settings.FrameTimeSmoothing;
        }

        private int ActiveGpuRequestCount =>
            runningGpuGenRequests.Count + runningGpuMeshRequests.Count + runningGpuModificationRequests.Count;

        private bool HasGpuCapacityForRequest()
        {
            int limit = Mathf.Max(1, maxConcurrentGpuReadbacks);
            return ActiveGpuRequestCount < limit;
        }

        private bool TryConsumeGpuGenerationBudget()
        {
            int budget = Mathf.Max(0, maxGpuGenerationsPerFrame);
            if (budget == 0)
            {
                return true;
            }

            int currentFrame = Time.frameCount;
            if (currentFrame != lastGpuGenerationFrame)
            {
                lastGpuGenerationFrame = currentFrame;
                gpuGenerationsThisFrame = 0;
            }

            if (gpuGenerationsThisFrame >= budget)
            {
                return false;
            }

            gpuGenerationsThisFrame++;
            return true;
        }

        private void ReleaseBufferSafe(ComputeBuffer buffer)
        {
            if (buffer == null)
            {
                return;
            }

            if (computeBufferPool != null)
            {
                computeBufferPool.ReleaseBuffer(buffer);
            }
            else
            {
                buffer.Release();
            }
        }
    }

    [System.Serializable]
    public struct BiomeSettings
    {
        public string name;
        [Tooltip("The biome noise value where this biome starts")]
        public float startThreshold;
    
        // Added new fields for noise parameters
        public int octaves;
        public float lacunarity;
        public float persistence;
    
        // Existing fields
        public float worldGroundLevel;
        public float heightImpact;
        public float heightNoiseScale;
        public float caveImpact;
        public float caveNoiseScale;
        public bool enableHeightDomainWarp;
        public float heightWarpStrength;
        public float heightWarpScale;
        public bool heightWarpUseRidged;
        public bool enableSecondaryHeightDomainWarp;
        public float secondaryHeightWarpStrength;
        public float secondaryHeightWarpScale;
        public bool secondaryHeightWarpUseRidged;
        public bool enableCaveDomainWarp;
        public float caveWarpStrength;
        public float caveWarpScale;
        public bool caveWarpUseRidged;
        public bool enableSecondaryCaveDomainWarp;
        public float secondaryCaveWarpStrength;
        public float secondaryCaveWarpScale;
        public bool secondaryCaveWarpUseRidged;
        public bool enableAdditionalHeightLayer;
        public int additionalHeightOctaves;
        public float additionalHeightScale;
        public float additionalHeightLacunarity;
        public float additionalHeightPersistence;
        public float additionalHeightImpact;
        public bool enableAdditionalCaveLayer;
        public int additionalCaveOctaves;
        public float additionalCaveScale;
        public float additionalCaveLacunarity;
        public float additionalCavePersistence;
        public float additionalCaveImpact;
        public bool enableCliffLayer;
        public float cliffNoiseScale;
        public float cliffImpact;
        public float cliffThreshold;
        public float cliffSharpness;
        public bool enablePlateauLayer;
        public float plateauNoiseScale;
        public float plateauStepHeight;
        public float plateauStrength;
        public float plateauThreshold;
        public float plateauSharpness;
        public bool enableSurfaceCaveMask;
        public float surfaceCaveMaskScale;
        public float surfaceCaveMaskThreshold;
        public float surfaceCaveMaskStrength;
        public float surfaceCaveMaskFalloff;
    }
}