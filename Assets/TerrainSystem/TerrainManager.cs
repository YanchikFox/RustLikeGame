using System.Collections.Generic;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace TerrainSystem
{
    public class TerrainManager : MonoBehaviour
    {
        #region Inspector Properties
        [Header("Terrain Settings")]
        [SerializeField] private Vector3 chunkWorldSize = new Vector3(16, 16, 16);
        
        // WARNING: Halving voxelSize will now 8x the number of voxels per chunk,
        // which drastically increases memory usage and processing time.
        // Adjust loadDistance accordingly.
        [SerializeField] private float voxelSize = 1f;
        // Track previous value to detect changes in the Inspector
        private float previousVoxelSize;
        
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
            public NativeArray<float> densities;
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
        private ComputeBuffer biomeThresholdsBuffer;
        private ComputeBuffer biomeGroundLevelsBuffer;
        private ComputeBuffer biomeHeightImpactsBuffer;
        private ComputeBuffer biomeHeightScalesBuffer;
        private ComputeBuffer biomeCaveImpactsBuffer;
        private ComputeBuffer biomeCaveScalesBuffer;
        private ComputeBuffer biomeOctavesBuffer;
        private ComputeBuffer biomeLacunarityBuffer;
        private ComputeBuffer biomePersistenceBuffer;

// Словарь для отслеживания асинхронных запросов к GPU
        private readonly Dictionary<Vector3Int, AsyncGPUReadbackRequest> runningGpuGenRequests = 
            new Dictionary<Vector3Int, AsyncGPUReadbackRequest>();
// Словарь для хранения буфера плотностей во время генерации
        private readonly Dictionary<Vector3Int, ComputeBuffer> densityBuffers = 
            new Dictionary<Vector3Int, ComputeBuffer>();
        private readonly Dictionary<Vector3Int, ChunkData> chunks = new Dictionary<Vector3Int, ChunkData>();
        private readonly Queue<Vector3Int> dirtyChunkQueue = new Queue<Vector3Int>();
        private readonly Dictionary<Vector3Int, MeshJobHandleData> runningMeshJobs = new Dictionary<Vector3Int, MeshJobHandleData>();
        private readonly Dictionary<Vector3Int, VoxelGenJobHandleData> runningGenJobs = new Dictionary<Vector3Int, VoxelGenJobHandleData>();
        
        // Shader property IDs
        private int shaderPropLODLevel;
        #endregion

        #region Unity Lifecycle
        private void Start()
        {
            if (meshGenerator == null || chunkPrefab == null || voxelTerrainShader == null)
            {
                Debug.LogError("TerrainManager is missing required references!", this);
                enabled = false;
                return;
            }
            if (playerTransform == null && Camera.main != null)
            {
                playerTransform = Camera.main.transform;
            }
            
            // Cache shader property IDs
            shaderPropLODLevel = Shader.PropertyToID("_LODLevel");
            
            // Store initial voxelSize value for change detection
            previousVoxelSize = voxelSize;
            
            InitializeStaticGpuBuffers();

            UpdateChunksAroundPlayer();        }

        private void Update()
        {
            UpdateChunksAroundPlayer();
            ProcessDirtyChunks();
        }

private void LateUpdate()
{
    CompleteGenerationJobs(); // Для старой системы на CPU

    // Новый безопасный способ обработки GPU запросов
    if (runningGpuGenRequests.Count > 0)
    {
        // Создаем список для ключей завершенных запросов
        List<Vector3Int> completedRequests = null;

        foreach (var kvp in runningGpuGenRequests)
        {
            var request = kvp.Value;
            // Проверяем, завершен ли запрос
            if (request.done)
            {
                // Если да, обрабатываем его
                if (request.hasError)
                {
                    Debug.LogError($"GPU readback error for chunk {kvp.Key}.");
                }
                else
                {
                    // Получаем данные
                    NativeArray<float> densities = request.GetData<float>();
                    if (chunks.TryGetValue(kvp.Key, out var chunkData))
                    {
                        // Применяем данные к чанку
                        chunkData.chunk.ApplyDensities(densities);
                        // Ставим чанк в очередь на создание меша
                        QueueChunkForUpdate(kvp.Key);
                    }
                }
                
                // Добавляем ключ в список на удаление
                if (completedRequests == null)
                {
                    completedRequests = new List<Vector3Int>();
                }
                completedRequests.Add(kvp.Key);
            }
        }

        // Удаляем все обработанные запросы из словаря ПОСЛЕ цикла
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
        }
        
        // Detect changes in the Inspector during Play Mode
        private void OnValidate()
        {
            // Only process in Play Mode and when value has actually changed
            if (Application.isPlaying && previousVoxelSize != voxelSize && !isRegenerating)
            {
                Debug.Log($"VoxelSize changed from {previousVoxelSize} to {voxelSize}. Regenerating world...");
                RegenerateWorld();
            }
        }
        
        private void InitializeStaticGpuBuffers()
{
    // Извлекаем данные из массива биомов
    var biomeThresholds = new float[biomes.Length];
    var biomeGroundLevels = new float[biomes.Length];
    var biomeHeightImpacts = new float[biomes.Length];
    var biomeHeightScales = new float[biomes.Length];
    var biomeCaveImpacts = new float[biomes.Length];
    var biomeCaveScales = new float[biomes.Length];
    var biomeOctaves = new int[biomes.Length];
    var biomeLacunarity = new float[biomes.Length];
    var biomePersistence = new float[biomes.Length];

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

    // Создаем ComputeBuffer'ы
    biomeThresholdsBuffer = new ComputeBuffer(biomes.Length, sizeof(float));
    biomeGroundLevelsBuffer = new ComputeBuffer(biomes.Length, sizeof(float));
    biomeHeightImpactsBuffer = new ComputeBuffer(biomes.Length, sizeof(float));
    biomeHeightScalesBuffer = new ComputeBuffer(biomes.Length, sizeof(float));
    biomeCaveImpactsBuffer = new ComputeBuffer(biomes.Length, sizeof(float));
    biomeCaveScalesBuffer = new ComputeBuffer(biomes.Length, sizeof(float));
    biomeOctavesBuffer = new ComputeBuffer(biomes.Length, sizeof(int));
    biomeLacunarityBuffer = new ComputeBuffer(biomes.Length, sizeof(float));
    biomePersistenceBuffer = new ComputeBuffer(biomes.Length, sizeof(float));

    // Загружаем данные в буферы
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

private void ReleaseStaticGpuBuffers()
{
    biomeThresholdsBuffer?.Release();
    biomeGroundLevelsBuffer?.Release();
    biomeHeightImpactsBuffer?.Release();
    biomeHeightScalesBuffer?.Release();
    biomeCaveImpactsBuffer?.Release();
    biomeCaveScalesBuffer?.Release();
    biomeOctavesBuffer?.Release();
    biomeLacunarityBuffer?.Release();
    biomePersistenceBuffer?.Release();
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

            StartCoroutine(RegenerateWorldCoroutine());
        }

        private System.Collections.IEnumerator RegenerateWorldCoroutine()
        {
            isRegenerating = true;
            
            // Step 1: Complete all running jobs and dispose resources
            CleanupAllJobs();
            
            // Give a frame to ensure all jobs have completed
            yield return null;
            
            // Step 2: Destroy all existing chunks
            foreach (var chunkData in chunks.Values)
            {
                if (chunkData.chunk != null)
                {
                    Destroy(chunkData.chunk.gameObject);
                }
            }
            
            // Step 3: Clear all internal state
            chunks.Clear();
            dirtyChunkQueue.Clear();
            
            // Step 4: Store the current voxelSize value
            previousVoxelSize = voxelSize;
            
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
            float distance = Vector3.Distance(playerTransform.position, chunkWorldPos + chunkWorldSize * 0.5f);
            
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
        #endregion

        #region Chunk Management
        private void UpdateChunksAroundPlayer()
        {
            if (playerTransform == null || isRegenerating) return;

            Vector3Int playerChunkPos = WorldToChunkPosition(playerTransform.position);
            int loadRadius = Mathf.CeilToInt(loadDistance / chunkWorldSize.x);

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
                    Destroy(chunkData.chunk.gameObject);
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
                
                if (!chunks.ContainsKey(chunkPos))
                {
                    // Create new chunk
                    CreateChunk(chunkPos, lodLevel);
                }
                else if (chunks[chunkPos].lodLevel != lodLevel)
                {
                    // LOD level has changed, recreate the chunk
                    TerrainChunk oldChunk = chunks[chunkPos].chunk;
                    Destroy(oldChunk.gameObject);
                    chunks.Remove(chunkPos);
                    CreateChunk(chunkPos, lodLevel);
                }
            }
        }

        private void CreateChunk(Vector3Int position, int lodLevel)
        {
            Vector3 worldPos = ChunkToWorldPosition(position);
            GameObject chunkObject = Instantiate(chunkPrefab, worldPos, Quaternion.identity, transform);
            
            // Name the chunk to include LOD level
            chunkObject.name = $"Chunk_{position.x}_{position.y}_{position.z}_LOD{lodLevel}";
            
            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);
            
            var chunk = chunkObject.GetComponent<TerrainChunk>();
            chunk.Initialize(position, voxelDimensions, adjustedVoxelSize, worldPos, lodLevel);
            
            // Store chunk with LOD level
            chunks.Add(position, new ChunkData { 
                chunk = chunk, 
                lodLevel = lodLevel 
            });

            // Schedule voxel data generation in background or on GPU
            if (voxelTerrainShader != null)
            {
                ScheduleGenerateVoxelDataGPU(chunk, lodLevel);
            }
            else
            {
                ScheduleGenerateVoxelDataJob(chunk, lodLevel);
            }
        }

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
        
// Замените существующий метод ScheduleGenerateVoxelDataGPU этим кодом
private void ScheduleGenerateVoxelDataGPU(TerrainChunk chunk, int lodLevel)
{
    // Не запускаем новую генерацию, если она уже идет для этого чанка
    if (densityBuffers.ContainsKey(chunk.ChunkPosition) || runningGpuGenRequests.ContainsKey(chunk.ChunkPosition)) return;

    // 1. Находим ядро (Kernel) в шейдере
    int kernel = voxelTerrainShader.FindKernel("GenerateVoxelData");

    // 2. Получаем размеры для этого LOD
    Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
    int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);

    // 3. Берем буфер из пула
    ComputeBuffer densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));
    densityBuffers.Add(chunk.ChunkPosition, densityBuffer); // Сохраняем для будущего использования

    // 4. Передаем все параметры в шейдер
    voxelTerrainShader.SetBuffer(kernel, "_DensityValues", densityBuffer);
    
    // Передаем параметры чанка
    voxelTerrainShader.SetVector("_ChunkWorldOrigin", chunk.WorldPosition);
    voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
    voxelTerrainShader.SetFloat("_VoxelSize", voxelSize); // Базовый размер
    voxelTerrainShader.SetInt("_LODLevel", lodLevel);     // Уровень LOD
    
    // Передаем параметры биомов (из статических буферов)
    voxelTerrainShader.SetFloat("_BiomeNoiseScale", biomeNoiseScale);
    voxelTerrainShader.SetInt("_BiomeCount", biomes.Length);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeThresholds", biomeThresholdsBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeGroundLevels", biomeGroundLevelsBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightImpacts", biomeHeightImpactsBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeHeightScales", biomeHeightScalesBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveImpacts", biomeCaveImpactsBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeCaveScales", biomeCaveScalesBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeOctaves", biomeOctavesBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomeLacunarity", biomeLacunarityBuffer);
    voxelTerrainShader.SetBuffer(kernel, "_BiomePersistence", biomePersistenceBuffer);
    
    // Передаем параметры дополнительного шума
    voxelTerrainShader.SetFloat("_TemperatureNoiseScale", temperatureNoiseScale);
    voxelTerrainShader.SetFloat("_HumidityNoiseScale", humidityNoiseScale);
    voxelTerrainShader.SetFloat("_RiverNoiseScale", riverNoiseScale);
    voxelTerrainShader.SetFloat("_RiverThreshold", riverThreshold);
    voxelTerrainShader.SetFloat("_RiverDepth", riverDepth);
    
    // 5. Вычисляем количество групп потоков и запускаем шейдер
    int threadGroupsX = Mathf.CeilToInt((voxelDimensions.x + 1) / 8.0f);
    int threadGroupsY = Mathf.CeilToInt((voxelDimensions.y + 1) / 8.0f);
    int threadGroupsZ = Mathf.CeilToInt((voxelDimensions.z + 1) / 8.0f);
    voxelTerrainShader.Dispatch(kernel, threadGroupsX, threadGroupsY, threadGroupsZ);

    // 6. Запрашиваем данные обратно с GPU асинхронно
    var request = AsyncGPUReadback.Request(densityBuffer);
    runningGpuGenRequests.Add(chunk.ChunkPosition, request);
}

