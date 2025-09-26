using UnityEngine;

namespace TerrainSystem
{
    public interface IComputeBufferPool
    {
        ComputeBuffer GetBuffer(int count, int stride, ComputeBufferType type = ComputeBufferType.Default);
        void ReleaseBuffer(ComputeBuffer buffer);
        ComputeBuffer GetStructuredVertexBuffer(Vector3Int resolution, int requiredCount);
        ComputeBuffer GetStructuredNormalBuffer(Vector3Int resolution, int requiredCount);
        ComputeBuffer GetStructuredCounterBuffer(Vector3Int resolution);
        void ResetCounterBuffer(ComputeBuffer counterBuffer);
        void ReleaseStructuredBuffers();
    }
}