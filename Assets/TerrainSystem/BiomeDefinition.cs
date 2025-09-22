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
        private bool heightWarpUseRidged = false;

        [SerializeField]
        private bool enableSecondaryHeightDomainWarp = false;

        [SerializeField]
        private float secondaryHeightWarpStrength = 0f;

        [SerializeField]
        private float secondaryHeightWarpScale = 0.025f;

        [SerializeField]
        private bool secondaryHeightWarpUseRidged = false;

        [SerializeField]
        private bool enableCaveDomainWarp = false;

        [SerializeField]
        private float caveWarpStrength = 0f;

        [SerializeField]
        private float caveWarpScale = 0.08f;

        [SerializeField]
        private bool caveWarpUseRidged = false;

        [SerializeField]
        private bool enableSecondaryCaveDomainWarp = false;

        [SerializeField]
        private float secondaryCaveWarpStrength = 0f;

        [SerializeField]
        private float secondaryCaveWarpScale = 0.04f;

        [SerializeField]
        private bool secondaryCaveWarpUseRidged = false;

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

        [Header("Cliff Layer")]
        [SerializeField]
        private bool enableCliffLayer = false;

        [SerializeField]
        private float cliffNoiseScale = 0.05f;

        [SerializeField]
        private float cliffImpact = 0f;

        [SerializeField]
        [Range(-1f, 1f)]
        private float cliffThreshold = 0.2f;

        [SerializeField]
        [Min(0.01f)]
        private float cliffSharpness = 1f;

        [Header("Plateau Layer")]
        [SerializeField]
        private bool enablePlateauLayer = false;

        [SerializeField]
        private float plateauNoiseScale = 0.04f;

        [SerializeField]
        [Min(0.01f)]
        private float plateauStepHeight = 3f;

        [SerializeField]
        [Range(0f, 1f)]
        private float plateauStrength = 0f;

        [SerializeField]
        [Range(-1f, 1f)]
        private float plateauThreshold = 0f;

        [SerializeField]
        [Min(0.01f)]
        private float plateauSharpness = 1f;

        [Header("Surface Cave Mask")]
        [SerializeField]
        private bool enableSurfaceCaveMask = false;

        [SerializeField]
        private float surfaceCaveMaskScale = 0.06f;

        [SerializeField]
        [Range(-1f, 1f)]
        private float surfaceCaveMaskThreshold = 0.1f;

        [SerializeField]
        private float surfaceCaveMaskStrength = 0f;

        [SerializeField]
        [Min(0.01f)]
        private float surfaceCaveMaskFalloff = 6f;

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
        public bool HeightWarpUseRidged => heightWarpUseRidged;
        public bool EnableSecondaryHeightDomainWarp => enableSecondaryHeightDomainWarp;
        public float SecondaryHeightWarpStrength => secondaryHeightWarpStrength;
        public float SecondaryHeightWarpScale => secondaryHeightWarpScale;
        public bool SecondaryHeightWarpUseRidged => secondaryHeightWarpUseRidged;
        public bool EnableCaveDomainWarp => enableCaveDomainWarp;
        public float CaveWarpStrength => caveWarpStrength;
        public float CaveWarpScale => caveWarpScale;
        public bool CaveWarpUseRidged => caveWarpUseRidged;
        public bool EnableSecondaryCaveDomainWarp => enableSecondaryCaveDomainWarp;
        public float SecondaryCaveWarpStrength => secondaryCaveWarpStrength;
        public float SecondaryCaveWarpScale => secondaryCaveWarpScale;
        public bool SecondaryCaveWarpUseRidged => secondaryCaveWarpUseRidged;
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
        public bool EnableCliffLayer => enableCliffLayer;
        public float CliffNoiseScale => cliffNoiseScale;
        public float CliffImpact => cliffImpact;
        public float CliffThreshold => cliffThreshold;
        public float CliffSharpness => cliffSharpness;
        public bool EnablePlateauLayer => enablePlateauLayer;
        public float PlateauNoiseScale => plateauNoiseScale;
        public float PlateauStepHeight => plateauStepHeight;
        public float PlateauStrength => plateauStrength;
        public float PlateauThreshold => plateauThreshold;
        public float PlateauSharpness => plateauSharpness;
        public bool EnableSurfaceCaveMask => enableSurfaceCaveMask;
        public float SurfaceCaveMaskScale => surfaceCaveMaskScale;
        public float SurfaceCaveMaskThreshold => surfaceCaveMaskThreshold;
        public float SurfaceCaveMaskStrength => surfaceCaveMaskStrength;
        public float SurfaceCaveMaskFalloff => surfaceCaveMaskFalloff;

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
                heightWarpUseRidged = heightWarpUseRidged,
                enableSecondaryHeightDomainWarp = enableSecondaryHeightDomainWarp,
                secondaryHeightWarpStrength = secondaryHeightWarpStrength,
                secondaryHeightWarpScale = secondaryHeightWarpScale,
                secondaryHeightWarpUseRidged = secondaryHeightWarpUseRidged,
                enableCaveDomainWarp = enableCaveDomainWarp,
                caveWarpStrength = caveWarpStrength,
                caveWarpScale = caveWarpScale,
                caveWarpUseRidged = caveWarpUseRidged,
                enableSecondaryCaveDomainWarp = enableSecondaryCaveDomainWarp,
                secondaryCaveWarpStrength = secondaryCaveWarpStrength,
                secondaryCaveWarpScale = secondaryCaveWarpScale,
                secondaryCaveWarpUseRidged = secondaryCaveWarpUseRidged,
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
                additionalCaveImpact = additionalCaveImpact,
                enableCliffLayer = enableCliffLayer,
                cliffNoiseScale = cliffNoiseScale,
                cliffImpact = cliffImpact,
                cliffThreshold = cliffThreshold,
                cliffSharpness = cliffSharpness,
                enablePlateauLayer = enablePlateauLayer,
                plateauNoiseScale = plateauNoiseScale,
                plateauStepHeight = plateauStepHeight,
                plateauStrength = plateauStrength,
                plateauThreshold = plateauThreshold,
                plateauSharpness = plateauSharpness,
                enableSurfaceCaveMask = enableSurfaceCaveMask,
                surfaceCaveMaskScale = surfaceCaveMaskScale,
                surfaceCaveMaskThreshold = surfaceCaveMaskThreshold,
                surfaceCaveMaskStrength = surfaceCaveMaskStrength,
                surfaceCaveMaskFalloff = surfaceCaveMaskFalloff
            };
        }
    }
}