// --- Добавьте этот новый метод в TerrainManager.cs ---
private void OnVoxelDataReceived(AsyncGPUReadbackRequest request)
{
    if (request.hasError)
    {
        Debug.LogError("GPU readback error.");
        return;
    }

    // Находим, какому чанку принадлежат эти данные
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
        
        // Получаем данные
        NativeArray<float> densities = request.GetData<float>();
        
        if (chunks.TryGetValue(pos, out var chunkData))
        {
            // Применяем данные к чанку
            chunkData.chunk.ApplyDensities(densities);
            
            // Ставим чанк в очередь на создание меша (пока что через CPU Job)
            QueueChunkForUpdate(pos);
        }
        
        // Освобождаем буфер плотностей обратно в пул
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
            if (voxelTerrainShader == null) return;
            
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
            
            int processedCount = 0;
            while (processedCount < maxChunksPerFrame && dirtyChunkQueue.Count > 0)
            {
                Vector3Int chunkPos = dirtyChunkQueue.Dequeue();

                // Skip if voxel generation is still running for this chunk
                if (runningGenJobs.ContainsKey(chunkPos))
                    continue;

                if (chunks.TryGetValue(chunkPos, out var chunkData) && !runningMeshJobs.ContainsKey(chunkPos))
                {
                    ScheduleMarchingCubesJob(chunkData.chunk, chunkData.lodLevel);
                    processedCount++;
                }
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

            var job = new MarchingCubesJob
            {
                densities = densitiesForJob,
                vertices = new NativeList<Vector3>(Allocator.Persistent),
                triangles = new NativeList<int>(Allocator.Persistent),
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
                densities = job.densities
            };
        }
        
        private void DispatchMarchingCubesGPU(TerrainChunk chunk, int lodLevel)
        {
            if (voxelTerrainShader == null) return;
            
            // Get kernel index
            int kernelIndex = voxelTerrainShader.FindKernel("MarchingCubes");
            
            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            float adjustedVoxelSize = GetVoxelSizeForLOD(lodLevel);
            
            // Create or get buffers from pool
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);
            ComputeBuffer densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));
            
            // Copy density values to buffer
            float[] densities = new float[voxelCount];
            chunk.CopyVoxelDataTo(densities);
            densityBuffer.SetData(densities);
            
            // Create output buffers
            int maxVertexCount = voxelCount * 3; // Worst case: every voxel produces a triangle (3 vertices)
            ComputeBuffer vertexBuffer = ComputeBufferManager.Instance.GetBuffer(maxVertexCount, 3 * sizeof(float), ComputeBufferType.Append);
            ComputeBuffer counterBuffer = ComputeBufferManager.Instance.GetBuffer(1, sizeof(uint));
            
            // Reset append buffer counter
            uint[] zeros = { 0 };
            counterBuffer.SetData(zeros);
            vertexBuffer.SetCounterValue(0);
            
            // Set shader parameters
            voxelTerrainShader.SetBuffer(kernelIndex, "_DensitiesForMC", densityBuffer);
            voxelTerrainShader.SetBuffer(kernelIndex, "_VertexBuffer", vertexBuffer);
            voxelTerrainShader.SetBuffer(kernelIndex, "_VertexCount", counterBuffer);
            voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
            voxelTerrainShader.SetFloat("_VoxelSize", voxelSize); // Base voxel size
            voxelTerrainShader.SetInt("_LODLevel", lodLevel);     // LOD level for adjustment in shader
            voxelTerrainShader.SetFloat("_SurfaceLevel", meshGenerator.surfaceLevel);
            
            // Dispatch the shader
            int threadGroupsX = Mathf.CeilToInt(voxelDimensions.x / 8.0f);
            int threadGroupsY = Mathf.CeilToInt(voxelDimensions.y / 8.0f);
            int threadGroupsZ = Mathf.CeilToInt(voxelDimensions.z / 8.0f);
            voxelTerrainShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
            
            // Get vertex count
            uint[] countData = new uint[1];
            ComputeBuffer.CopyCount(vertexBuffer, counterBuffer, 0);
            counterBuffer.GetData(countData);
            int vertexCount = (int)countData[0];
            
            if (vertexCount > 0)
            {
                // Get generated vertices
                Vector3[] vertices = new Vector3[vertexCount];
                vertexBuffer.GetData(vertices, 0, 0, vertexCount);
                
                // Create triangles (assuming vertices are already in triangle order)
                int[] triangles = new int[vertexCount];
                for (int i = 0; i < vertexCount; i++)
                {
                    triangles[i] = i;
                }
                
                // Create and apply mesh
                Mesh mesh = new Mesh();
                mesh.SetVertices(vertices);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                
                chunk.ApplyMesh(mesh);
            }
            
            // Release buffers back to the pool
            ComputeBufferManager.Instance.ReleaseBuffer(densityBuffer);
            ComputeBufferManager.Instance.ReleaseBuffer(vertexBuffer);
            ComputeBufferManager.Instance.ReleaseBuffer(counterBuffer);
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
                    if (!runningMeshJobs.ContainsKey(pos) && !isRegenerating)
                    {
                        ScheduleMarchingCubesJob(chunkData.chunk, chunkData.lodLevel);
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
                        Mesh mesh = meshGenerator.CreateMeshFromJob(jobEntry.Value.vertices, jobEntry.Value.triangles);
                        chunkData.chunk.ApplyMesh(mesh);
                        chunkData.chunk.IsDirty = false;
                    }

                    jobEntry.Value.vertices.Dispose();
                    jobEntry.Value.triangles.Dispose();
                    if (jobEntry.Value.densities.IsCreated) jobEntry.Value.densities.Dispose();
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

            var modifiedChunks = new HashSet<Vector3Int>();

            for (int x = min.x; x <= max.x; x++)
            for (int y = min.y; y <= max.y; y++)
            for (int z = min.z; z <= max.z; z++)
            {
                Vector3 voxelWorldPos = new Vector3(x, y, z) * voxelSize + Vector3.one * (voxelSize * 0.5f);
                float sqrDist = (voxelWorldPos - worldPosition).sqrMagnitude;
                if (sqrDist > radius * radius) continue;

                var (chunkPos, voxelPos) = WorldToVoxelPosition(voxelWorldPos);
                if (chunks.TryGetValue(chunkPos, out var chunkData))
                {
                    // Get the chunk's LOD level
                    int lodLevel = chunkData.lodLevel;
                    
                    // For higher LOD levels, modifications affect a wider area
                    float lodAdjustedStrength = strength;
                    
                    // Apply strength adjustment for LOD to maintain consistent modification size
                    if (lodLevel > 0)
                    {
                        lodAdjustedStrength *= Mathf.Pow(2, lodLevel);
                    }
                    
                    VoxelData currentVoxel = chunkData.chunk.GetVoxel(voxelPos.x, voxelPos.y, voxelPos.z);
                    float modificationAmount = lodAdjustedStrength * (1f - Mathf.Sqrt(sqrDist) / radius);
                    float newDensity = Mathf.Clamp(currentVoxel.density - modificationAmount, -1f, 1f);

                    if (chunkData.chunk.SetVoxel(voxelPos.x, voxelPos.y, voxelPos.z, new VoxelData(newDensity)))
                    {
                        modifiedChunks.Add(chunkPos);
                    }
                }
            }

            // If using GPU modification, dispatch the ModifyDensity kernel
            if (voxelTerrainShader != null)
            {
                foreach (var chunkPos in modifiedChunks)
                {
                    if (chunks.TryGetValue(chunkPos, out var chunkData))
                    {
                        DispatchModifyDensityGPU(chunkData.chunk, chunkData.lodLevel, worldPosition, radius, strength);
                    }
                }
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
        
        private void DispatchModifyDensityGPU(TerrainChunk chunk, int lodLevel, Vector3 modificationPosition, float radius, float strength)
        {
            if (voxelTerrainShader == null) return;
            
            // Get kernel index
            int kernelIndex = voxelTerrainShader.FindKernel("ModifyDensity");
            
            // Get the actual voxel dimensions for this LOD level
            Vector3Int voxelDimensions = GetChunkVoxelDimensionsForLOD(lodLevel);
            
            // Create or get buffer for densities from pool
            int voxelCount = (voxelDimensions.x + 1) * (voxelDimensions.y + 1) * (voxelDimensions.z + 1);
            ComputeBuffer densityBuffer = ComputeBufferManager.Instance.GetBuffer(voxelCount, sizeof(float));
            
            // Copy current densities to buffer
            float[] densities = new float[voxelCount];
            chunk.CopyVoxelDataTo(densities);
            densityBuffer.SetData(densities);
            
            // Set shader parameters
            voxelTerrainShader.SetBuffer(kernelIndex, "_ModifiedDensities", densityBuffer);
            voxelTerrainShader.SetVector("_ChunkWorldOrigin", chunk.WorldPosition);
            voxelTerrainShader.SetInts("_ChunkSize", voxelDimensions.x, voxelDimensions.y, voxelDimensions.z);
            voxelTerrainShader.SetFloat("_VoxelSize", voxelSize); // Base voxel size
            voxelTerrainShader.SetInt("_LODLevel", lodLevel);     // LOD level for adjustment in shader
            voxelTerrainShader.SetVector("_ModificationPosition", modificationPosition);
            voxelTerrainShader.SetFloat("_ModificationRadius", radius);
            voxelTerrainShader.SetFloat("_ModificationStrength", strength);
            
            // Dispatch the shader
            int threadGroupsX = Mathf.CeilToInt((voxelDimensions.x + 1) / 8.0f);
            int threadGroupsY = Mathf.CeilToInt((voxelDimensions.y + 1) / 8.0f);
            int threadGroupsZ = Mathf.CeilToInt((voxelDimensions.z + 1) / 8.0f);
            voxelTerrainShader.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
            
            // Get modified densities back
            densityBuffer.GetData(densities);
            
            // Apply modified densities to chunk
            chunk.ApplyDensities(densities);
            
            // Release the buffer back to the pool
            ComputeBufferManager.Instance.ReleaseBuffer(densityBuffer);
        }
        #endregion

        #region Utility Methods
        public void QueueChunkForUpdate(Vector3Int chunkPos)
        {
            if (isRegenerating) return;
            
            if (chunks.ContainsKey(chunkPos)
                && !dirtyChunkQueue.Contains(chunkPos)
                && !runningMeshJobs.ContainsKey(chunkPos)
                && !runningGenJobs.ContainsKey(chunkPos))
            {
                dirtyChunkQueue.Enqueue(chunkPos);
            }
        }

        public Vector3Int WorldToChunkPosition(Vector3 worldPos) => new Vector3Int(
            Mathf.FloorToInt(worldPos.x / chunkWorldSize.x),
            Mathf.FloorToInt(worldPos.y / chunkWorldSize.y),
            Mathf.FloorToInt(worldPos.z / chunkWorldSize.z)
        );

        public Vector3 ChunkToWorldPosition(Vector3Int chunkPos) => new Vector3(
            chunkPos.x * chunkWorldSize.x,
            chunkPos.y * chunkWorldSize.y,
            chunkPos.z * chunkWorldSize.z
        );

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
                    Gizmos.DrawWireCube(worldPos + chunkWorldSize * 0.5f, chunkWorldSize);
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