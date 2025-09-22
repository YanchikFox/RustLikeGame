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

        [Header("Domain Warp Settings")]
        [SerializeField]
        private bool enableHeightDomainWarp = false;

        [SerializeField]
        private float heightWarpStrength = 0f;

        [SerializeField]
        private float heightWarpScale = 0.05f;

        [SerializeField]
        private bool enableCaveDomainWarp = false;

        [SerializeField]
        private float caveWarpStrength = 0f;

        [SerializeField]
        private float caveWarpScale = 0.08f;

        [Header("Additional Height Layer")]
        [SerializeField]
        private bool enableAdditionalHeightLayer = false;

        [SerializeField]
        private int additionalHeightOctaves = 0;

        [SerializeField]
        private float additionalHeightScale = 0.1f;

        [SerializeField]
        private float additionalHeightLacunarity = 2f;

        [SerializeField]
        private float additionalHeightPersistence = 0.5f;

        [SerializeField]
        private float additionalHeightImpact = 0f;

        [Header("Additional Cave Layer")]
        [SerializeField]
        private bool enableAdditionalCaveLayer = false;

        [SerializeField]
        private int additionalCaveOctaves = 0;

        [SerializeField]
        private float additionalCaveScale = 0.1f;

        [SerializeField]
        private float additionalCaveLacunarity = 2f;

        [SerializeField]
        private float additionalCavePersistence = 0.5f;

        [SerializeField]
        private float additionalCaveImpact = 0f;

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
        public bool EnableHeightDomainWarp => enableHeightDomainWarp;
        public float HeightWarpStrength => heightWarpStrength;
        public float HeightWarpScale => heightWarpScale;
        public bool EnableCaveDomainWarp => enableCaveDomainWarp;
        public float CaveWarpStrength => caveWarpStrength;
        public float CaveWarpScale => caveWarpScale;
        public bool EnableAdditionalHeightLayer => enableAdditionalHeightLayer;
        public int AdditionalHeightOctaves => additionalHeightOctaves;
        public float AdditionalHeightScale => additionalHeightScale;
        public float AdditionalHeightLacunarity => additionalHeightLacunarity;
        public float AdditionalHeightPersistence => additionalHeightPersistence;
        public float AdditionalHeightImpact => additionalHeightImpact;
        public bool EnableAdditionalCaveLayer => enableAdditionalCaveLayer;
        public int AdditionalCaveOctaves => additionalCaveOctaves;
        public float AdditionalCaveScale => additionalCaveScale;
        public float AdditionalCaveLacunarity => additionalCaveLacunarity;
        public float AdditionalCavePersistence => additionalCavePersistence;
        public float AdditionalCaveImpact => additionalCaveImpact;

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
                caveNoiseScale = caveNoiseScale,
                enableHeightDomainWarp = enableHeightDomainWarp,
                heightWarpStrength = heightWarpStrength,
                heightWarpScale = heightWarpScale,
                enableCaveDomainWarp = enableCaveDomainWarp,
                caveWarpStrength = caveWarpStrength,
                caveWarpScale = caveWarpScale,
                enableAdditionalHeightLayer = enableAdditionalHeightLayer,
                additionalHeightOctaves = additionalHeightOctaves,
                additionalHeightScale = additionalHeightScale,
                additionalHeightLacunarity = additionalHeightLacunarity,
                additionalHeightPersistence = additionalHeightPersistence,
                additionalHeightImpact = additionalHeightImpact,
                enableAdditionalCaveLayer = enableAdditionalCaveLayer,
                additionalCaveOctaves = additionalCaveOctaves,
                additionalCaveScale = additionalCaveScale,
                additionalCaveLacunarity = additionalCaveLacunarity,
                additionalCavePersistence = additionalCavePersistence,
                additionalCaveImpact = additionalCaveImpact
            };
        }
    }
}
