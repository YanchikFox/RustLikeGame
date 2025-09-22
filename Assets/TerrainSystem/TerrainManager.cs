using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

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
        [SerializeField] private BiomeSettings[] biomes;

        [Header("Advanced Biome Noise")]
        [SerializeField] private float temperatureNoiseScale = 0.002f;
        [SerializeField] private float humidityNoiseScale = 0.003f;
        [SerializeField] private float riverNoiseScale = 0.008f;
        [SerializeField] private float riverThreshold = 0.02f;
        [SerializeField] private float riverDepth = 5.0f;

        [Header("Performance")]
        [SerializeField] private int maxChunksPerFrame = 4;
        [SerializeField] private int verticalLoadRadius = 1;

        [Header("Culling Settings")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private bool enableFrustumCulling = true;
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
        #endregion

        #region Private Fields
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
        private ComputeBuffer triangleTableBuffer;
        private ComputeBuffer edgeConnectionsBuffer;
        private bool gpuBuffersInitialized;

        private TerrainProcessingMode runtimeProcessingMode = TerrainProcessingMode.CPU;
        private bool hasLoggedModeFallback;

// Ñëîâàðü äëÿ îòñëåæèâàíèÿ àñèíõðîííûõ çàïðîñîâ ê GPU
        private readonly Dictionary<Vector3Int, AsyncGPUReadbackRequest> runningGpuGenRequests = 
            new Dictionary<Vector3Int, AsyncGPUReadbackRequest>();
// Ñëîâàðü äëÿ õðàíåíèÿ áóôåðà ïëîòíîñòåé âî âðåìÿ ãåíåðàöèè
        private readonly Dictionary<Vector3Int, ComputeBuffer> densityBuffers = 
            new Dictionary<Vector3Int, ComputeBuffer>();
        private readonly Dictionary<Vector3Int, ChunkData> chunks = new Dictionary<Vector3Int, ChunkData>();
        private readonly Queue<Vector3Int> dirtyChunkQueue = new Queue<Vector3Int>();
        private readonly Dictionary<Vector3Int, MeshJobHandleData> runningMeshJobs = new Dictionary<Vector3Int, MeshJobHandleData>();
        private readonly Dictionary<Vector3Int, VoxelGenJobHandleData> runningGenJobs = new Dictionary<Vector3Int, VoxelGenJobHandleData>();

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
                    Debug.LogWarning("Compute shader is not available. Falling back to CPU terrain processing mode.", this);
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

        private void Start()
        {
            if (meshGenerator == null || chunkPrefab == null)
            {
                Debug.LogError("TerrainManager is missing required references!", this);
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

            UpdateChunksAroundPlayer();
        }

        private void Update()
        {
            RefreshRuntimeProcessingMode(false);
            HandleGeometrySettingsChangeIfNeeded();
            UpdateChunksAroundPlayer();
            UpdateAdaptiveChunkBudget();
            ProcessDirtyChunks();
        }

private void LateUpdate()
{
    CompleteGenerationJobs(); // Äëÿ ñòàðîé ñèñòåìû íà CPU

    // Íîâûé áåçîïàñíûé ñïîñîá îáðàáîòêè GPU çàïðîñîâ
    if (runningGpuGenRequests.Count > 0)
    {
        // Ñîçäàåì ñïèñîê äëÿ êëþ÷åé çàâåðøåííûõ çàïðîñîâ
        List<Vector3Int> completedRequests = null;

        foreach (var kvp in runningGpuGenRequests)
        {
            var request = kvp.Value;
            // Ïðîâåðÿåì, çàâåðøåí ëè çàïðîñ
            if (request.done)
            {
                // Åñëè äà, îáðàáàòûâàåì åãî
                if (request.hasError)
                {
                    Debug.LogError($"GPU readback error for chunk {kvp.Key}.");
                }
                else
                {
                    // Ïîëó÷àåì äàííûå
                    NativeArray<float> densities = request.GetData<float>();
                    if (chunks.TryGetValue(kvp.Key, out var chunkData))
                    {
                        // Ïðèìåíÿåì äàííûå ê ÷àíêó
                        chunkData.chunk.ApplyDensities(densities);
                        // Ñòàâèì ÷àíê â î÷åðåäü íà ñîçäàíèå ìåøà
                        QueueChunkForUpdate(kvp.Key);
                    }
                }
                
                // Äîáàâëÿåì êëþ÷ â ñïèñîê íà óäàëåíèå
                if (completedRequests == null)
                {
                    completedRequests = new List<Vector3Int>();
                }
                completedRequests.Add(kvp.Key);
            }
        }

        // Óäàëÿåì âñå îáðàáîòàííûå çàïðîñû èç ñëîâàðÿ ÏÎÑËÅ öèêëà
        if (completedRequests != null)
        {
            foreach (var pos in completedRequests)
            {
                if (densityBuffers.TryGetValue(pos, out ComputeBuffer buffer))
                {
                    ComputeBufferManager.Instance.ReleaseBuffer(buffer);
                    densityBuffers.Remove(pos);
                }
                runningGpuGenRequests.Remove(pos);
            }
        }
    }

    CompleteRunningMeshJobs();
}

        private void OnDestroy()
        {
            CleanupAllJobs();
            ReleaseStaticGpuBuffers();

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
        }
        
        private void InitializeStaticGpuBuffers()
        {
            if (gpuBuffersInitialized || voxelTerrainShader == null || meshGenerator == null)
            {
                return;
            }

            int biomeCount = biomes != null ? biomes.Length : 0;
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

                for (int i = 0; i < biomeCount; i++)
                {
                    biomeThresholds[i] = biomes[i].startThreshold;
                    biomeGroundLevels[i] = biomes[i].worldGroundLevel;
                    biomeHeightImpacts[i] = biomes[i].heightImpact;
                    biomeHeightScales[i] = biomes[i].heightNoiseScale;
                    biomeCaveImpacts[i] = biomes[i].caveImpact;
                    biomeCaveScales[i] = biomes[i].caveNoiseScale;
                    biomeOctaves[i] = biomes[i].octaves;
                    biomeLacunarity[i] = biomes[i].lacunarity;
                    biomePersistence[i] = biomes[i].persistence;
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

                biomeThresholdsBuffer.SetData(biomeThresholds);
                biomeGroundLevelsBuffer.SetData(biomeGroundLevels);
                biomeHeightImpactsBuffer.SetData(biomeHeightImpacts);
                biomeHeightScalesBuffer.SetData(biomeHeightScales);
                biomeCaveImpactsBuffer.SetData(biomeCaveImpacts);
                biomeCaveScalesBuffer.SetData(biomeCaveScales);
                biomeOctavesBuffer.SetData(biomeOctaves);
                biomeLacunarityBuffer.SetData(biomeLacunarity);
                biomePersistenceBuffer.SetData(biomePersistence);
            }

            triangleTableBuffer = new ComputeBuffer(meshGenerator.NativeTriangleTable.Length, sizeof(int));
            triangleTableBuffer.SetData(meshGenerator.NativeTriangleTable);
            edgeConnectionsBuffer = new ComputeBuffer(meshGenerator.NativeEdgeConnections.Length, sizeof(int));
            edgeConnectionsBuffer.SetData(meshGenerator.NativeEdgeConnections);

            gpuBuffersInitialized = true;
        }

        private void ReleaseStaticGpuBuffers()
        {
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
            triangleTableBuffer?.Release();
            triangleTableBuffer = null;
            edgeConnectionsBuffer?.Release();
            edgeConnectionsBuffer = null;

            gpuBuffersInitialized = false;
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
                Debug.LogWarning("World regeneration already in progress!");
                return;
            }

            isRegenerating = true;
            StartCoroutine(RegenerateWorldCoroutine());
        }

        private System.Collections.IEnumerator RegenerateWorldCoroutine()
        {
            InvalidateChunkWorldSizeCache();

            // Step 1: Complete all running jobs and dispose resources
            CleanupAllJobs();
            
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
            
            // Step 4: Store the current settings values
            previousVoxelSize = voxelSize;
            previousChunkWorldSize = chunkWorldSize;

            // Step 5: Start regenerating the world
            Debug.Log("World regeneration: Starting new terrain generation...");
            UpdateChunksAroundPlayer();
            
            isRegenerating = false;
        }

        /// <summary>
        /// Completes all running jobs and disposes of their resources safely.
        /// </summary>
        private void CleanupAllJobs()
        {
            Debug.Log("World regeneration: Cleaning up jobs...");
            
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

            ClearTransitionMeshes();

            Debug.Log("World regeneration: All jobs completed and resources disposed.");
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
            float distance = Vector3.Distance(playerTransform.position, chunkWorldPos + baseChunkSize * 0.5f);
            
            // Determine LOD level based on distance
            for (int i = 0; i < lodDistanceThresholds.Length; i++)
            {
                if (distance <= lodDistanceThresholds[i])
                    return i;
            }
            
            // If beyond all thresholds, return the maximum LOD level
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

            // Step 1: Identify chunks to unload (too far from player)
            var chunksToUnload = new List<Vector3Int>();
            foreach (var chunkPos in chunks.Keys)
            {
                if (Vector3Int.Distance(chunkPos, playerChunkPos) > loadRadius + 2)
                    chunksToUnload.Add(chunkPos);
            }
            
            // Step 2: Unload distant chunks
            foreach (var pos in chunksToUnload)
            {
                if (chunks.TryGetValue(pos, out var chunkData))
                {
                    RemoveTransitionsForChunk(pos);
                    CancelChunkProcessing(pos);
                    ReturnChunkToPool(chunkData.chunk);
                    chunks.Remove(pos);
                }
            }

            // Step 3: Load or update chunks within load radius
            for (int x = -loadRadius; x <= loadRadius; x++)
            for (int z = -loadRadius; z <= loadRadius; z++)
            for (int y = -verticalLoadRadius; y <= verticalLoadRadius; y++)
            {
                Vector3Int chunkPos = playerChunkPos + new Vector3Int(x, y, z);

                // Calculate LOD level based on distance
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

            Vector3 worldPos = ChunkToWorldPosition(position);
            TerrainChunk chunk = GetChunkFromPool(worldPos);
            GameObject chunkObject = chunk.gameObject;
            chunkObject.name = $"Chunk_{position.x}_{position.y}_{position.z}_LOD{lodLevel}";

            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);

            chunk.Initialize(position, voxelDimensions, adjustedVoxelSize, worldPos, lodLevel);

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

        private TerrainChunk GetChunkFromPool(Vector3 worldPosition)
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

            if (runningGpuGenRequests.ContainsKey(chunkPos))
            {
                runningGpuGenRequests.Remove(chunkPos);
            }

            if (densityBuffers.TryGetValue(chunkPos, out var buffer))
            {
                ComputeBufferManager.Instance.ReleaseBuffer(buffer);
                densityBuffers.Remove(chunkPos);
            }

            if (dirtyChunkQueue.Contains(chunkPos))
            {
                int count = dirtyChunkQueue.Count;
                var filteredQueue = new Queue<Vector3Int>(count);
                while (dirtyChunkQueue.Count > 0)
                {
                    Vector3Int dequeued = dirtyChunkQueue.Dequeue();
                    if (dequeued != chunkPos)
                    {
                        filteredQueue.Enqueue(dequeued);
                    }
                }

                while (filteredQueue.Count > 0)
                {
                    dirtyChunkQueue.Enqueue(filteredQueue.Dequeue());
                }
            }
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
                    Dirty = true
                };

                transitionMeshes[key] = data;
            }
            else
            {
                data.Direction = direction;

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
            }

            if (!transitionLODs)
            {
                return;
            }

            if (data.Dirty)
            {
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

            // Create NativeArrays for biome data
            var biomeThresholds = new NativeArray<float>(biomes.Length, Allocator.TempJob);
            var biomeGroundLevels = new NativeArray<float>(biomes.Length, Allocator.TempJob);
            var biomeHeightImpacts = new NativeArray<float>(biomes.Length, Allocator.TempJob);
            var biomeHeightScales = new NativeArray<float>(biomes.Length, Allocator.TempJob);
            var biomeCaveImpacts = new NativeArray<float>(biomes.Length, Allocator.TempJob);
            var biomeCaveScales = new NativeArray<float>(biomes.Length, Allocator.TempJob);
            
            // Biome-specific noise parameters
            var biomeOctaves = new NativeArray<int>(biomes.Length, Allocator.TempJob);
            var biomeLacunarity = new NativeArray<float>(biomes.Length, Allocator.TempJob);
            var biomePersistence = new NativeArray<float>(biomes.Length, Allocator.TempJob);

            // Fill the biome data arrays
            for (int i = 0; i < biomes.Length; i++)
            {
                biomeThresholds[i] = biomes[i].startThreshold;
                biomeGroundLevels[i] = biomes[i].worldGroundLevel;
                biomeHeightImpacts[i] = biomes[i].heightImpact;
                biomeHeightScales[i] = biomes[i].heightNoiseScale;
                biomeCaveImpacts[i] = biomes[i].caveImpact;
                biomeCaveScales[i] = biomes[i].caveNoiseScale;
                
                biomeOctaves[i] = biomes[i].octaves;
                biomeLacunarity[i] = biomes[i].lacunarity;
                biomePersistence[i] = biomes[i].persistence;
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
                biomeCount = biomes.Length,
                biomeThresholds = biomeThresholds,
                biomeGroundLevels = biomeGroundLevels,
                biomeHeightImpacts = biomeHeightImpacts,
                biomeHeightScales = biomeHeightScales,
                biomeCaveImpacts = biomeCaveImpacts,
                biomeCaveScales = biomeCaveScales,
                
                biomeOctaves = biomeOctaves,
                biomeLacunarity = biomeLacunarity,
                biomePersistence = biomePersistence,
                
                temperatureNoiseScale = temperatureNoiseScale,
                humidityNoiseScale = humidityNoiseScale,
                riverNoiseScale = riverNoiseScale,
                riverThreshold = riverThreshold,
                riverDepth = riverDepth
            };

            var handle = job.Schedule();
            
            // Add dependencies for proper disposal
            handle = JobHandle.CombineDependencies(
                handle, 
                biomeThresholds.Dispose(handle),
                biomeGroundLevels.Dispose(handle)
            );
            handle = JobHandle.CombineDependencies(
                handle, 
                biomeHeightImpacts.Dispose(handle),
                biomeHeightScales.Dispose(handle)
            );
            handle = JobHandle.CombineDependencies(
                handle, 
                biomeCaveImpacts.Dispose(handle),
                biomeCaveScales.Dispose(handle)
            );
            
            handle = JobHandle.CombineDependencies(
                handle, 
                biomeOctaves.Dispose(handle),
                biomeLacunarity.Dispose(handle)
            );
            handle = JobHandle.CombineDependencies(
                handle,
                biomePersistence.Dispose(handle)
            );

            runningGenJobs[chunk.ChunkPosition] = new VoxelGenJobHandleData
            {
                handle = handle,
                densities = densities
            };
        }
        
