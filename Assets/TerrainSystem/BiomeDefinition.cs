using UnityEngine;

namespace TerrainSystem
{
    [CreateAssetMenu(menuName = "Terrain/Biome Definition", fileName = "BiomeDefinition")]
    public class BiomeDefinition : ScriptableObject
    {
        [SerializeField]
        private string displayName = "New Biome";

        [SerializeField]
        [Tooltip("The biome noise value where this biome starts")]
        private float startThreshold = -1f;

        [SerializeField]
        private int octaves = 4;

        [SerializeField]
        private float lacunarity = 2f;

        [SerializeField]
        private float persistence = 0.5f;

        [SerializeField]
        private float worldGroundLevel = 20f;

        [SerializeField]
        private float heightImpact = 15f;

        [SerializeField]
        private float heightNoiseScale = 0.05f;

        [SerializeField]
        private float caveImpact = 0.3f;

        [SerializeField]
        private float caveNoiseScale = 0.08f;

        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? name : displayName;
        public float StartThreshold => startThreshold;
        public int Octaves => octaves;
        public float Lacunarity => lacunarity;
        public float Persistence => persistence;
        public float WorldGroundLevel => worldGroundLevel;
        public float HeightImpact => heightImpact;
        public float HeightNoiseScale => heightNoiseScale;
        public float CaveImpact => caveImpact;
        public float CaveNoiseScale => caveNoiseScale;

        public BiomeSettings ToSettings()
        {
            return new BiomeSettings
            {
                name = DisplayName,
                startThreshold = startThreshold,
                octaves = octaves,
                lacunarity = lacunarity,
                persistence = persistence,
                worldGroundLevel = worldGroundLevel,
                heightImpact = heightImpact,
                heightNoiseScale = heightNoiseScale,
                caveImpact = caveImpact,
                caveNoiseScale = caveNoiseScale
            };
        }
    }
}
