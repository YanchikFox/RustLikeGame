using UnityEngine;
using System;
using System.Runtime.CompilerServices;
using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace TerrainSystem
{
    [BurstCompile]
    public struct MarchingCubesJob : IJob
    {
        [ReadOnly] public NativeArray<float> densities;
        [ReadOnly] public NativeArray<float> gradientDensities;
        public NativeList<Vector3> vertices;
        public NativeList<int> triangles;
        public NativeList<Vector3> normals;
        [ReadOnly] public NativeArray<int> triangleTable;
        [ReadOnly] public NativeArray<int> edgeConnections;
        [ReadOnly] public Vector3Int chunkSize;
        [ReadOnly] public float surfaceLevel;
        [ReadOnly] public float voxelSize;

        public void Execute()
        {
            var edgeVertexIndices = new NativeArray<int>(12, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var cellDensities = new NativeArray<float>(8, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var cellGradients = new NativeArray<Vector3>(8, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int x = 0; x < chunkSize.x; x++)
            {
                for (int y = 0; y < chunkSize.y; y++)
                {
                    for (int z = 0; z < chunkSize.z; z++)
                    {
                        ProcessCell(x, y, z, ref edgeVertexIndices, ref cellDensities, ref cellGradients);
                    }
                }
            }

            cellGradients.Dispose();
            cellDensities.Dispose();
            edgeVertexIndices.Dispose();
        }

        private void ProcessCell(int x, int y, int z, ref NativeArray<int> edgeVertexIndices, ref NativeArray<float> cellDensities, ref NativeArray<Vector3> cellGradients)
        {
            int cubeIndex = 0;

            for (int i = 0; i < 8; i++)
            {
                Vector3Int corner = new Vector3Int(x, y, z) + MarchingCubesMeshGenerator.cubeCorners[i];
                float density = densities[GetDensityIndex(corner.x, corner.y, corner.z)];
                cellDensities[i] = density;
                cellGradients[i] = CalculateDensityGradient(corner.x, corner.y, corner.z);
                if (density < surfaceLevel)
                {
                    cubeIndex |= (1 << i);
                }
            }

            if (cubeIndex == 0 || cubeIndex == 255)
            {
                return;
            }

            // Reset cached edge vertices
            for (int i = 0; i < 12; i++)
            {
                edgeVertexIndices[i] = -1;
            }

            for (int i = 0; i < 16 && triangleTable[cubeIndex * 16 + i] != -1; i += 3)
            {
                int edge1 = triangleTable[cubeIndex * 16 + i];
                int edge2 = triangleTable[cubeIndex * 16 + i + 1];
                int edge3 = triangleTable[cubeIndex * 16 + i + 2];

                int vert1 = GetOrCreateVertex(x, y, z, edge1, ref edgeVertexIndices, ref cellDensities, ref cellGradients);
                int vert2 = GetOrCreateVertex(x, y, z, edge2, ref edgeVertexIndices, ref cellDensities, ref cellGradients);
                int vert3 = GetOrCreateVertex(x, y, z, edge3, ref edgeVertexIndices, ref cellDensities, ref cellGradients);

                triangles.Add(vert1);
                triangles.Add(vert2);
                triangles.Add(vert3);
            }
        }

        private int GetOrCreateVertex(int x, int y, int z, int edgeIndex, ref NativeArray<int> edgeVertexIndices, ref NativeArray<float> cellDensities, ref NativeArray<Vector3> cellGradients)
        {
            if (edgeVertexIndices[edgeIndex] != -1)
            {
                return edgeVertexIndices[edgeIndex];
            }

            int cornerIdx1 = edgeConnections[edgeIndex * 2 + 0];
            int cornerIdx2 = edgeConnections[edgeIndex * 2 + 1];

            Vector3 cornerPos1 = (Vector3)(new Vector3Int(x, y, z) + MarchingCubesMeshGenerator.cubeCorners[cornerIdx1]);
            Vector3 cornerPos2 = (Vector3)(new Vector3Int(x, y, z) + MarchingCubesMeshGenerator.cubeCorners[cornerIdx2]);

            float density1 = cellDensities[cornerIdx1];
            float density2 = cellDensities[cornerIdx2];

            float t = 0.5f;
            if (Mathf.Abs(density1 - density2) > 0.00001f)
            {
                t = (surfaceLevel - density1) / (density2 - density1);
            }
            Vector3 vertexPosition = Vector3.Lerp(cornerPos1, cornerPos2, t) * voxelSize;

            Vector3 gradient1 = cellGradients[cornerIdx1];
            Vector3 gradient2 = cellGradients[cornerIdx2];
            Vector3 interpolatedGradient = Vector3.Lerp(gradient1, gradient2, t);
            Vector3 normal = interpolatedGradient.sqrMagnitude > 1e-12f
                ? interpolatedGradient.normalized
                : Vector3.up;

            int newIndex = vertices.Length;
            vertices.Add(vertexPosition);
            normals.Add(normal);
            edgeVertexIndices[edgeIndex] = newIndex;

            return newIndex;
        }

        private Vector3 CalculateDensityGradient(int x, int y, int z)
        {
            float dx = GetGradientDensity(x + 1, y, z) - GetGradientDensity(x - 1, y, z);
            float dy = GetGradientDensity(x, y + 1, z) - GetGradientDensity(x, y - 1, z);
            float dz = GetGradientDensity(x, y, z + 1) - GetGradientDensity(x, y, z - 1);

            const float half = 0.5f;
            return new Vector3(dx * half, dy * half, dz * half);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetDensityIndex(int x, int y, int z)
        {
            int width = chunkSize.x + 1;
            int height = chunkSize.y + 1;
            return x + y * width + z * width * height;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetGradientDensityIndex(int x, int y, int z)
        {
            int width = chunkSize.x + 3;
            int height = chunkSize.y + 3;
            return (x + 1) + (y + 1) * width + (z + 1) * width * height;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float GetGradientDensity(int x, int y, int z)
        {
            return gradientDensities[GetGradientDensityIndex(x, y, z)];
        }
    }



    public class MarchingCubesMeshGenerator : MonoBehaviour
    {        public readonly struct DensitySampler
    {
        public readonly struct Source
        {
            public readonly TerrainChunk Chunk;
            public readonly Vector3Int OriginVoxel;
            public readonly Vector3Int VoxelDimensions;
            public readonly int LodLevel;

            public Source(TerrainChunk chunk, Vector3Int originVoxel, Vector3Int voxelDimensions, int lodLevel)
            {
                Chunk = chunk;
                OriginVoxel = originVoxel;
                VoxelDimensions = voxelDimensions;
                LodLevel = lodLevel;
            }

            public bool TrySample(Vector3Int worldVoxel, out float density)
            {
                density = 0f;

                var chunk = Chunk;
                if (chunk == null)
                {
                    return false;
                }

                int step = 1 << LodLevel;
                Vector3Int relative = worldVoxel - OriginVoxel;

                if (relative.x < 0 || relative.y < 0 || relative.z < 0)
                {
                    return false;
                }

                int maxX = VoxelDimensions.x * step;
                int maxY = VoxelDimensions.y * step;
                int maxZ = VoxelDimensions.z * step;
                if (relative.x > maxX || relative.y > maxY || relative.z > maxZ)
                {
                    return false;
                }

                if ((relative.x % step) != 0 || (relative.y % step) != 0 || (relative.z % step) != 0)
                {
                    return false;
                }

                int ix = relative.x / step;
                int iy = relative.y / step;
                int iz = relative.z / step;

                if (ix < 0 || iy < 0 || iz < 0
                    || ix > VoxelDimensions.x
                    || iy > VoxelDimensions.y
                    || iz > VoxelDimensions.z)
                {
                    return false;
                }

                density = chunk.GetVoxel(ix, iy, iz).density;
                return true;
            }
        }

        private readonly Source[] sources;

        public DensitySampler(Source[] sources)
        {
            this.sources = sources;
        }

        public bool TrySample(int lodLevel, Vector3Int worldVoxel, out float density)
        {
            density = 0f;

            if (sources == null)
            {
                return false;
            }

            for (int i = 0; i < sources.Length; i++)
            {
                Source source = sources[i];
                if (source.LodLevel != lodLevel)
                {
                    continue;
                }

                if (source.TrySample(worldVoxel, out density))
                {
                    return true;
                }
            }

            return false;
        }
    }
        [Header("Mesh Generation Settings")]
        [SerializeField] private bool calculateTangents = true;

        [Header("Performance Settings")]
        [Range(0f, 1f)]
        [Tooltip("Surface level threshold. Values below this are solid, above are air")]
        [SerializeField]
        private float _surfaceLevel = 0.0f;
        public float surfaceLevel => _surfaceLevel;

        [Header("Transition Settings")]
        [Tooltip("World-space depth of the seam skirt extruded from high-detail chunk edges when stitching to lower LODs.")]
        [Min(0f)]
        [SerializeField]
        private float transitionSkirtDepth = 0.25f;
        public float TransitionSkirtDepth => Mathf.Max(0f, transitionSkirtDepth);

        private NativeArray<int> nativeTriangleTable;
        private NativeArray<int> nativeEdgeConnections;

        // ????????? ????????, ????? TerrainManager ??? ???????? ? ??? ??????
        public NativeArray<int> NativeTriangleTable => nativeTriangleTable;
        public NativeArray<int> NativeEdgeConnections => nativeEdgeConnections;

        #region Marching Cubes Tables (Static data accessible by jobs)

        private static readonly int[] flatEdgeConnections =
        {
            0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7
        };

        public static readonly Vector3Int[] cubeCorners = new Vector3Int[8]
        {
            new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(1, 0, 1), new Vector3Int(0, 0, 1),
            new Vector3Int(0, 1, 0), new Vector3Int(1, 1, 0), new Vector3Int(1, 1, 1), new Vector3Int(0, 1, 1)
        };

        private static readonly int[] flatTriangleTable =
{
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,8,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,1,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,8,3,9,8,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,2,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,8,3,1,2,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    9,2,10,0,2,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    2,8,3,2,10,8,10,9,8,-1,-1,-1,-1,-1,-1,-1,
    3,11,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,11,2,8,11,0,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,9,0,2,3,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,11,2,1,9,11,9,8,11,-1,-1,-1,-1,-1,-1,-1,
    3,10,1,11,10,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,10,1,0,8,10,8,11,10,-1,-1,-1,-1,-1,-1,-1,
    3,9,0,3,11,9,11,10,9,-1,-1,-1,-1,-1,-1,-1,
    9,8,10,10,8,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,7,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,3,0,7,3,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,1,9,8,4,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,1,9,4,7,1,7,3,1,-1,-1,-1,-1,-1,-1,-1,
    1,2,10,8,4,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    3,4,7,3,0,4,1,2,10,-1,-1,-1,-1,-1,-1,-1,
    9,2,10,9,0,2,8,4,7,-1,-1,-1,-1,-1,-1,-1,
    2,10,9,2,9,7,2,7,3,7,9,4,-1,-1,-1,-1,
    8,4,7,3,11,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    11,4,7,11,2,4,2,0,4,-1,-1,-1,-1,-1,-1,-1,
    9,0,1,8,4,7,2,3,11,-1,-1,-1,-1,-1,-1,-1,
    4,7,11,9,4,11,9,11,2,9,2,1,-1,-1,-1,-1,
    3,10,1,3,11,10,7,8,4,-1,-1,-1,-1,-1,-1,-1,
    1,11,10,1,4,11,1,0,4,7,11,4,-1,-1,-1,-1,
    4,7,8,9,0,11,9,11,10,11,0,3,-1,-1,-1,-1,
    4,7,11,4,11,9,9,11,10,-1,-1,-1,-1,-1,-1,-1,
    9,5,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    9,5,4,0,8,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,5,4,1,5,0,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    8,5,4,8,3,5,3,1,5,-1,-1,-1,-1,-1,-1,-1,
    1,2,10,9,5,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    3,0,8,1,2,10,4,9,5,-1,-1,-1,-1,-1,-1,-1,
    5,2,10,5,4,2,4,0,2,-1,-1,-1,-1,-1,-1,-1,
    2,10,5,3,2,5,3,5,4,3,4,8,-1,-1,-1,-1,
    9,5,4,2,3,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,11,2,0,8,11,4,9,5,-1,-1,-1,-1,-1,-1,-1,
    0,5,4,0,1,5,2,3,11,-1,-1,-1,-1,-1,-1,-1,
    2,1,5,2,5,8,2,8,11,4,8,5,-1,-1,-1,-1,
    10,3,11,10,1,3,9,5,4,-1,-1,-1,-1,-1,-1,-1,
    4,9,5,0,8,1,8,10,1,8,11,10,-1,-1,-1,-1,
    5,4,0,5,0,11,5,11,10,11,0,3,-1,-1,-1,-1,
    5,4,8,5,8,10,10,8,11,-1,-1,-1,-1,-1,-1,-1,
    9,7,8,5,7,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    9,3,0,9,5,3,5,7,3,-1,-1,-1,-1,-1,-1,-1,
    0,7,8,0,1,7,1,5,7,-1,-1,-1,-1,-1,-1,-1,
    1,5,3,3,5,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    9,7,8,9,5,7,10,1,2,-1,-1,-1,-1,-1,-1,-1,
    10,1,2,9,5,0,5,3,0,5,7,3,-1,-1,-1,-1,
    8,0,2,8,2,5,8,5,7,10,5,2,-1,-1,-1,-1,
    2,10,5,2,5,3,3,5,7,-1,-1,-1,-1,-1,-1,-1,
    7,9,5,7,8,9,3,11,2,-1,-1,-1,-1,-1,-1,-1,
    9,5,7,9,7,2,9,2,0,2,7,11,-1,-1,-1,-1,
    2,3,11,0,1,8,1,7,8,1,5,7,-1,-1,-1,-1,
    11,2,1,11,1,7,7,1,5,-1,-1,-1,-1,-1,-1,-1,
    9,5,8,8,5,7,10,1,3,10,3,11,-1,-1,-1,-1,
    5,7,0,5,0,9,7,11,0,1,0,10,11,10,0,-1,
    11,10,0,11,0,3,10,5,0,8,0,7,5,7,0,-1,
    11,10,5,7,11,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    10,6,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,8,3,5,10,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    9,0,1,5,10,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,8,3,1,9,8,5,10,6,-1,-1,-1,-1,-1,-1,-1,
    1,6,5,2,6,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,6,5,1,2,6,3,0,8,-1,-1,-1,-1,-1,-1,-1,
    9,6,5,9,0,6,0,2,6,-1,-1,-1,-1,-1,-1,-1,
    5,9,8,5,8,2,5,2,6,3,2,8,-1,-1,-1,-1,
    2,3,11,10,6,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    11,0,8,11,2,0,10,6,5,-1,-1,-1,-1,-1,-1,-1,
    0,1,9,2,3,11,5,10,6,-1,-1,-1,-1,-1,-1,-1,
    5,10,6,1,9,2,9,11,2,9,8,11,-1,-1,-1,-1,
    6,3,11,6,5,3,5,1,3,-1,-1,-1,-1,-1,-1,-1,
    0,8,11,0,11,5,0,5,1,5,11,6,-1,-1,-1,-1,
    3,11,6,0,3,6,0,6,5,0,5,9,-1,-1,-1,-1,
    6,5,9,6,9,11,11,9,8,-1,-1,-1,-1,-1,-1,-1,
    5,10,6,4,7,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,3,0,4,7,3,6,5,10,-1,-1,-1,-1,-1,-1,-1,
    1,9,0,5,10,6,8,4,7,-1,-1,-1,-1,-1,-1,-1,
    10,6,5,1,9,7,1,7,3,7,9,4,-1,-1,-1,-1,
    6,1,2,6,5,1,4,7,8,-1,-1,-1,-1,-1,-1,-1,
    1,2,5,5,2,6,3,0,4,3,4,7,-1,-1,-1,-1,
    8,4,7,9,0,5,0,6,5,0,2,6,-1,-1,-1,-1,
    7,3,9,7,9,4,3,2,9,5,9,6,2,6,9,-1,
    3,11,2,7,8,4,10,6,5,-1,-1,-1,-1,-1,-1,-1,
    5,10,6,4,7,2,4,2,0,2,7,11,-1,-1,-1,-1,
    0,1,9,4,7,8,2,3,11,5,10,6,-1,-1,-1,-1,
    9,2,1,9,11,2,9,4,11,7,11,4,5,10,6,-1,
    8,4,7,3,11,5,3,5,1,5,11,6,-1,-1,-1,-1,
    5,1,11,5,11,6,1,0,11,7,11,4,0,4,11,-1,
    0,5,9,0,6,5,0,3,6,11,6,3,8,4,7,-1,
    6,5,9,6,9,11,4,7,9,7,11,9,-1,-1,-1,-1,
    10,4,9,6,4,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,10,6,4,9,10,0,8,3,-1,-1,-1,-1,-1,-1,-1,
    10,0,1,10,6,0,6,4,0,-1,-1,-1,-1,-1,-1,-1,
    8,3,1,8,1,6,8,6,4,6,1,10,-1,-1,-1,-1,
    1,4,9,1,2,4,2,6,4,-1,-1,-1,-1,-1,-1,-1,
    3,0,8,1,2,9,2,4,9,2,6,4,-1,-1,-1,-1,
    0,2,4,4,2,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    8,3,2,8,2,4,4,2,6,-1,-1,-1,-1,-1,-1,-1,
    10,4,9,10,6,4,11,2,3,-1,-1,-1,-1,-1,-1,-1,
    0,8,2,2,8,11,4,9,10,4,10,6,-1,-1,-1,-1,
    3,11,2,0,1,6,0,6,4,6,1,10,-1,-1,-1,-1,
    6,4,1,6,1,10,4,8,1,2,1,11,8,11,1,-1,
    9,6,4,9,3,6,9,1,3,11,6,3,-1,-1,-1,-1,
    8,11,1,8,1,0,11,6,1,9,1,4,6,4,1,-1,
    3,11,6,3,6,0,0,6,4,-1,-1,-1,-1,-1,-1,-1,
    6,4,8,11,6,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    7,10,6,7,8,10,8,9,10,-1,-1,-1,-1,-1,-1,-1,
    0,7,3,0,10,7,0,9,10,6,7,10,-1,-1,-1,-1,
    10,6,7,1,10,7,1,7,8,1,8,0,-1,-1,-1,-1,
    10,6,7,10,7,1,1,7,3,-1,-1,-1,-1,-1,-1,-1,
    1,2,6,1,6,8,1,8,9,8,6,7,-1,-1,-1,-1,
    2,6,9,2,9,1,6,7,9,0,9,3,7,3,9,-1,
    7,8,0,7,0,6,6,0,2,-1,-1,-1,-1,-1,-1,-1,
    7,3,2,6,7,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    2,3,11,10,6,8,10,8,9,8,6,7,-1,-1,-1,-1,
    2,0,7,2,7,11,0,9,7,6,7,10,9,10,7,-1,
    1,8,0,1,7,8,1,10,7,6,7,10,2,3,11,-1,
    11,2,1,11,1,7,10,6,1,6,7,1,-1,-1,-1,-1,
    8,9,6,8,6,7,9,1,6,11,6,3,1,3,6,-1,
    0,9,1,11,6,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    7,8,0,7,0,6,3,11,0,11,6,0,-1,-1,-1,-1,
    7,11,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    7,6,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    3,0,8,11,7,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,1,9,11,7,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    8,1,9,8,3,1,11,7,6,-1,-1,-1,-1,-1,-1,-1,
    10,1,2,6,11,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,2,10,3,0,8,6,11,7,-1,-1,-1,-1,-1,-1,-1,
    2,9,0,2,10,9,6,11,7,-1,-1,-1,-1,-1,-1,-1,
    6,11,7,2,10,3,10,8,3,10,9,8,-1,-1,-1,-1,
    7,2,3,6,2,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    7,0,8,7,6,0,6,2,0,-1,-1,-1,-1,-1,-1,-1,
    2,7,6,2,3,7,0,1,9,-1,-1,-1,-1,-1,-1,-1,
    1,6,2,1,8,6,1,9,8,8,7,6,-1,-1,-1,-1,
    10,7,6,10,1,7,1,3,7,-1,-1,-1,-1,-1,-1,-1,
    10,7,6,1,7,10,1,8,7,1,0,8,-1,-1,-1,-1,
    0,3,7,0,7,10,0,10,9,6,10,7,-1,-1,-1,-1,
    7,6,10,7,10,8,8,10,9,-1,-1,-1,-1,-1,-1,-1,
    6,8,4,11,8,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    3,6,11,3,0,6,0,4,6,-1,-1,-1,-1,-1,-1,-1,
    8,6,11,8,4,6,9,0,1,-1,-1,-1,-1,-1,-1,-1,
    9,4,6,9,6,3,9,3,1,11,3,6,-1,-1,-1,-1,
    6,8,4,6,11,8,2,10,1,-1,-1,-1,-1,-1,-1,-1,
    1,2,10,3,0,11,0,6,11,0,4,6,-1,-1,-1,-1,
    4,11,8,4,6,11,0,2,9,2,10,9,-1,-1,-1,-1,
    10,9,3,10,3,2,9,4,3,11,3,6,4,6,3,-1,
    8,2,3,8,4,2,4,6,2,-1,-1,-1,-1,-1,-1,-1,
    0,4,2,4,6,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,9,0,2,3,4,2,4,6,4,3,8,-1,-1,-1,-1,
    1,9,4,1,4,2,2,4,6,-1,-1,-1,-1,-1,-1,-1,
    8,1,3,8,6,1,8,4,6,6,10,1,-1,-1,-1,-1,
    10,1,0,10,0,6,6,0,4,-1,-1,-1,-1,-1,-1,-1,
    4,6,3,4,3,8,6,10,3,0,3,9,10,9,3,-1,
    10,9,4,6,10,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,9,5,7,6,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,8,3,4,9,5,11,7,6,-1,-1,-1,-1,-1,-1,-1,
    5,0,1,5,4,0,7,6,11,-1,-1,-1,-1,-1,-1,-1,
    11,7,6,8,3,4,3,5,4,3,1,5,-1,-1,-1,-1,
    9,5,4,10,1,2,7,6,11,-1,-1,-1,-1,-1,-1,-1,
    6,11,7,1,2,10,0,8,3,4,9,5,-1,-1,-1,-1,
    7,6,11,5,4,10,4,2,10,4,0,2,-1,-1,-1,-1,
    3,4,8,3,5,4,3,2,5,10,5,2,11,7,6,-1,
    7,2,3,7,6,2,5,4,9,-1,-1,-1,-1,-1,-1,-1,
    9,5,4,0,8,6,0,6,2,6,8,7,-1,-1,-1,-1,
    3,6,2,3,7,6,1,5,0,5,4,0,-1,-1,-1,-1,
    6,2,8,6,8,7,2,1,8,4,8,5,1,5,8,-1,
    9,5,4,10,1,6,1,7,6,1,3,7,-1,-1,-1,-1,
    1,6,10,1,7,6,1,0,7,8,7,0,9,5,4,-1,
    4,0,10,4,10,5,0,3,10,6,10,7,3,7,10,-1,
    7,6,10,7,10,8,5,4,10,4,8,10,-1,-1,-1,-1,
    6,9,5,6,11,9,11,8,9,-1,-1,-1,-1,-1,-1,-1,
    3,6,11,0,6,3,0,5,6,0,9,5,-1,-1,-1,-1,
    0,11,8,0,5,11,0,1,5,5,6,11,-1,-1,-1,-1,
    6,11,3,6,3,5,5,3,1,-1,-1,-1,-1,-1,-1,-1,
    1,2,10,9,5,11,9,11,8,11,5,6,-1,-1,-1,-1,
    0,11,3,0,6,11,0,9,6,5,6,9,1,2,10,-1,
    11,8,5,11,5,6,8,0,5,10,5,2,0,2,5,-1,
    6,11,3,6,3,5,2,10,3,10,5,3,-1,-1,-1,-1,
    5,8,9,5,2,8,5,6,2,3,8,2,-1,-1,-1,-1,
    9,5,6,9,6,0,0,6,2,-1,-1,-1,-1,-1,-1,-1,
    1,5,8,1,8,0,5,6,8,3,8,2,6,2,8,-1,
    1,5,6,2,1,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,3,6,1,6,10,3,8,6,5,6,9,8,9,6,-1,
    10,1,0,10,0,6,9,5,0,5,6,0,-1,-1,-1,-1,
    0,3,8,5,6,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    10,5,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    11,5,10,7,5,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    11,5,10,11,7,5,8,3,0,-1,-1,-1,-1,-1,-1,-1,
    5,11,7,5,10,11,1,9,0,-1,-1,-1,-1,-1,-1,-1,
    10,7,5,10,11,7,9,8,1,8,3,1,-1,-1,-1,-1,
    11,1,2,11,7,1,7,5,1,-1,-1,-1,-1,-1,-1,-1,
    0,8,3,1,2,7,1,7,5,7,2,11,-1,-1,-1,-1,
    9,7,5,9,2,7,9,0,2,2,11,7,-1,-1,-1,-1,
    7,5,2,7,2,11,5,9,2,3,2,8,9,8,2,-1,
    2,5,10,2,3,5,3,7,5,-1,-1,-1,-1,-1,-1,-1,
    8,2,0,8,5,2,8,7,5,10,2,5,-1,-1,-1,-1,
    9,0,1,5,10,3,5,3,7,3,10,2,-1,-1,-1,-1,
    9,8,2,9,2,1,8,7,2,10,2,5,7,5,2,-1,
    1,3,5,3,7,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,8,7,0,7,1,1,7,5,-1,-1,-1,-1,-1,-1,-1,
    9,0,3,9,3,5,5,3,7,-1,-1,-1,-1,-1,-1,-1,
    9,8,7,5,9,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    5,8,4,5,10,8,10,11,8,-1,-1,-1,-1,-1,-1,-1,
    5,0,4,5,11,0,5,10,11,11,3,0,-1,-1,-1,-1,
    0,1,9,8,4,10,8,10,11,10,4,5,-1,-1,-1,-1,
    10,11,4,10,4,5,11,3,4,9,4,1,3,1,4,-1,
    2,5,1,2,8,5,2,11,8,4,5,8,-1,-1,-1,-1,
    0,4,11,0,11,3,4,5,11,2,11,1,5,1,11,-1,
    0,2,5,0,5,9,2,11,5,4,5,8,11,8,5,-1,
    9,4,5,2,11,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    2,5,10,3,5,2,3,4,5,3,8,4,-1,-1,-1,-1,
    5,10,2,5,2,4,4,2,0,-1,-1,-1,-1,-1,-1,-1,
    3,10,2,3,5,10,3,8,5,4,5,8,0,1,9,-1,
    5,10,2,5,2,4,1,9,2,9,4,2,-1,-1,-1,-1,
    8,4,5,8,5,3,3,5,1,-1,-1,-1,-1,-1,-1,-1,
    0,4,5,1,0,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    8,4,5,8,5,3,9,0,5,0,3,5,-1,-1,-1,-1,
    9,4,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,11,7,4,9,11,9,10,11,-1,-1,-1,-1,-1,-1,-1,
    0,8,3,4,9,7,9,11,7,9,10,11,-1,-1,-1,-1,
    1,10,11,1,11,4,1,4,0,7,4,11,-1,-1,-1,-1,
    3,1,4,3,4,8,1,10,4,7,4,11,10,11,4,-1,
    4,11,7,9,11,4,9,2,11,9,1,2,-1,-1,-1,-1,
    9,7,4,9,11,7,9,1,11,2,11,1,0,8,3,-1,
    11,7,4,11,4,2,2,4,0,-1,-1,-1,-1,-1,-1,-1,
    11,7,4,11,4,2,8,3,4,3,2,4,-1,-1,-1,-1,
    2,9,10,2,7,9,2,3,7,7,4,9,-1,-1,-1,-1,
    9,10,7,9,7,4,10,2,7,8,7,0,2,0,7,-1,
    3,7,10,3,10,2,7,4,10,1,10,0,4,0,10,-1,
    1,10,2,8,7,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,9,1,4,1,7,7,1,3,-1,-1,-1,-1,-1,-1,-1,
    4,9,1,4,1,7,0,8,1,8,7,1,-1,-1,-1,-1,
    4,0,3,7,4,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    4,8,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    9,10,8,10,11,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    3,0,9,3,9,11,11,9,10,-1,-1,-1,-1,-1,-1,-1,
    0,1,10,0,10,8,8,10,11,-1,-1,-1,-1,-1,-1,-1,
    3,1,10,11,3,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,2,11,1,11,9,9,11,8,-1,-1,-1,-1,-1,-1,-1,
    3,0,9,3,9,11,1,2,9,2,11,9,-1,-1,-1,-1,
    0,2,11,8,0,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    3,2,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    2,3,8,2,8,10,10,8,9,-1,-1,-1,-1,-1,-1,-1,
    9,10,2,0,9,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    2,3,8,2,8,10,0,1,8,1,10,8,-1,-1,-1,-1,
    1,10,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    1,3,8,9,1,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,9,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    0,3,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
    -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1
};

        #endregion

        private void Awake()
        {
            EnsureTablesInitialized();
        }

        private void OnDestroy()
        {
            if (nativeTriangleTable.IsCreated)
            {
                nativeTriangleTable.Dispose();
            }
            if (nativeEdgeConnections.IsCreated)
            {
                nativeEdgeConnections.Dispose();
            }
        }

        public void EnsureTablesInitialized()
        {
            EnsureNativeArrayInitialized(ref nativeTriangleTable, flatTriangleTable);
            EnsureNativeArrayInitialized(ref nativeEdgeConnections, flatEdgeConnections);
        }

        private static void EnsureNativeArrayInitialized(ref NativeArray<int> nativeArray, int[] sourceData)
        {
            if (nativeArray.IsCreated)
            {
                if (nativeArray.Length == sourceData.Length)
                {
                    return;
                }

                nativeArray.Dispose();
            }

            nativeArray = new NativeArray<int>(sourceData, Allocator.Persistent);
        }

        #region Public API

        /// <summary>
        /// Creates a Unity Mesh from the output of a MarchingCubesJob.
        /// This method must be called from the main thread after a job has completed.
        /// </summary>
        public Mesh CreateMeshFromJob(NativeList<Vector3> vertices, NativeList<int> triangles, NativeList<Vector3> normals)
        {
            if (!vertices.IsCreated)
            {
                return new Mesh();
            }

            NativeSlice<Vector3> vertexSlice = new NativeSlice<Vector3>(vertices.AsArray());
            NativeSlice<int> indexSlice = triangles.IsCreated ? new NativeSlice<int>(triangles.AsArray()) : default;
            NativeSlice<Vector3> normalSlice = normals.IsCreated ? new NativeSlice<Vector3>(normals.AsArray()) : default;

            return CreateMeshFromNativeSlices(vertexSlice, indexSlice, normalSlice, useSequentialIndices: false);
        }

        /// <summary>
        /// Builds a mesh from native slices of vertex, index and normal data without creating managed arrays.
        /// </summary>
        public Mesh CreateMeshFromNativeSlices(
            NativeSlice<Vector3> vertices,
            NativeSlice<int> indices,
            NativeSlice<Vector3> normals,
            bool useSequentialIndices,
            int sequentialIndexCount = -1)
        {
            Mesh mesh = new Mesh();
            int vertexCount = vertices.Length;
            if (vertexCount == 0)
            {
                return mesh;
            }

            bool hasNormals = normals.Length >= vertexCount;
            int indexCount = useSequentialIndices
                ? (sequentialIndexCount >= 0 ? sequentialIndexCount : vertexCount)
                : indices.Length;

            IndexFormat indexFormat = vertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.indexFormat = indexFormat;

            Mesh.MeshDataArray meshDataArray = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData meshData = meshDataArray[0];

            if (hasNormals)
            {
                meshData.SetVertexBufferParams(vertexCount,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, stream: 0),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, stream: 1));
            }
            else
            {
                meshData.SetVertexBufferParams(vertexCount,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3));
            }

            var vertexPositions = meshData.GetVertexData<Vector3>(0);
            new NativeSlice<Vector3>(vertexPositions, 0, vertexCount).CopyFrom(vertices);

            if (hasNormals)
            {
                var vertexNormals = meshData.GetVertexData<Vector3>(1);
                new NativeSlice<Vector3>(vertexNormals, 0, vertexCount).CopyFrom(normals);
            }

            meshData.SetIndexBufferParams(indexCount, indexFormat);

            if (indexFormat == IndexFormat.UInt32)
            {
                var indexData = meshData.GetIndexData<int>();
                if (useSequentialIndices)
                {
                    for (int i = 0; i < indexCount; i++)
                    {
                        indexData[i] = i;
                    }
                }
                else if (indexCount > 0)
                {
                    new NativeSlice<int>(indexData, 0, indexCount).CopyFrom(indices);
                }
            }
            else
            {
                var indexData = meshData.GetIndexData<ushort>();
                if (useSequentialIndices)
                {
                    for (int i = 0; i < indexCount; i++)
                    {
                        indexData[i] = (ushort)i;
                    }
                }
                else if (indexCount > 0)
                {
                    for (int i = 0; i < indexCount; i++)
                    {
                        indexData[i] = (ushort)indices[i];
                    }
                }
            }

            meshData.subMeshCount = 1;
            var subMeshDescriptor = new SubMeshDescriptor(0, indexCount)
            {
                vertexCount = vertexCount
            };
            meshData.SetSubMesh(0, subMeshDescriptor, MeshUpdateFlags.DontRecalculateBounds);

            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, mesh, MeshUpdateFlags.DontRecalculateBounds);

            if (!hasNormals)
            {
                mesh.RecalculateNormals();
            }
            if (calculateTangents)
            {
                mesh.RecalculateTangents();
            }

            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Builds or updates a transition mesh that stitches a high-detail chunk to a lower-detail neighbour.
        /// Returns <c>true</c> if the mesh contains any generated geometry.
        /// </summary>
        public bool GenerateTransitionMesh(TerrainChunk highDetailChunk, TerrainChunk lowDetailChunk, Vector3Int direction, Mesh targetMesh)
        {
            if (highDetailChunk == null || lowDetailChunk == null || targetMesh == null)
            {
                return false;
            }

            Vector3Int clampedDir = new Vector3Int(
                direction.x == 0 ? 0 : (direction.x > 0 ? 1 : -1),
                direction.y == 0 ? 0 : (direction.y > 0 ? 1 : -1),
                direction.z == 0 ? 0 : (direction.z > 0 ? 1 : -1));

            if (clampedDir == Vector3Int.zero)
            {
                targetMesh.Clear();
                return false;
            }

            int mainAxis;
            int axisU;
            int axisV;
            DetermineAxes(clampedDir, out mainAxis, out axisU, out axisV);

            Vector3Int highDims = highDetailChunk.VoxelDimensions;

            int resU = GetComponent(highDims, axisU);
            int resV = GetComponent(highDims, axisV);

            if (resU <= 0 || resV <= 0)
            {
                targetMesh.Clear();
                return false;
            }

            bool positiveDirection = GetComponent(clampedDir, mainAxis) > 0;

            int boundaryIndexHigh = positiveDirection ? GetComponent(highDims, mainAxis) : 0;
            int insideIndexHigh = positiveDirection
                ? Mathf.Max(boundaryIndexHigh - 1, 0)
                : Mathf.Clamp(1, 0, GetComponent(highDims, mainAxis));

            Vector3[,] highSurface = new Vector3[resU + 1, resV + 1];
            bool[,] validSample = new bool[resU + 1, resV + 1];

            float skirtDepth = TransitionSkirtDepth;
            if (skirtDepth <= 0f)
            {
                targetMesh.Clear();
                return false;
            }

            Vector3 normalizedDirection = ((Vector3)clampedDir).normalized;
            if (normalizedDirection.sqrMagnitude <= 1e-6f)
            {
                targetMesh.Clear();
                return false;
            }

            for (int u = 0; u <= resU; u++)
            {
                for (int v = 0; v <= resV; v++)
                {
                    Vector3 insideCoordHigh = Vector3.zero;
                    Vector3 boundaryCoordHigh = Vector3.zero;

                    SetComponent(ref insideCoordHigh, axisU, Mathf.Clamp(u, 0, GetComponent(highDims, axisU)));
                    SetComponent(ref insideCoordHigh, axisV, Mathf.Clamp(v, 0, GetComponent(highDims, axisV)));
                    SetComponent(ref insideCoordHigh, mainAxis, insideIndexHigh);

                    boundaryCoordHigh = insideCoordHigh;
                    SetComponent(ref boundaryCoordHigh, mainAxis, boundaryIndexHigh);

                    float densityInsideHigh = SampleDensityAtLocal(highDetailChunk, insideCoordHigh);
                    float densityBoundaryHigh = SampleDensityAtLocal(highDetailChunk, boundaryCoordHigh);

                    float insideDelta = densityInsideHigh - surfaceLevel;
                    float boundaryDelta = densityBoundaryHigh - surfaceLevel;
                    bool intersects = (insideDelta <= 0f && boundaryDelta >= 0f) || (insideDelta >= 0f && boundaryDelta <= 0f);
                    if (!intersects)
                    {
                        validSample[u, v] = false;
                        continue;
                    }

                    Vector3 worldInsideHigh = ToWorld(highDetailChunk, insideCoordHigh);
                    Vector3 worldBoundaryHigh = ToWorld(highDetailChunk, boundaryCoordHigh);

                    Vector3 highSurfaceWorld = InterpolateSurface(worldInsideHigh, densityInsideHigh, worldBoundaryHigh, densityBoundaryHigh);
                    Vector3 highSurfaceLocal = highSurfaceWorld - highDetailChunk.WorldPosition;
                    highSurface[u, v] = highSurfaceLocal;
                    validSample[u, v] = true;
                }
            }

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            Vector3 normalHint = normalizedDirection;

            int[,] topIndices = new int[resU + 1, resV + 1];
            int[,] bottomIndices = new int[resU + 1, resV + 1];

            for (int u = 0; u <= resU; u++)
            {
                for (int v = 0; v <= resV; v++)
                {
                    if (!validSample[u, v])
                    {
                        topIndices[u, v] = -1;
                        bottomIndices[u, v] = -1;
                        continue;
                    }

                    Vector3 top = highSurface[u, v];
                    Vector3 bottom = top + normalHint * skirtDepth;

                    int topIndex = vertices.Count;
                    vertices.Add(top);
                    normals.Add(normalHint);
                    topIndices[u, v] = topIndex;

                    int bottomIndex = vertices.Count;
                    vertices.Add(bottom);
                    normals.Add(normalHint);
                    bottomIndices[u, v] = bottomIndex;
                }
            }

            for (int u = 0; u < resU; u++)
            {
                for (int v = 0; v < resV; v++)
                {
                    int t00 = topIndices[u, v];
                    int t10 = topIndices[u + 1, v];
                    int t01 = topIndices[u, v + 1];
                    int t11 = topIndices[u + 1, v + 1];
                    int b00 = bottomIndices[u, v];
                    int b10 = bottomIndices[u + 1, v];
                    int b01 = bottomIndices[u, v + 1];
                    int b11 = bottomIndices[u + 1, v + 1];

                    if (t00 < 0 || t10 < 0 || t01 < 0 || t11 < 0 || b00 < 0 || b10 < 0 || b01 < 0 || b11 < 0)
                    {
                        continue;
                    }

                    triangles.Add(t00);
                    triangles.Add(t10);
                    triangles.Add(b11);

                    triangles.Add(t00);
                    triangles.Add(b11);
                    triangles.Add(b01);
                }
            }

            targetMesh.Clear();

            if (triangles.Count == 0 || vertices.Count == 0)
            {
                return false;
            }

            targetMesh.indexFormat = vertices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            targetMesh.SetVertices(vertices);
            targetMesh.SetNormals(normals);
            targetMesh.SetTriangles(triangles, 0, true);
            targetMesh.SetTriangles(triangles, 0, true);
            targetMesh.RecalculateBounds();
            return true;
        }

        public bool GenerateLodStitchMesh(
            DensitySampler sampler,
            Vector3Int highChunkOriginVoxel,
            Vector3Int lowChunkOriginVoxel,
            Vector3 chunkWorldSize,
            float voxelSize,
            int mainAxis,
            int direction,
            int highLodLevel,
            int lowLodLevel,
            Mesh targetMesh,
            float isoLevel = 0f)
        {
            if (targetMesh == null)
            {
                return false;
            }

            if (direction != -1 && direction != 1)
            {
                targetMesh.Clear();
                return false;
            }

            int axisU;
            int axisV;
            DetermineAxesFromMainAxis(mainAxis, out axisU, out axisV);

            if (!TryCalculateVoxelCounts(chunkWorldSize, voxelSize, highLodLevel, lowLodLevel, mainAxis, axisU, axisV,
                    out int highNu, out int highNv, out int highWCount,
                    out int lowNu, out int lowNv, out int lowWCount))
            {
                targetMesh.Clear();
                return false;
            }

            int highInsideIndex = Mathf.Clamp(direction > 0 ? highWCount - 1 : 1, 0, highWCount);
            int highBoundaryIndex = direction > 0 ? highWCount : 0;

            int lowBoundaryIndex = direction > 0 ? 0 : lowWCount;
            int lowInsideIndex = Mathf.Clamp(lowBoundaryIndex + direction, 0, lowWCount);

            Vector3 highChunkWorldOrigin = new Vector3(
                highChunkOriginVoxel.x * voxelSize,
                highChunkOriginVoxel.y * voxelSize,
                highChunkOriginVoxel.z * voxelSize);

            Vector3 lowChunkWorldOrigin = new Vector3(
                lowChunkOriginVoxel.x * voxelSize,
                lowChunkOriginVoxel.y * voxelSize,
                lowChunkOriginVoxel.z * voxelSize);

            Vector3Int highStepU = GetAxisStep(axisU, 1 << highLodLevel);
            Vector3Int highStepV = GetAxisStep(axisV, 1 << highLodLevel);
            Vector3Int highStepW = GetAxisStep(mainAxis, 1 << highLodLevel);

            Vector3Int lowStepU = GetAxisStep(axisU, 1 << lowLodLevel);
            Vector3Int lowStepV = GetAxisStep(axisV, 1 << lowLodLevel);
            Vector3Int lowStepW = GetAxisStep(mainAxis, 1 << lowLodLevel);

            Vector3[,] highSurface = new Vector3[highNu, highNv];
            bool[,] highValid = new bool[highNu, highNv];

            int highValidCount = SampleFaceSurface(
                sampler,
                highLodLevel,
                highChunkOriginVoxel,
                highStepU,
                highStepV,
                highStepW,
                highInsideIndex,
                highBoundaryIndex,
                highNu,
                highNv,
                voxelSize,
                highChunkWorldOrigin,
                isoLevel,
                highSurface,
                highValid);

            if (highValidCount == 0)
            {
                targetMesh.Clear();
                return false;
            }

            Vector3[,] lowSurface = new Vector3[lowNu, lowNv];
            bool[,] lowValid = new bool[lowNu, lowNv];

            int lowValidCount = SampleFaceSurface(
                sampler,
                lowLodLevel,
                lowChunkOriginVoxel,
                lowStepU,
                lowStepV,
                lowStepW,
                lowInsideIndex,
                lowBoundaryIndex,
                lowNu,
                lowNv,
                voxelSize,
                highChunkWorldOrigin,
                isoLevel,
                lowSurface,
                lowValid);

            if (lowValidCount == 0)
            {
                targetMesh.Clear();
                return false;
            }

            Vector3[,] lowUpsampled = new Vector3[highNu, highNv];
            bool[,] lowUpsampledValid = new bool[highNu, highNv];
            UpsampleLowSurface(lowSurface, lowValid, lowNu, lowNv, lowUpsampled, lowUpsampledValid);

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();

            int[,] topIndices = new int[highNu, highNv];
            int[,] bottomIndices = new int[highNu, highNv];
            for (int u = 0; u < highNu; u++)
            {
                for (int v = 0; v < highNv; v++)
                {
                    topIndices[u, v] = -1;
                    bottomIndices[u, v] = -1;
                }
            }

            for (int u = 0; u < highNu; u++)
            {
                for (int v = 0; v < highNv; v++)
                {
                    if (!highValid[u, v])
                    {
                        continue;
                    }

                    if (!lowUpsampledValid[u, v])
                    {
                        continue;
                    }

                    int topIndex = vertices.Count;
                    vertices.Add(highSurface[u, v]);
                    normals.Add(Vector3.zero);
                    topIndices[u, v] = topIndex;

                    int bottomIndex = vertices.Count;
                    vertices.Add(lowUpsampled[u, v]);
                    normals.Add(Vector3.zero);
                    bottomIndices[u, v] = bottomIndex;
                }
            }

            if (!BuildStitchTriangles(topIndices, bottomIndices, vertices, normals, triangles))
            {
                targetMesh.Clear();
                return false;
            }

            NormalizeNormals(normals);

            targetMesh.Clear();
            targetMesh.indexFormat = vertices.Count > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16;
            targetMesh.SetVertices(vertices);
            targetMesh.SetNormals(normals);
            targetMesh.SetTriangles(triangles, 0, true);
            targetMesh.RecalculateBounds();
            return true;
        }
private static bool BuildStitchTriangles(int[,] topIndices, int[,] bottomIndices, List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
        {
            int width = topIndices.GetLength(0);
            int height = topIndices.GetLength(1);
            bool any = false;

            for (int u = 0; u < width - 1; u++)
            {
                for (int v = 0; v < height - 1; v++)
                {
                    int t00 = topIndices[u, v];
                    int t10 = topIndices[u + 1, v];
                    int t01 = topIndices[u, v + 1];
                    int t11 = topIndices[u + 1, v + 1];

                    int b00 = bottomIndices[u, v];
                    int b10 = bottomIndices[u + 1, v];
                    int b01 = bottomIndices[u, v + 1];
                    int b11 = bottomIndices[u + 1, v + 1];

                    if (t00 < 0 || t10 < 0 || t01 < 0 || t11 < 0
                        || b00 < 0 || b10 < 0 || b01 < 0 || b11 < 0)
                    {
                        continue;
                    }

                    AddTriangle(triangles, vertices, normals, t00, t10, b11);
                    AddTriangle(triangles, vertices, normals, t00, b11, t01);
                    AddTriangle(triangles, vertices, normals, b00, b10, t11);
                    AddTriangle(triangles, vertices, normals, b00, t11, b01);
                    any = true;
                }
            }

            return any;
        }

        private static void AddTriangle(List<int> triangles, List<Vector3> vertices, List<Vector3> normals, int a, int b, int c)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);

            Vector3 ab = vertices[b] - vertices[a];
            Vector3 ac = vertices[c] - vertices[a];
            Vector3 normal = Vector3.Cross(ab, ac);
            if (normal.sqrMagnitude > 1e-12f)
            {
                normal.Normalize();
            }

            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }

        private static void NormalizeNormals(List<Vector3> normals)
        {
            for (int i = 0; i < normals.Count; i++)
            {
                Vector3 n = normals[i];
                if (n.sqrMagnitude > 1e-12f)
                {
                    normals[i] = n.normalized;
                }
                else
                {
                    normals[i] = Vector3.up;
                }
            }
        }

        private static void DetermineAxesFromMainAxis(int mainAxis, out int axisU, out int axisV)
        {
            switch (mainAxis)
            {
                case 0:
                    axisU = 1;
                    axisV = 2;
                    break;
                case 1:
                    axisU = 0;
                    axisV = 2;
                    break;
                default:
                    axisU = 0;
                    axisV = 1;
                    break;
            }
        }

        private static bool TryCalculateVoxelCounts(
            Vector3 chunkWorldSize,
            float voxelSize,
            int highLodLevel,
            int lowLodLevel,
            int mainAxis,
            int axisU,
            int axisV,
            out int highNu,
            out int highNv,
            out int highWCount,
            out int lowNu,
            out int lowNv,
            out int lowWCount)
        {
            highNu = highNv = highWCount = lowNu = lowNv = lowWCount = 0;

            int baseU = Mathf.RoundToInt(GetComponent(chunkWorldSize, axisU) / voxelSize);
            int baseV = Mathf.RoundToInt(GetComponent(chunkWorldSize, axisV) / voxelSize);
            int baseW = Mathf.RoundToInt(GetComponent(chunkWorldSize, mainAxis) / voxelSize);

            if (!IsDivisibleByLod(baseU, highLodLevel) || !IsDivisibleByLod(baseV, highLodLevel) || !IsDivisibleByLod(baseW, highLodLevel))
            {
                return false;
            }

            if (!IsDivisibleByLod(baseU, lowLodLevel) || !IsDivisibleByLod(baseV, lowLodLevel) || !IsDivisibleByLod(baseW, lowLodLevel))
            {
                return false;
            }

            int highUCount = baseU >> highLodLevel;
            int highVCount = baseV >> highLodLevel;
            highWCount = baseW >> highLodLevel;

            int lowUCount = baseU >> lowLodLevel;
            int lowVCount = baseV >> lowLodLevel;
            lowWCount = baseW >> lowLodLevel;

            highNu = highUCount + 1;
            highNv = highVCount + 1;
            lowNu = lowUCount + 1;
            lowNv = lowVCount + 1;

            return true;
        }

        private static bool IsDivisibleByLod(int value, int lodLevel)
        {
            if (lodLevel <= 0)
            {
                return true;
            }

            int divisor = 1 << lodLevel;
            return (value % divisor) == 0;
        }

        private static Vector3Int GetAxisStep(int axis, int magnitude)
        {
            switch (axis)
            {
                case 0:
                    return new Vector3Int(magnitude, 0, 0);
                case 1:
                    return new Vector3Int(0, magnitude, 0);
                default:
                    return new Vector3Int(0, 0, magnitude);
            }
        }

        private static int SampleFaceSurface(
            DensitySampler sampler,
            int lodLevel,
            Vector3Int chunkOriginVoxel,
            Vector3Int stepU,
            Vector3Int stepV,
            Vector3Int stepW,
            int insideIndex,
            int boundaryIndex,
            int countU,
            int countV,
            float voxelSize,
            Vector3 referenceOrigin,
            float isoLevel,
            Vector3[,] surface,
            bool[,] valid)
        {
            int validCount = 0;

            for (int u = 0; u < countU; u++)
            {
                for (int v = 0; v < countV; v++)
                {
                    Vector3Int baseVoxel = chunkOriginVoxel + stepU * u + stepV * v;
                    Vector3Int insideVoxel = baseVoxel + stepW * insideIndex;
                    Vector3Int boundaryVoxel = baseVoxel + stepW * boundaryIndex;

                    if (!sampler.TrySample(lodLevel, insideVoxel, out float densityInside)
                        || !sampler.TrySample(lodLevel, boundaryVoxel, out float densityBoundary))
                    {
                        valid[u, v] = false;
                        continue;
                    }

                    float insideDelta = densityInside - isoLevel;
                    float boundaryDelta = densityBoundary - isoLevel;
                    if (insideDelta * boundaryDelta > 0f)
                    {
                        valid[u, v] = false;
                        continue;
                    }

                    Vector3 insideWorld = (Vector3)insideVoxel * voxelSize;
                    Vector3 boundaryWorld = (Vector3)boundaryVoxel * voxelSize;
                    Vector3 point = InterpolateSurface(isoLevel, insideWorld, densityInside, boundaryWorld, densityBoundary) - referenceOrigin;
                    surface[u, v] = point;
                    valid[u, v] = true;
                    validCount++;
                }
            }

            return validCount;
        }

        private static Vector3 InterpolateSurface(float isoLevel, Vector3 start, float densityStart, Vector3 end, float densityEnd)
        {
            float denominator = densityEnd - densityStart;
            float t = Mathf.Abs(denominator) > 1e-5f
                ? (isoLevel - densityStart) / denominator
                : 0.5f;
            t = Mathf.Clamp01(t);
            return Vector3.Lerp(start, end, t);
        }

        private static void UpsampleLowSurface(
            Vector3[,] lowSurface,
            bool[,] lowValid,
            int lowNu,
            int lowNv,
            Vector3[,] result,
            bool[,] resultValid)
        {
            int highNu = result.GetLength(0);
            int highNv = result.GetLength(1);

            for (int u = 0; u < highNu; u++)
            {
                for (int v = 0; v < highNv; v++)
                {
                    if (TrySampleUpsampledLow(lowSurface, lowValid, lowNu, lowNv, u, v, out Vector3 sample))
                    {
                        result[u, v] = sample;
                        resultValid[u, v] = true;
                    }
                    else
                    {
                        resultValid[u, v] = false;
                    }
                }
            }
        }

        private static bool TrySampleUpsampledLow(
            Vector3[,] lowSurface,
            bool[,] lowValid,
            int lowNu,
            int lowNv,
            int highU,
            int highV,
            out Vector3 sample)
        {
            float scaledU = highU * 0.5f;
            float scaledV = highV * 0.5f;

            int u0 = Mathf.Clamp(Mathf.FloorToInt(scaledU), 0, lowNu - 1);
            int v0 = Mathf.Clamp(Mathf.FloorToInt(scaledV), 0, lowNv - 1);
            int u1 = Mathf.Clamp(u0 + 1, 0, lowNu - 1);
            int v1 = Mathf.Clamp(v0 + 1, 0, lowNv - 1);

            float fu = scaledU - u0;
            float fv = scaledV - v0;

            bool v00 = lowValid[u0, v0];
            bool v10 = lowValid[u1, v0];
            bool v01 = lowValid[u0, v1];
            bool v11 = lowValid[u1, v1];

            if (Mathf.Approximately(fu, 0f) && Mathf.Approximately(fv, 0f))
            {
                if (v00)
                {
                    sample = lowSurface[u0, v0];
                    return true;
                }

                sample = default;
                return false;
            }

            if (Mathf.Approximately(fv, 0f))
            {
                if (v00 && v10)
                {
                    sample = Vector3.Lerp(lowSurface[u0, v0], lowSurface[u1, v0], fu);
                    return true;
                }

                if (v00)
                {
                    sample = lowSurface[u0, v0];
                    return true;
                }

                if (v10)
                {
                    sample = lowSurface[u1, v0];
                    return true;
                }

                sample = default;
                return false;
            }

            if (Mathf.Approximately(fu, 0f))
            {
                if (v00 && v01)
                {
                    sample = Vector3.Lerp(lowSurface[u0, v0], lowSurface[u0, v1], fv);
                    return true;
                }

                if (v00)
                {
                    sample = lowSurface[u0, v0];
                    return true;
                }

                if (v01)
                {
                    sample = lowSurface[u0, v1];
                    return true;
                }

                sample = default;
                return false;
            }

            if (v00 && v10 && v01 && v11)
            {
                Vector3 a = Vector3.Lerp(lowSurface[u0, v0], lowSurface[u1, v0], fu);
                Vector3 b = Vector3.Lerp(lowSurface[u0, v1], lowSurface[u1, v1], fu);
                sample = Vector3.Lerp(a, b, fv);
                return true;
            }

            Vector3 accumulator = Vector3.zero;
            float weight = 0f;

            if (v00)
            {
                accumulator += lowSurface[u0, v0];
                weight += 1f;
            }

            if (v10)
            {
                accumulator += lowSurface[u1, v0];
                weight += 1f;
            }

            if (v01)
            {
                accumulator += lowSurface[u0, v1];
                weight += 1f;
            }

            if (v11)
            {
                accumulator += lowSurface[u1, v1];
                weight += 1f;
            }

            if (weight > 0f)
            {
                sample = accumulator / weight;
                return true;
            }

            sample = default;
            return false;
        }
        private static void DetermineAxes(Vector3Int direction, out int mainAxis, out int axisU, out int axisV)
        {
            if (Mathf.Abs(direction.x) > 0)
            {
                mainAxis = 0;
                axisU = 1;
                axisV = 2;
            }
            else if (Mathf.Abs(direction.y) > 0)
            {
                mainAxis = 1;
                axisU = 0;
                axisV = 2;
            }
            else
            {
                mainAxis = 2;
                axisU = 0;
                axisV = 1;
            }
        }

        private float SampleDensityAtLocal(TerrainChunk chunk, Vector3 localCoord)
        {
            if (chunk == null)
            {
                return 0f;
            }

            Vector3Int dims = chunk.VoxelDimensions;

            float x = Mathf.Clamp(localCoord.x, 0f, dims.x);
            float y = Mathf.Clamp(localCoord.y, 0f, dims.y);
            float z = Mathf.Clamp(localCoord.z, 0f, dims.z);

            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            int z0 = Mathf.FloorToInt(z);

            int x1 = Mathf.Min(x0 + 1, dims.x);
            int y1 = Mathf.Min(y0 + 1, dims.y);
            int z1 = Mathf.Min(z0 + 1, dims.z);

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

        private float SampleDensityAtWorld(TerrainChunk chunk, Vector3 worldPosition)
        {
            if (chunk == null)
            {
                return 0f;
            }

            float voxelSize = chunk.VoxelSize;
            if (voxelSize <= 0f)
            {
                return 0f;
            }

            Vector3 localCoord = (worldPosition - chunk.WorldPosition) / voxelSize;
            return SampleDensityAtLocal(chunk, localCoord);
        }

        private Vector3 EstimateNormalWorld(TerrainChunk chunk, Vector3 worldPosition)
        {
            if (chunk == null)
            {
                return Vector3.zero;
            }

            float voxelSize = chunk.VoxelSize;
            if (voxelSize <= 0f)
            {
                return Vector3.zero;
            }

            float sampleOffset = Mathf.Max(voxelSize * 0.5f, 1e-3f);
            float invTwoOffset = 0.5f / sampleOffset;

            float dx = SampleDensityAtWorld(chunk, worldPosition + new Vector3(sampleOffset, 0f, 0f))
                - SampleDensityAtWorld(chunk, worldPosition - new Vector3(sampleOffset, 0f, 0f));
            float dy = SampleDensityAtWorld(chunk, worldPosition + new Vector3(0f, sampleOffset, 0f))
                - SampleDensityAtWorld(chunk, worldPosition - new Vector3(0f, sampleOffset, 0f));
            float dz = SampleDensityAtWorld(chunk, worldPosition + new Vector3(0f, 0f, sampleOffset))
                - SampleDensityAtWorld(chunk, worldPosition - new Vector3(0f, 0f, sampleOffset));

            Vector3 normal = new Vector3(dx, dy, dz) * invTwoOffset;
            if (normal.sqrMagnitude > 1e-12f)
            {
                return normal.normalized;
            }

            return Vector3.zero;
        }

        private Vector3 InterpolateSurface(Vector3 start, float densityStart, Vector3 end, float densityEnd)
        {
            float denominator = densityEnd - densityStart;
            float t = Mathf.Abs(denominator) > 1e-5f
                ? (surfaceLevel - densityStart) / denominator
                : 0.5f;
            t = Mathf.Clamp01(t);
            return Vector3.Lerp(start, end, t);
        }

        private static Vector3 ToWorld(TerrainChunk chunk, Vector3 localCoord)
        {
            float vSize = chunk.VoxelSize;
            return chunk.WorldPosition + new Vector3(localCoord.x * vSize, localCoord.y * vSize, localCoord.z * vSize);
        }

        private static int GetComponent(Vector3Int value, int axis)
        {
            switch (axis)
            {
                case 0: return value.x;
                case 1: return value.y;
                default: return value.z;
            }
        }

        private static float GetComponent(Vector3 value, int axis)
        {
            switch (axis)
            {
                case 0: return value.x;
                case 1: return value.y;
                default: return value.z;
            }
        }

        private static void SetComponent(ref Vector3 vector, int axis, float newValue)
        {
            switch (axis)
            {
                case 0:
                    vector.x = newValue;
                    break;
                case 1:
                    vector.y = newValue;
                    break;
                default:
                    vector.z = newValue;
                    break;
            }
        }

        #endregion
    }

}