// Çàìåíèòå ñóùåñòâóþùèé ìåòîä ScheduleGenerateVoxelDataGPU ýòèì êîäîì
private void ScheduleGenerateVoxelDataGPU(TerrainChunk chunk, int lodLevel)
{
    if (!ShouldUseGpuForVoxelGeneration || voxelTerrainShader == null || chunk == null)
    {
        ScheduleGenerateVoxelDataJob(chunk, lodLevel);
        return;
    }

    // Íå çàïóñêàåì íîâóþ ãåíåðàöèþ, åñëè îíà óæå èäåò äëÿ ýòîãî ÷àíêà
    if (densityBuffers.ContainsKey(chunk.ChunkPosition) || runningGpuGenRequests.ContainsKey(chunk.ChunkPosition)) return;

    // 1. Íàõîäèì ÿäðî (Kernel) â øåéäåðå
    int kernel = voxelTerrainShader.FindKernel("GenerateVoxelData");

    // 2. Ïîëó÷àåì ðàçìåðû äëÿ ýòîãî LOD
    Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
    int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

    // 3. Áåðåì áóôåð èç ïóëà
    ComputeBuffer densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));
    densityBuffers.Add(chunk.ChunkPosition, densityBuffer); // Ñîõðàíÿåì äëÿ áóäóùåãî èñïîëüçîâàíèÿ

    // 4. Ïåðåäàåì âñå ïàðàìåòðû â øåéäåð
    voxelTerrainShader.SetBuffer(kernel, "_DensityValues", densityBuffer);
    
    // Ïåðåäàåì ïàðàìåòðû ÷àíêà
    voxelTerrainShader.SetVector("_ChunkWorldOrigin", chunk.WorldPosition);
    voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
    voxelTerrainShader.SetFloat("_VoxelSize", voxelSize); // Áàçîâûé ðàçìåð
    voxelTerrainShader.SetInt("_LODLevel", lodLevel);     // Óðîâåíü LOD
    
    // Ïåðåäàåì ïàðàìåòðû áèîìîâ (èç ñòàòè÷åñêèõ áóôåðîâ)
    int biomeCount = biomes != null ? biomes.Length : 0;
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
    }
    
    // Ïåðåäàåì ïàðàìåòðû äîïîëíèòåëüíîãî øóìà
    voxelTerrainShader.SetFloat("_TemperatureNoiseScale", temperatureNoiseScale);
    voxelTerrainShader.SetFloat("_HumidityNoiseScale", humidityNoiseScale);
    voxelTerrainShader.SetFloat("_RiverNoiseScale", riverNoiseScale);
    voxelTerrainShader.SetFloat("_RiverThreshold", riverThreshold);
    voxelTerrainShader.SetFloat("_RiverDepth", riverDepth);
    
    // 5. Âû÷èñëÿåì êîëè÷åñòâî ãðóïï ïîòîêîâ è çàïóñêàåì øåéäåð
    int threadGroupsX = Mathf.CeilToInt((voxelDimensions.x + 1) / 8.0f);
    int threadGroupsY = Mathf.CeilToInt((voxelDimensions.y + 1) / 8.0f);
    int threadGroupsZ = Mathf.CeilToInt((voxelDimensions.z + 1) / 8.0f);
    voxelTerrainShader.Dispatch(kernel, threadGroupsX, threadGroupsY, threadGroupsZ);

    // 6. Çàïðàøèâàåì äàííûå îáðàòíî ñ GPU àñèíõðîííî
    var request = AsyncGPUReadback.Request(densityBuffer);
    runningGpuGenRequests.Add(chunk.ChunkPosition, request);
}

