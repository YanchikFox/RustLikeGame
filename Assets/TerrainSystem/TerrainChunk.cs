using UnityEngine;
using System;
using Unity.Collections;

namespace TerrainSystem
{
    public class TerrainChunk : MonoBehaviour
    {
        #region Properties and Fields
        private Vector3Int chunkSize; // Now represents voxel dimensions
        private float voxelSize;
        [SerializeField] private bool generateCollider = true;
        
        private MeshFilter meshFilter;
        private MeshRenderer meshRenderer;
        private MeshCollider meshCollider;
        
        private VoxelData[,,] voxelData;
        private Vector3Int chunkPosition;
        private int lodLevel; // Added LOD level property
        private bool isInitialized = false;
        private int densityVersion = 0;

        public Vector3 WorldPosition { get; private set; }
        public VoxelData[,,] Voxels => voxelData;
        public Vector3Int ChunkPosition => chunkPosition;
        public int LODLevel => lodLevel; // Added public getter for LOD level
        public Vector3Int VoxelDimensions => chunkSize;
        public float VoxelSize => voxelSize;
        public bool IsDirty { get; set; } = true;
        public int DensityVersion => densityVersion;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            meshFilter = GetComponent<MeshFilter>() ?? gameObject.AddComponent<MeshFilter>();
            meshRenderer = GetComponent<MeshRenderer>() ?? gameObject.AddComponent<MeshRenderer>();
            if (generateCollider)
            {
                meshCollider = GetComponent<MeshCollider>() ?? gameObject.AddComponent<MeshCollider>();
            }
        }

        private void OnDestroy()
        {
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                Destroy(meshFilter.sharedMesh);
            }
        }
        #endregion

        #region Initialization
        public void Initialize(Vector3Int position, Vector3Int voxelDimensions, float vSize, Vector3 worldPos, int lod = 0)
        {
            chunkPosition = position;
            chunkSize = voxelDimensions;
            voxelSize = vSize;
            WorldPosition = worldPos;
            lodLevel = lod;
            
            name = $"Chunk_{position.x}_{position.y}_{position.z}_LOD{lod}";
            voxelData = new VoxelData[chunkSize.x + 1, chunkSize.y + 1, chunkSize.z + 1];
            isInitialized = true;
            IsDirty = true;
            densityVersion = 0;
        }
        #endregion

        #region Voxel Operations
        public VoxelData GetVoxel(int x, int y, int z)
        {
            if (isInitialized && x >= 0 && x <= chunkSize.x && y >= 0 && y <= chunkSize.y && z >= 0 && z <= chunkSize.z)
            {
                return voxelData[x, y, z];
            }
            return VoxelData.AIR;
        }

        public bool SetVoxel(int x, int y, int z, VoxelData value)
        {
            if (!isInitialized || x < 0 || x > chunkSize.x || y < 0 || y > chunkSize.y || z < 0 || z > chunkSize.z)
            {
                return false;
            }

            if (Math.Abs(voxelData[x, y, z].density - value.density) > 0.001f)
            {
                voxelData[x, y, z] = value;
                IsDirty = true;
                unchecked
                {
                    densityVersion++;
                }
                return true;
            }
            return false;
        }

        // Bulk-apply densities generated in a job without per-voxel overhead on main thread.
        public void ApplyDensities(NativeArray<float> densities)
        {
            if (!isInitialized) return;

            int width = chunkSize.x + 1;
            int height = chunkSize.y + 1;
            int depth = chunkSize.z + 1;
            int idx = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        voxelData[x, y, z] = new VoxelData(densities[idx++]);
                    }
                }
            }

            IsDirty = true;
            unchecked
            {
                densityVersion++;
            }
        }

        // Overload for float array when using GPU buffers
        public void ApplyDensities(float[] densities)
        {
            if (!isInitialized) return;

            int width = chunkSize.x + 1;
            int height = chunkSize.y + 1;
            int depth = chunkSize.z + 1;
            int idx = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        voxelData[x, y, z] = new VoxelData(densities[idx++]);
                    }
                }
            }

            IsDirty = true;
            unchecked
            {
                densityVersion++;
            }
        }

        public void CopyVoxelDataTo(NativeArray<float> destination)
        {
            int width = chunkSize.x + 1;
            int height = chunkSize.y + 1;
            int idx = 0;

            for (int z = 0; z <= chunkSize.z; z++)
            for (int y = 0; y <= chunkSize.y; y++)
            for (int x = 0; x <= chunkSize.x; x++)
            {
                destination[idx++] = voxelData[x, y, z].density;
            }
        }
        
        public void CopyVoxelDataTo(float[] destination)
        {
            int width = chunkSize.x + 1;
            int height = chunkSize.y + 1;
            int idx = 0;

            for (int z = 0; z <= chunkSize.z; z++)
            for (int y = 0; y <= chunkSize.y; y++)
            for (int x = 0; x <= chunkSize.x; x++)
            {
                destination[idx++] = voxelData[x, y, z].density;
            }
        }
        #endregion

        #region Mesh Generation
        public void ApplyMesh(Mesh mesh)
        {
            if (mesh == null) return;

            if (meshFilter.sharedMesh != null)
            {
                Destroy(meshFilter.sharedMesh);
            }

            meshFilter.sharedMesh = mesh;

            if (generateCollider && meshCollider != null)
            {
                meshCollider.sharedMesh = null;
                meshCollider.sharedMesh = mesh;
            }
            IsDirty = false;
        }
        #endregion

        #region Debug
        private void OnDrawGizmosSelected()
        {
            if (!isInitialized) return;
            
            // Choose color based on LOD level
            Color lodColor = Color.white;
            switch (lodLevel)
            {
                case 0: lodColor = Color.green; break;   // Highest detail
                case 1: lodColor = Color.yellow; break;  // Medium detail
                case 2: lodColor = Color.red; break;     // Low detail
                default: lodColor = Color.gray; break;   // Lowest detail
            }
            
            Gizmos.color = lodColor;
            Vector3 size = new Vector3(chunkSize.x * voxelSize, chunkSize.y * voxelSize, chunkSize.z * voxelSize);
            Gizmos.DrawWireCube(transform.position + size / 2f, size);
            
            // Optionally show LOD level as text
            // You'd need to implement this with a custom editor or text mesh
        }
        #endregion
    }
}