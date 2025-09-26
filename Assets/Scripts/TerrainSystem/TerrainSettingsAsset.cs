using System.Collections.Generic;
using UnityEngine;

namespace TerrainSystem
{
    [CreateAssetMenu(fileName = "TerrainSettings", menuName = "Terrain/Terrain Settings")]
    public class TerrainSettingsAsset : ScriptableObject, ITerrainSettings
    {
        [SerializeField] private Vector3 chunkWorldSize = new Vector3(16f, 16f, 16f);
        [SerializeField] private float voxelSize = 1f;
        [SerializeField] private float loadDistance = 64f;
        [SerializeField] private bool useLOD = true;
        [SerializeField] private List<float> lodDistanceThresholds = new List<float> { 32f, 64f, 96f };
        [SerializeField] private int maxLODLevel = 2;
        [SerializeField] private bool transitionLODs = true;
        [SerializeField] private TerrainProcessingMode processingMode = TerrainProcessingMode.Hybrid;
        [SerializeField] private int maxChunksPerFrame = 4;
        [SerializeField] private int verticalLoadRadius = 1;
        [SerializeField] private bool adaptiveChunkBudget = true;
        [SerializeField] private int minChunksPerFrame = 1;
        [SerializeField] private int maxAdaptiveChunksPerFrame = 12;
        [SerializeField] private float targetFrameRate = 60f;
        [SerializeField] private float frameRateBuffer = 5f;
        [SerializeField] private float adaptationInterval = 0.5f;
        [SerializeField] private float frameTimeSmoothing = 0.1f;

        public Vector3 ChunkWorldSize => chunkWorldSize;
        public float VoxelSize => voxelSize;
        public float LoadDistance => loadDistance;
        public bool UseLod => useLOD;
        public IReadOnlyList<float> LodDistanceThresholds => lodDistanceThresholds;
        public int MaxLodLevel => maxLODLevel;
        public bool TransitionLods => transitionLODs;
        public TerrainProcessingMode ProcessingMode => processingMode;
        public int MaxChunksPerFrame => maxChunksPerFrame;
        public int VerticalLoadRadius => verticalLoadRadius;
        public bool AdaptiveChunkBudget => adaptiveChunkBudget;
        public int MinChunksPerFrame => minChunksPerFrame;
        public int MaxAdaptiveChunksPerFrame => maxAdaptiveChunksPerFrame;
        public float TargetFrameRate => targetFrameRate;
        public float FrameRateBuffer => frameRateBuffer;
        public float AdaptationInterval => adaptationInterval;
        public float FrameTimeSmoothing => frameTimeSmoothing;
    }
}