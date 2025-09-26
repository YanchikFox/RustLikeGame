using System.Collections.Generic;
using UnityEngine;

namespace TerrainSystem
{
    public interface ITerrainSettings
    {
        Vector3 ChunkWorldSize { get; }
        float VoxelSize { get; }
        float LoadDistance { get; }
        bool UseLod { get; }
        IReadOnlyList<float> LodDistanceThresholds { get; }
        int MaxLodLevel { get; }
        bool TransitionLods { get; }
        TerrainProcessingMode ProcessingMode { get; }
        int MaxChunksPerFrame { get; }
        int VerticalLoadRadius { get; }
        bool AdaptiveChunkBudget { get; }
        int MinChunksPerFrame { get; }
        int MaxAdaptiveChunksPerFrame { get; }
        float TargetFrameRate { get; }
        float FrameRateBuffer { get; }
        float AdaptationInterval { get; }
        float FrameTimeSmoothing { get; }
    }
}