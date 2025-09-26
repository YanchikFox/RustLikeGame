using UnityEngine;
using System;

namespace TerrainSystem
{
    /// <summary>
    /// Represents the data for a single voxel in the terrain.
    /// Density follows the terrain generation convention: negative values indicate solid matter
    /// and positive values indicate empty space/air.
    /// </summary>
    [Serializable]
    public struct VoxelData
    {
        /// <summary>
        /// Density value (-1 to 1). Negative values are inside terrain, positive values are outside.
        /// </summary>
        public float density;
        
        /// <summary>
        /// Material index for this voxel (can represent different terrain types).
        /// </summary>
        public byte materialIndex;

        /// <summary>
        /// A static instance representing empty air.
        /// </summary>
        public static readonly VoxelData AIR = new VoxelData(1f, 0);

        /// <summary>
        /// A static instance representing solid terrain.
        /// </summary>
        public static readonly VoxelData SOLID = new VoxelData(-1f, 0);

        /// <summary>
        /// Creates a new voxel with the specified density and material.
        /// </summary>
        /// <param name="density">Density value where negative is inside terrain, positive is outside</param>
        /// <param name="materialIndex">Material type index</param>
        public VoxelData(float density, byte materialIndex = 0)
        {
            this.density = density;
            this.materialIndex = materialIndex;
        }

        /// <summary>
        /// Checks if this voxel is solid (inside terrain).
        /// </summary>
        public bool IsSolid => density < 0f;
        
        /// <summary>
        /// Checks if this voxel is empty (outside terrain).
        /// </summary>
        public bool IsEmpty => density >= 0f;

        /// <summary>
        /// Returns a solid terrain voxel with the specified material.
        /// </summary>
        public static VoxelData Solid(byte materialIndex = 0) => new VoxelData(-1f, materialIndex);
        
        /// <summary>
        /// Returns an empty air voxel.
        /// </summary>
        public static VoxelData Air => new VoxelData(1f, 0);
    }
}