// --- Äîáàâüòå ýòîò íîâûé ìåòîä â TerrainManager.cs ---
private void OnVoxelDataReceived(AsyncGPUReadbackRequest request)
{
    if (request.hasError)
    {
        Debug.LogError("GPU readback error.");
        return;
    }

    // Íàõîäèì, êàêîìó ÷àíêó ïðèíàäëåæàò ýòè äàííûå
    Vector3Int? chunkPos = null;
    foreach (var pair in runningGpuGenRequests)
    {
        if (pair.Value.Equals(request))
        {
            chunkPos = pair.Key;
            break;
        }
    }

    if (chunkPos.HasValue)
    {
        Vector3Int pos = chunkPos.Value;
        
        // Ïîëó÷àåì äàííûå
        NativeArray<float> densities = request.GetData<float>();
        
        if (chunks.TryGetValue(pos, out var chunkData))
        {
            // Ïðèìåíÿåì äàííûå ê ÷àíêó
            chunkData.chunk.ApplyDensities(densities);
            
            // Ñòàâèì ÷àíê â î÷åðåäü íà ñîçäàíèå ìåøà (ïîêà ÷òî ÷åðåç CPU Job)
            QueueChunkForUpdate(pos);
        }
        
        // Îñâîáîæäàåì áóôåð ïëîòíîñòåé îáðàòíî â ïóë
        if (densityBuffers.TryGetValue(pos, out ComputeBuffer buffer))
        {
            ComputeBufferManager.Instance.ReleaseBuffer(buffer);
            densityBuffers.Remove(pos);
        }
        
        runningGpuGenRequests.Remove(pos);
    }
}
        
        private void DispatchGenerateVoxelDataGPU(TerrainChunk chunk, int lodLevel)
        {
            if (!ShouldUseGpuForVoxelGeneration || voxelTerrainShader == null || chunk == null) return;
            
            // Get kernel index
            int kernelIndex = voxelTerrainShader.FindKernel("GenerateVoxelData");
            
            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);
            
            // Create or get buffer for densities from pool
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);
            ComputeBuffer densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));
            
            // Set shader parameters
            voxelTerrainShader.SetBuffer(kernelIndex, "_DensityValues", densityBuffer);
            voxelTerrainShader.SetVector("_ChunkWorldOrigin", chunk.WorldPosition);
            voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
            voxelTerrainShader.SetFloat("_VoxelSize", voxelSize); // Base voxel size
            voxelTerrainShader.SetInt("_LODLevel", lodLevel);     // LOD level for adjustment in shader
            
            // Set other terrain generation parameters...
            
            // Dispatch the shader
            int threadGroupsX = Mathf.CeilToInt((voxelDimensions.x + 1) / 8.0f);
            int threadGroupsY = Mathf.CeilToInt((voxelDimensions.y + 1) / 8.0f);
            int threadGroupsZ = Mathf.CeilToInt((voxelDimensions.z + 1) / 8.0f);
            voxelTerrainShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
            
            // Get results and apply to chunk
            float[] densities = new float[voxelCount];
            densityBuffer.GetData(densities);
            
            // Convert to VoxelData and apply to chunk
            // ...
            
            // Release the buffer back to the pool
            ComputeBufferManager.Instance.ReleaseBuffer(densityBuffer);
        }
        #endregion

        #region Job-Based Mesh Generation
        private void ProcessDirtyChunks()
        {
            if (isRegenerating) return;

            int chunkBudget = Mathf.Max(0, maxChunksPerFrame);
            if (chunkBudget <= 0)
            {
                return;
            }

            int processedCount = 0;
            List<Vector3Int> skippedChunks = null;

            while (processedCount < chunkBudget && dirtyChunkQueue.Count > 0)
            {
                Vector3Int chunkPos = dirtyChunkQueue.Dequeue();

                // Skip if voxel generation is still running for this chunk
                if (runningGenJobs.ContainsKey(chunkPos))
                    continue;

                if (!chunks.TryGetValue(chunkPos, out var chunkData) || chunkData.chunk == null || runningMeshJobs.ContainsKey(chunkPos))
                {
                    continue;
                }

                if (enableFrustumCulling && !IsChunkVisible(chunkPos, chunkData.lodLevel))
                {
                    skippedChunks ??= new List<Vector3Int>();
                    skippedChunks.Add(chunkPos);
                    continue;
                }

                bool processed = false;

                if (ShouldUseGpuForMeshGeneration)
                {
                    processed = DispatchMarchingCubesGPU(chunkData.chunk, chunkData.lodLevel);
                }

                if (!processed)
                {
                    ScheduleMarchingCubesJob(chunkData.chunk, chunkData.lodLevel);
                }

                processedCount++;
            }

            if (skippedChunks != null)
            {
                for (int i = 0; i < skippedChunks.Count; i++)
                {
                    dirtyChunkQueue.Enqueue(skippedChunks[i]);
                }
            }
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

            ComputeBuffer densityBuffer = null;
            ComputeBuffer vertexBuffer = null;
            ComputeBuffer normalBuffer = null;
            ComputeBuffer counterBuffer = null;

            try
            {
                int kernelIndex = voxelTerrainShader.FindKernel("MarchingCubes");

                Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
                int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

                densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));

                float[] densities = new float[voxelCount];
                chunk.CopyVoxelDataTo(densities);
                densityBuffer.SetData(densities);

                int maxVertexCount = voxelCount * 3;
                vertexBuffer = ComputeBufferManager.Instance.GetBuffer(maxVertexCount, 3 * sizeof(float), ComputeBufferType.Append);
                normalBuffer = ComputeBufferManager.Instance.GetBuffer(maxVertexCount, 3 * sizeof(float), ComputeBufferType.Append);
                counterBuffer = ComputeBufferManager.Instance.GetBuffer(1, sizeof(uint));

                uint[] zeros = { 0 };
                counterBuffer.SetData(zeros);
                vertexBuffer.SetCounterValue(0);
                normalBuffer.SetCounterValue(0);

                voxelTerrainShader.SetBuffer(kernelIndex, "_DensitiesForMC", densityBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_VertexBuffer", vertexBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_NormalBuffer", normalBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_VertexCountBuffer", counterBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_TriangleTable", triangleTableBuffer);
                voxelTerrainShader.SetBuffer(kernelIndex, "_EdgeConnections", edgeConnectionsBuffer);
                voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
                voxelTerrainShader.SetFloat("_VoxelSize", voxelSize);
                voxelTerrainShader.SetInt("_LODLevel", lodLevel);
                voxelTerrainShader.SetFloat("_SurfaceLevel", meshGenerator.surfaceLevel);

                int threadGroupsX = Mathf.CeilToInt(Mathf.Max(1f, voxelDimensions.x) / 8.0f);
                int threadGroupsY = Mathf.CeilToInt(Mathf.Max(1f, voxelDimensions.y) / 8.0f);
                int threadGroupsZ = Mathf.CeilToInt(Mathf.Max(1f, voxelDimensions.z) / 8.0f);
                voxelTerrainShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);

                uint[] countData = new uint[1];
                ComputeBuffer.CopyCount(vertexBuffer, counterBuffer, 0);
                counterBuffer.GetData(countData);
                int vertexCount = (int)countData[0];

                Mesh mesh;
                if (vertexCount > 0)
                {
                    Vector3[] vertices = new Vector3[vertexCount];
                    Vector3[] normals = new Vector3[vertexCount];
                    vertexBuffer.GetData(vertices, 0, 0, vertexCount);
                    normalBuffer.GetData(normals, 0, 0, vertexCount);

                    int[] triangles = new int[vertexCount];
                    for (int i = 0; i < vertexCount; i++)
                    {
                        triangles[i] = i;
                    }

                    mesh = meshGenerator.CreateMeshFromArrays(vertices, triangles, normals);
                }
                else
                {
                    mesh = new Mesh();
                }

                ApplyMesh(chunk, mesh);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"GPU marching cubes failed for chunk {chunk?.ChunkPosition}: {ex.Message}. Falling back to CPU.", this);
                return false;
            }
            finally
            {
                ComputeBufferManager.Instance.ReleaseBuffer(densityBuffer);
                ComputeBufferManager.Instance.ReleaseBuffer(vertexBuffer);
                ComputeBufferManager.Instance.ReleaseBuffer(normalBuffer);
                ComputeBufferManager.Instance.ReleaseBuffer(counterBuffer);
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

        private void CompleteGenerationJobs()
        {
            if (runningGenJobs.Count == 0 || isRegenerating) return;

            var completed = new List<Vector3Int>();
            foreach (var kvp in runningGenJobs)
            {
                var pos = kvp.Key;
                var data = kvp.Value;

                if (!data.handle.IsCompleted) continue;

                data.handle.Complete();

                if (chunks.TryGetValue(pos, out var chunkData))
                {
                    chunkData.chunk.ApplyDensities(data.densities);
                    if (!isRegenerating)
                    {
                        QueueChunkForUpdate(pos);
                    }
                }

                if (data.densities.IsCreated) data.densities.Dispose();
                completed.Add(pos);
            }

            foreach (var pos in completed) runningGenJobs.Remove(pos);
        }

        private void CompleteRunningMeshJobs()
        {
            if (runningMeshJobs.Count == 0 || isRegenerating) return;

            var completedJobs = new List<Vector3Int>();
            foreach (var jobEntry in runningMeshJobs)
            {
                if (jobEntry.Value.handle.IsCompleted)
                {
                    jobEntry.Value.handle.Complete();
                    if (chunks.TryGetValue(jobEntry.Key, out var chunkData))
                    {
                        Mesh mesh = meshGenerator.CreateMeshFromJob(jobEntry.Value.vertices, jobEntry.Value.triangles, jobEntry.Value.normals);
                        ApplyMesh(chunkData.chunk, mesh);
                    }

                    jobEntry.Value.vertices.Dispose();
                    jobEntry.Value.triangles.Dispose();
                    jobEntry.Value.normals.Dispose();
                    if (jobEntry.Value.densities.IsCreated) jobEntry.Value.densities.Dispose();
                    if (jobEntry.Value.gradientDensities.IsCreated) jobEntry.Value.gradientDensities.Dispose();
                    completedJobs.Add(jobEntry.Key);
                }
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
                    else
                    {
                        modifiedChunks.Add(chunkPos);
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

            if (chunk.DensityVersion != expectedVersion)
            {
                versionMismatch = true;
                return false;
            }

            int kernelIndex = voxelTerrainShader.FindKernel("ModifyDensity");
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

            ComputeBuffer densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));

            try
            {
                float[] densities = new float[voxelCount];
                chunk.CopyVoxelDataTo(densities);

                if (chunk.DensityVersion != expectedVersion)
                {
                    versionMismatch = true;
                    return false;
                }

                densityBuffer.SetData(densities);

                voxelTerrainShader.SetBuffer(kernelIndex, "_ModifiedDensities", densityBuffer);
                voxelTerrainShader.SetVector("_ChunkWorldOrigin", chunk.WorldPosition);
                voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
                voxelTerrainShader.SetFloat("_VoxelSize", voxelSize);
                voxelTerrainShader.SetInt("_LODLevel", lodLevel);
                voxelTerrainShader.SetVector("_ModificationPosition", modificationPosition);
                voxelTerrainShader.SetFloat("_ModificationRadius", radius);
                voxelTerrainShader.SetFloat("_ModificationStrength", lodAdjustedStrength);

                int threadGroupsX = Mathf.CeilToInt((voxelDimensions.x + 1) / 8.0f);
                int threadGroupsY = Mathf.CeilToInt((voxelDimensions.y + 1) / 8.0f);
                int threadGroupsZ = Mathf.CeilToInt((voxelDimensions.z + 1) / 8.0f);
                voxelTerrainShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);

                densityBuffer.GetData(densities);

                if (chunk.DensityVersion != expectedVersion)
                {
                    versionMismatch = true;
                    return false;
                }

                chunk.ApplyDensities(densities);
                return true;
            }
            finally
            {
                ComputeBufferManager.Instance.ReleaseBuffer(densityBuffer);
            }
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

                Debug.Log($"Terrain settings changed ({changeDescription}). Regenerating world...");
                InvalidateChunkWorldSizeCache();
                RegenerateWorld();
            }
        }

        public void QueueChunkForUpdate(Vector3Int chunkPos)
        {
            if (isRegenerating) return;

            if (chunks.ContainsKey(chunkPos)
                && !dirtyChunkQueue.Contains(chunkPos)
                && !runningMeshJobs.ContainsKey(chunkPos)
                && !runningGenJobs.ContainsKey(chunkPos))
            {
                dirtyChunkQueue.Enqueue(chunkPos);
                MarkTransitionsDirtyForChunk(chunkPos);
            }
            else if (chunks.ContainsKey(chunkPos))
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
    }
}