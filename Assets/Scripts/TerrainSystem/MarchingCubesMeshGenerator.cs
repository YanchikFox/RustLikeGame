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
    {
        [Header("Mesh Generation Settings")]
        [SerializeField] private bool calculateTangents = true;

        [Header("Performance Settings")]
        [Range(0f, 1f)]
        [Tooltip("Surface level threshold. Values below this are solid, above are air")]
        [SerializeField]
        private float _surfaceLevel = 0.0f;
        public float surfaceLevel => _surfaceLevel;

        [Header("Transition Settings")]
        [Tooltip("Depth of the transition skirt in voxel units. The final offset scales with the chunk's voxel size.")]
        [SerializeField, Min(0f)]
        private float transitionSkirtDepth = 0.25f;

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

            int lowResU = GetComponent(lowDims, axisU);
            int lowResV = GetComponent(lowDims, axisV);
            int lowMainRes = GetComponent(lowDims, mainAxis);

            float scaleU = resU > 0 ? (float)lowResU / resU : 0f;
            float scaleV = resV > 0 ? (float)lowResV / resV : 0f;

            int boundaryIndexLow = positiveDirection ? 0 : lowMainRes;
            int insideIndexLow = positiveDirection
                ? (lowMainRes > 0 ? 1 : 0)
                : Mathf.Max(lowMainRes - 1, 0);

            Vector3[,] highSurface = new Vector3[resU + 1, resV + 1];

            float skirtDepth = TransitionSkirtDepth;
            if (skirtDepth <= 0f)
            {
                targetMesh.Clear();
                return false;
            }

            Vector3 normalizedDirection = ((Vector3)clampedDir).normalized;
            Vector3 skirtOffsetLocal = normalizedDirection * skirtDepth;

            float skirtDepthWorld = Mathf.Max(0f, transitionSkirtDepth) * highDetailChunk.VoxelSize;
            Vector3 fallbackOffsetWorld = ((Vector3)clampedDir).normalized * -skirtDepthWorld;

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

                    Vector3 worldInsideHigh = ToWorld(highDetailChunk, insideCoordHigh);
                    Vector3 worldBoundaryHigh = ToWorld(highDetailChunk, boundaryCoordHigh);

                    Vector3 highSurfaceWorld = InterpolateSurface(worldInsideHigh, densityInsideHigh, worldBoundaryHigh, densityBoundaryHigh);

                    float scaledU = Mathf.Clamp(u * scaleU, 0f, lowResU);
                    float scaledV = Mathf.Clamp(v * scaleV, 0f, lowResV);

                    Vector3 insideCoordLow = Vector3.zero;
                    Vector3 boundaryCoordLow = Vector3.zero;

                    SetComponent(ref insideCoordLow, axisU, scaledU);
                    SetComponent(ref insideCoordLow, axisV, scaledV);
                    SetComponent(ref insideCoordLow, mainAxis, insideIndexLow);

                    boundaryCoordLow = insideCoordLow;
                    SetComponent(ref boundaryCoordLow, mainAxis, boundaryIndexLow);

                    float densityInsideLow = SampleDensityAtLocal(lowDetailChunk, insideCoordLow);
                    float densityBoundaryLow = SampleDensityAtLocal(lowDetailChunk, boundaryCoordLow);

                    Vector3 worldInsideLow = ToWorld(lowDetailChunk, insideCoordLow);
                    Vector3 worldBoundaryLow = ToWorld(lowDetailChunk, boundaryCoordLow);

                    Vector3 lowSurfaceWorld = InterpolateSurface(worldInsideLow, densityInsideLow, worldBoundaryLow, densityBoundaryLow);

                    if (!IsFinite(highSurfaceWorld))
                    {
                        highSurfaceWorld = worldBoundaryHigh;
                    }

                    if (!IsFinite(lowSurfaceWorld))
                    {
                        Vector3 fallbackBase = IsFinite(highSurfaceWorld) ? highSurfaceWorld : worldBoundaryHigh;
                        lowSurfaceWorld = fallbackBase + fallbackOffsetWorld;
                    }

                    highSurface[u, v] = highSurfaceWorld - highDetailChunk.WorldPosition;
                }
            }

            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var normals = new List<Vector3>();
            Vector3 normalHint = ((Vector3)clampedDir).normalized;

            for (int u = 0; u < resU; u++)
            {
                for (int v = 0; v < resV; v++)
                {
                    Vector3 h00 = highSurface[u, v];
                    Vector3 h10 = highSurface[u + 1, v];
                    Vector3 h01 = highSurface[u, v + 1];
                    Vector3 h11 = highSurface[u + 1, v + 1];

                    Vector3 l00 = h00 + skirtOffsetLocal;
                    Vector3 l10 = h10 + skirtOffsetLocal;
                    Vector3 l01 = h01 + skirtOffsetLocal;
                    Vector3 l11 = h11 + skirtOffsetLocal;

                    AddQuad(h00, h10, l10, l00, normalHint, vertices, triangles, normals);
                    AddQuad(h00, l00, l01, h01, normalHint, vertices, triangles, normals);
                    AddQuad(h10, h11, l11, l10, normalHint, vertices, triangles, normals);
                    AddQuad(h01, h11, l11, l01, normalHint, vertices, triangles, normals);
                }
            }

            targetMesh.Clear();

            if (vertices.Count == 0)
            {
                return false;
            }

            targetMesh.indexFormat = vertices.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;

            targetMesh.SetVertices(vertices);
            targetMesh.SetTriangles(triangles, 0, true);
            targetMesh.SetNormals(normals);
            targetMesh.RecalculateBounds();
            return true;
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

        private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normalHint,
            List<Vector3> vertices, List<int> triangles, List<Vector3> normals)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            if (normal.sqrMagnitude < 1e-12f)
            {
                return;
            }

            normal.Normalize();
            if (Vector3.Dot(normal, normalHint) < 0f)
            {
                Vector3 tempB = b;
                b = d;
                d = tempB;
                normal = -normal;
            }

            int startIndex = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);

            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);
            normals.Add(normal);

            triangles.Add(startIndex);
            triangles.Add(startIndex + 1);
            triangles.Add(startIndex + 2);

            triangles.Add(startIndex);
            triangles.Add(startIndex + 2);
            triangles.Add(startIndex + 3);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z)
                || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
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