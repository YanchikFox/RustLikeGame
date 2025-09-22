using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace TerrainSystem
{
    [BurstCompile]
    public struct GenerateVoxelDataJob : IJob
    {
        [WriteOnly] public NativeArray<float> densities;

        // Grid
        public int3 chunkSize;          // size.x, size.y, size.z (voxels)
        public float3 chunkWorldOrigin; // world position of chunk origin
        public float voxelSize;

        // Biome system
        public float biomeNoiseScale;
        public float biomeBlendRange;
        public int biomeCount;
        [ReadOnly] public NativeArray<float> biomeThresholds;
        [ReadOnly] public NativeArray<float> biomeGroundLevels;
        [ReadOnly] public NativeArray<float> biomeHeightImpacts;
        [ReadOnly] public NativeArray<float> biomeHeightScales;
        [ReadOnly] public NativeArray<float> biomeCaveImpacts;
        [ReadOnly] public NativeArray<float> biomeCaveScales;
        
        // Biome-specific noise parameters
        [ReadOnly] public NativeArray<int> biomeOctaves;
        [ReadOnly] public NativeArray<float> biomeLacunarity;
        [ReadOnly] public NativeArray<float> biomePersistence;
        [ReadOnly] public NativeArray<int> biomeHeightWarpEnabled;
        [ReadOnly] public NativeArray<float> biomeHeightWarpStrengths;
        [ReadOnly] public NativeArray<float> biomeHeightWarpScales;
        [ReadOnly] public NativeArray<int> biomeHeightWarpRidged;
        [ReadOnly] public NativeArray<int> biomeSecondaryHeightWarpEnabled;
        [ReadOnly] public NativeArray<float> biomeSecondaryHeightWarpStrengths;
        [ReadOnly] public NativeArray<float> biomeSecondaryHeightWarpScales;
        [ReadOnly] public NativeArray<int> biomeSecondaryHeightWarpRidged;
        [ReadOnly] public NativeArray<int> biomeCaveWarpEnabled;
        [ReadOnly] public NativeArray<float> biomeCaveWarpStrengths;
        [ReadOnly] public NativeArray<float> biomeCaveWarpScales;
        [ReadOnly] public NativeArray<int> biomeCaveWarpRidged;
        [ReadOnly] public NativeArray<int> biomeSecondaryCaveWarpEnabled;
        [ReadOnly] public NativeArray<float> biomeSecondaryCaveWarpStrengths;
        [ReadOnly] public NativeArray<float> biomeSecondaryCaveWarpScales;
        [ReadOnly] public NativeArray<int> biomeSecondaryCaveWarpRidged;
        [ReadOnly] public NativeArray<int> biomeExtraHeightLayerEnabled;
        [ReadOnly] public NativeArray<int> biomeExtraHeightOctaves;
        [ReadOnly] public NativeArray<float> biomeExtraHeightScales;
        [ReadOnly] public NativeArray<float> biomeExtraHeightLacunarity;
        [ReadOnly] public NativeArray<float> biomeExtraHeightPersistence;
        [ReadOnly] public NativeArray<float> biomeExtraHeightImpact;
        [ReadOnly] public NativeArray<int> biomeExtraCaveLayerEnabled;
        [ReadOnly] public NativeArray<int> biomeExtraCaveOctaves;
        [ReadOnly] public NativeArray<float> biomeExtraCaveScales;
        [ReadOnly] public NativeArray<float> biomeExtraCaveLacunarity;
        [ReadOnly] public NativeArray<float> biomeExtraCavePersistence;
        [ReadOnly] public NativeArray<float> biomeExtraCaveImpact;
        [ReadOnly] public NativeArray<int> biomeCliffLayerEnabled;
        [ReadOnly] public NativeArray<float> biomeCliffScales;
        [ReadOnly] public NativeArray<float> biomeCliffImpacts;
        [ReadOnly] public NativeArray<float> biomeCliffThresholds;
        [ReadOnly] public NativeArray<float> biomeCliffSharpness;
        [ReadOnly] public NativeArray<int> biomePlateauLayerEnabled;
        [ReadOnly] public NativeArray<float> biomePlateauScales;
        [ReadOnly] public NativeArray<float> biomePlateauStepHeights;
        [ReadOnly] public NativeArray<float> biomePlateauStrengths;
        [ReadOnly] public NativeArray<float> biomePlateauThresholds;
        [ReadOnly] public NativeArray<float> biomePlateauSharpness;
        [ReadOnly] public NativeArray<int> biomeSurfaceCaveMaskEnabled;
        [ReadOnly] public NativeArray<float> biomeSurfaceCaveMaskScales;
        [ReadOnly] public NativeArray<float> biomeSurfaceCaveMaskThresholds;
        [ReadOnly] public NativeArray<float> biomeSurfaceCaveMaskStrengths;
        [ReadOnly] public NativeArray<float> biomeSurfaceCaveMaskFalloffs;

        // Multi-layered noise parameters
        public float temperatureNoiseScale;
        public float humidityNoiseScale;
        public float riverNoiseScale;
        public float riverThreshold;
        public float riverDepth;

        public void Execute()
        {
            int width = chunkSize.x + 1;
            int height = chunkSize.y + 1;
            int depth = chunkSize.z + 1;

            int index = 0;
            for (int z = 0; z <= chunkSize.z; z++)
            {
                for (int y = 0; y <= chunkSize.y; y++)
                {
                    for (int x = 0; x <= chunkSize.x; x++)
                    {
                        float3 worldPos = chunkWorldOrigin + new float3(x, y, z) * voxelSize;
                        float d = CalculateDensity(worldPos);
                        densities[index++] = math.clamp(d, -1f, 1f);
                    }
                }
            }
        }

        private static readonly float2 HeightWarpOffset = new float2(37.21f, 17.31f);
        private static readonly float2 SecondaryHeightWarpOffsetA = new float2(81.53f, 23.11f);
        private static readonly float2 SecondaryHeightWarpOffsetB = new float2(12.37f, 64.29f);
        private static readonly float3 CaveWarpOffsetA = new float3(19.13f, 7.73f, 3.37f);
        private static readonly float3 CaveWarpOffsetB = new float3(5.21f, 9.18f, 12.89f);
        private static readonly float3 SecondaryCaveWarpOffsetA = new float3(41.12f, 3.91f, 27.45f);
        private static readonly float3 SecondaryCaveWarpOffsetB = new float3(13.87f, 52.63f, 8.14f);
        private static readonly float3 SecondaryCaveWarpOffsetC = new float3(71.22f, 34.55f, 19.08f);

        private static float ApplyRidged(float value)
        {
            float ridged = 1f - math.abs(value);
            return ridged * 2f - 1f;
        }

        private float CalculateDensity(float3 worldPos)
        {
            float biomeValue = noise.snoise(worldPos.xz * biomeNoiseScale);
            float temperatureValue = noise.snoise(worldPos.xz * temperatureNoiseScale);
            float humidityValue = noise.snoise(worldPos.xz * humidityNoiseScale);

            int currentBiomeIndex = 0;
            for (int i = 0; i < biomeCount; i++)
            {
                if (biomeValue >= biomeThresholds[i])
                {
                    currentBiomeIndex = i;
                }
                else
                {
                    break;
                }
            }
            int nextBiomeIndex = math.min(currentBiomeIndex + 1, biomeCount - 1);

            float blendFactor = 0f;
            if (currentBiomeIndex != nextBiomeIndex)
            {
                float range = biomeThresholds[nextBiomeIndex] - biomeThresholds[currentBiomeIndex];
                if (range > 0.0001f)
                {
                    blendFactor = math.saturate((biomeValue - biomeThresholds[currentBiomeIndex]) / range);
                }
            }

            float baseGroundLevel = math.lerp(biomeGroundLevels[currentBiomeIndex], biomeGroundLevels[nextBiomeIndex], blendFactor);
            float baseHeightImpact = math.lerp(biomeHeightImpacts[currentBiomeIndex], biomeHeightImpacts[nextBiomeIndex], blendFactor);
            float baseHeightScale = math.lerp(biomeHeightScales[currentBiomeIndex], biomeHeightScales[nextBiomeIndex], blendFactor);
            float baseCaveImpact = math.lerp(biomeCaveImpacts[currentBiomeIndex], biomeCaveImpacts[nextBiomeIndex], blendFactor);
            float baseCaveScale = math.lerp(biomeCaveScales[currentBiomeIndex], biomeCaveScales[nextBiomeIndex], blendFactor);
            int finalOctaves = (int)math.round(math.lerp(biomeOctaves[currentBiomeIndex], biomeOctaves[nextBiomeIndex], blendFactor));
            float finalLacunarity = math.lerp(biomeLacunarity[currentBiomeIndex], biomeLacunarity[nextBiomeIndex], blendFactor);
            float finalPersistence = math.lerp(biomePersistence[currentBiomeIndex], biomePersistence[nextBiomeIndex], blendFactor);

            float heightWarpToggle = math.lerp((float)biomeHeightWarpEnabled[currentBiomeIndex], (float)biomeHeightWarpEnabled[nextBiomeIndex], blendFactor);
            float heightWarpStrength = math.lerp(biomeHeightWarpStrengths[currentBiomeIndex], biomeHeightWarpStrengths[nextBiomeIndex], blendFactor);
            float heightWarpScale = math.lerp(biomeHeightWarpScales[currentBiomeIndex], biomeHeightWarpScales[nextBiomeIndex], blendFactor);
            float heightWarpRidgedToggle = math.lerp((float)biomeHeightWarpRidged[currentBiomeIndex], (float)biomeHeightWarpRidged[nextBiomeIndex], blendFactor);
            float secondaryHeightWarpToggle = math.lerp((float)biomeSecondaryHeightWarpEnabled[currentBiomeIndex], (float)biomeSecondaryHeightWarpEnabled[nextBiomeIndex], blendFactor);
            float secondaryHeightWarpStrength = math.lerp(biomeSecondaryHeightWarpStrengths[currentBiomeIndex], biomeSecondaryHeightWarpStrengths[nextBiomeIndex], blendFactor);
            float secondaryHeightWarpScale = math.lerp(biomeSecondaryHeightWarpScales[currentBiomeIndex], biomeSecondaryHeightWarpScales[nextBiomeIndex], blendFactor);
            float secondaryHeightWarpRidgedToggle = math.lerp((float)biomeSecondaryHeightWarpRidged[currentBiomeIndex], (float)biomeSecondaryHeightWarpRidged[nextBiomeIndex], blendFactor);
            float caveWarpToggle = math.lerp((float)biomeCaveWarpEnabled[currentBiomeIndex], (float)biomeCaveWarpEnabled[nextBiomeIndex], blendFactor);
            float caveWarpStrength = math.lerp(biomeCaveWarpStrengths[currentBiomeIndex], biomeCaveWarpStrengths[nextBiomeIndex], blendFactor);
            float caveWarpScale = math.lerp(biomeCaveWarpScales[currentBiomeIndex], biomeCaveWarpScales[nextBiomeIndex], blendFactor);
            float caveWarpRidgedToggle = math.lerp((float)biomeCaveWarpRidged[currentBiomeIndex], (float)biomeCaveWarpRidged[nextBiomeIndex], blendFactor);
            float secondaryCaveWarpToggle = math.lerp((float)biomeSecondaryCaveWarpEnabled[currentBiomeIndex], (float)biomeSecondaryCaveWarpEnabled[nextBiomeIndex], blendFactor);
            float secondaryCaveWarpStrength = math.lerp(biomeSecondaryCaveWarpStrengths[currentBiomeIndex], biomeSecondaryCaveWarpStrengths[nextBiomeIndex], blendFactor);
            float secondaryCaveWarpScale = math.lerp(biomeSecondaryCaveWarpScales[currentBiomeIndex], biomeSecondaryCaveWarpScales[nextBiomeIndex], blendFactor);
            float secondaryCaveWarpRidgedToggle = math.lerp((float)biomeSecondaryCaveWarpRidged[currentBiomeIndex], (float)biomeSecondaryCaveWarpRidged[nextBiomeIndex], blendFactor);
            float extraHeightToggle = math.lerp((float)biomeExtraHeightLayerEnabled[currentBiomeIndex], (float)biomeExtraHeightLayerEnabled[nextBiomeIndex], blendFactor);
            int extraHeightOctaves = (int)math.round(math.lerp(biomeExtraHeightOctaves[currentBiomeIndex], biomeExtraHeightOctaves[nextBiomeIndex], blendFactor));
            float extraHeightScale = math.lerp(biomeExtraHeightScales[currentBiomeIndex], biomeExtraHeightScales[nextBiomeIndex], blendFactor);
            float extraHeightLacunarity = math.lerp(biomeExtraHeightLacunarity[currentBiomeIndex], biomeExtraHeightLacunarity[nextBiomeIndex], blendFactor);
            float extraHeightPersistence = math.lerp(biomeExtraHeightPersistence[currentBiomeIndex], biomeExtraHeightPersistence[nextBiomeIndex], blendFactor);
            float extraHeightImpact = math.lerp(biomeExtraHeightImpact[currentBiomeIndex], biomeExtraHeightImpact[nextBiomeIndex], blendFactor);
            float extraCaveToggle = math.lerp((float)biomeExtraCaveLayerEnabled[currentBiomeIndex], (float)biomeExtraCaveLayerEnabled[nextBiomeIndex], blendFactor);
            int extraCaveOctaves = (int)math.round(math.lerp(biomeExtraCaveOctaves[currentBiomeIndex], biomeExtraCaveOctaves[nextBiomeIndex], blendFactor));
            float extraCaveScale = math.lerp(biomeExtraCaveScales[currentBiomeIndex], biomeExtraCaveScales[nextBiomeIndex], blendFactor);
            float extraCaveLacunarity = math.lerp(biomeExtraCaveLacunarity[currentBiomeIndex], biomeExtraCaveLacunarity[nextBiomeIndex], blendFactor);
            float extraCavePersistence = math.lerp(biomeExtraCavePersistence[currentBiomeIndex], biomeExtraCavePersistence[nextBiomeIndex], blendFactor);
            float extraCaveImpact = math.lerp(biomeExtraCaveImpact[currentBiomeIndex], biomeExtraCaveImpact[nextBiomeIndex], blendFactor);
            float cliffToggle = math.lerp((float)biomeCliffLayerEnabled[currentBiomeIndex], (float)biomeCliffLayerEnabled[nextBiomeIndex], blendFactor);
            float cliffScale = math.lerp(biomeCliffScales[currentBiomeIndex], biomeCliffScales[nextBiomeIndex], blendFactor);
            float cliffImpact = math.lerp(biomeCliffImpacts[currentBiomeIndex], biomeCliffImpacts[nextBiomeIndex], blendFactor);
            float cliffThreshold = math.lerp(biomeCliffThresholds[currentBiomeIndex], biomeCliffThresholds[nextBiomeIndex], blendFactor);
            float cliffSharpness = math.lerp(biomeCliffSharpness[currentBiomeIndex], biomeCliffSharpness[nextBiomeIndex], blendFactor);
            float plateauToggle = math.lerp((float)biomePlateauLayerEnabled[currentBiomeIndex], (float)biomePlateauLayerEnabled[nextBiomeIndex], blendFactor);
            float plateauScale = math.lerp(biomePlateauScales[currentBiomeIndex], biomePlateauScales[nextBiomeIndex], blendFactor);
            float plateauStepHeight = math.lerp(biomePlateauStepHeights[currentBiomeIndex], biomePlateauStepHeights[nextBiomeIndex], blendFactor);
            float plateauStrength = math.lerp(biomePlateauStrengths[currentBiomeIndex], biomePlateauStrengths[nextBiomeIndex], blendFactor);
            float plateauThreshold = math.lerp(biomePlateauThresholds[currentBiomeIndex], biomePlateauThresholds[nextBiomeIndex], blendFactor);
            float plateauSharpness = math.lerp(biomePlateauSharpness[currentBiomeIndex], biomePlateauSharpness[nextBiomeIndex], blendFactor);
            float surfaceCaveMaskToggle = math.lerp((float)biomeSurfaceCaveMaskEnabled[currentBiomeIndex], (float)biomeSurfaceCaveMaskEnabled[nextBiomeIndex], blendFactor);
            float surfaceCaveMaskScale = math.lerp(biomeSurfaceCaveMaskScales[currentBiomeIndex], biomeSurfaceCaveMaskScales[nextBiomeIndex], blendFactor);
            float surfaceCaveMaskThreshold = math.lerp(biomeSurfaceCaveMaskThresholds[currentBiomeIndex], biomeSurfaceCaveMaskThresholds[nextBiomeIndex], blendFactor);
            float surfaceCaveMaskStrength = math.lerp(biomeSurfaceCaveMaskStrengths[currentBiomeIndex], biomeSurfaceCaveMaskStrengths[nextBiomeIndex], blendFactor);
            float surfaceCaveMaskFalloff = math.lerp(biomeSurfaceCaveMaskFalloffs[currentBiomeIndex], biomeSurfaceCaveMaskFalloffs[nextBiomeIndex], blendFactor);

            float groundLevelModifier = 0f;
            float heightImpactModifier = 1f;
            float caveImpactModifier = 1f;

            if (temperatureValue > 0.6f)
            {
                float hotFactor = math.saturate((temperatureValue - 0.6f) / 0.4f);
                heightImpactModifier *= math.lerp(1f, 0.3f, hotFactor);
            }

            if (humidityValue > 0.5f)
            {
                float humidFactor = math.saturate((humidityValue - 0.5f) / 0.5f);
                heightImpactModifier *= math.lerp(1f, 1.3f, humidFactor);
                groundLevelModifier += math.lerp(0f, -2f, humidFactor);
            }

            if (temperatureValue < -0.3f && humidityValue < 0f)
            {
                float tundraFactor = math.saturate((-temperatureValue - 0.3f) / 0.7f) * math.saturate(-humidityValue);
                caveImpactModifier *= math.lerp(1f, 1.5f, tundraFactor);
            }

            float finalGroundLevel = baseGroundLevel + groundLevelModifier;
            float finalHeightImpact = baseHeightImpact * heightImpactModifier;
            float finalCaveImpact = baseCaveImpact * caveImpactModifier;

            float2 heightSamplePos = worldPos.xz;
            bool useHeightWarp = heightWarpToggle > 0.5f && math.abs(heightWarpStrength) > 0.0001f && math.abs(heightWarpScale) > 0.0001f;
            bool heightWarpUseRidged = heightWarpRidgedToggle > 0.5f;
            if (useHeightWarp)
            {
                float2 warpInput = heightSamplePos * heightWarpScale;
                float warpX = noise.snoise(warpInput);
                float warpZ = noise.snoise(warpInput + HeightWarpOffset);
                if (heightWarpUseRidged)
                {
                    warpX = ApplyRidged(warpX);
                    warpZ = ApplyRidged(warpZ);
                }
                heightSamplePos += new float2(warpX, warpZ) * heightWarpStrength;
            }

            bool useSecondaryHeightWarp = secondaryHeightWarpToggle > 0.5f && math.abs(secondaryHeightWarpStrength) > 0.0001f && math.abs(secondaryHeightWarpScale) > 0.0001f;
            bool secondaryHeightWarpUseRidged = secondaryHeightWarpRidgedToggle > 0.5f;
            if (useSecondaryHeightWarp)
            {
                float2 warpInput = heightSamplePos * secondaryHeightWarpScale;
                float warpX = noise.snoise(warpInput + SecondaryHeightWarpOffsetA);
                float warpZ = noise.snoise(warpInput + SecondaryHeightWarpOffsetB);
                if (secondaryHeightWarpUseRidged)
                {
                    warpX = ApplyRidged(warpX);
                    warpZ = ApplyRidged(warpZ);
                }
                heightSamplePos += new float2(warpX, warpZ) * secondaryHeightWarpStrength;
            }

            float sum2D = FractalNoise2D(heightSamplePos, finalOctaves, baseHeightScale, finalLacunarity, finalPersistence);
            float heightContribution = sum2D * finalHeightImpact;

            bool useExtraHeightLayer = extraHeightToggle > 0.5f && extraHeightOctaves > 0 && math.abs(extraHeightImpact) > 0.0001f && math.abs(extraHeightScale) > 0.0001f;
            if (useExtraHeightLayer)
            {
                float extraHeightNoise = FractalNoise2D(heightSamplePos, extraHeightOctaves, extraHeightScale, extraHeightLacunarity, extraHeightPersistence);
                heightContribution += extraHeightNoise * extraHeightImpact;
            }

            bool useCliffLayer = cliffToggle > 0.5f && math.abs(cliffImpact) > 0.0001f && math.abs(cliffScale) > 0.0001f;
            if (useCliffLayer)
            {
                float cliffNoise = noise.snoise(heightSamplePos * cliffScale);
                float ridgedCliff = 1f - math.abs(cliffNoise);
                float denom = math.max(1f - cliffThreshold, 0.0001f);
                float normalized = math.saturate((ridgedCliff - cliffThreshold) / denom);
                float sharpness = math.max(cliffSharpness, 0.0001f);
                float mask = math.pow(normalized, sharpness);
                heightContribution -= mask * cliffImpact;
            }

            bool usePlateauLayer = plateauToggle > 0.5f && plateauStepHeight > 0.0001f && math.abs(plateauScale) > 0.0001f && math.abs(plateauStrength) > 0.0001f;
            if (usePlateauLayer)
            {
                float plateauNoise = noise.snoise(heightSamplePos * plateauScale);
                float denom = math.max(1f - plateauThreshold, 0.0001f);
                float normalized = math.saturate((plateauNoise - plateauThreshold) / denom);
                float sharpness = math.max(plateauSharpness, 0.0001f);
                float mask = math.pow(normalized, sharpness);
                float plateauStrengthClamped = math.saturate(plateauStrength);
                float target = math.round(heightContribution / plateauStepHeight) * plateauStepHeight;
                heightContribution = math.lerp(heightContribution, target, mask * plateauStrengthClamped);
            }

            float surfaceHeight = finalGroundLevel + heightContribution;
            float surfaceDensity = worldPos.y - surfaceHeight;
            float finalDensity = surfaceDensity;

            float3 caveSamplePos = worldPos;
            bool useCaveWarp = caveWarpToggle > 0.5f && math.abs(caveWarpStrength) > 0.0001f && math.abs(caveWarpScale) > 0.0001f;
            bool caveWarpUseRidged = caveWarpRidgedToggle > 0.5f;
            if (useCaveWarp)
            {
                float3 warpInput = caveSamplePos * caveWarpScale;
                float warpX = noise.snoise(warpInput);
                float warpY = noise.snoise(warpInput + CaveWarpOffsetA);
                float warpZ = noise.snoise(warpInput + CaveWarpOffsetB);
                if (caveWarpUseRidged)
                {
                    warpX = ApplyRidged(warpX);
                    warpY = ApplyRidged(warpY);
                    warpZ = ApplyRidged(warpZ);
                }
                caveSamplePos += new float3(warpX, warpY, warpZ) * caveWarpStrength;
            }

            bool useSecondaryCaveWarp = secondaryCaveWarpToggle > 0.5f && math.abs(secondaryCaveWarpStrength) > 0.0001f && math.abs(secondaryCaveWarpScale) > 0.0001f;
            bool secondaryCaveWarpUseRidged = secondaryCaveWarpRidgedToggle > 0.5f;
            if (useSecondaryCaveWarp)
            {
                float3 warpInput = caveSamplePos * secondaryCaveWarpScale;
                float warpX = noise.snoise(warpInput + SecondaryCaveWarpOffsetA);
                float warpY = noise.snoise(warpInput + SecondaryCaveWarpOffsetB);
                float warpZ = noise.snoise(warpInput + SecondaryCaveWarpOffsetC);
                if (secondaryCaveWarpUseRidged)
                {
                    warpX = ApplyRidged(warpX);
                    warpY = ApplyRidged(warpY);
                    warpZ = ApplyRidged(warpZ);
                }
                caveSamplePos += new float3(warpX, warpY, warpZ) * secondaryCaveWarpStrength;
            }

            if (surfaceDensity < 0f)
            {
                float depthFactor = math.min(-surfaceDensity / 10f, 1f);
                float sum3D = FractalNoise3D(caveSamplePos, finalOctaves, baseCaveScale, finalLacunarity, finalPersistence);
                finalDensity -= sum3D * finalCaveImpact * depthFactor;

                bool useExtraCaveLayer = extraCaveToggle > 0.5f && extraCaveOctaves > 0 && math.abs(extraCaveImpact) > 0.0001f && math.abs(extraCaveScale) > 0.0001f;
                if (useExtraCaveLayer)
                {
                    float extraCaveNoise = FractalNoise3D(caveSamplePos, extraCaveOctaves, extraCaveScale, extraCaveLacunarity, extraCavePersistence);
                    finalDensity -= extraCaveNoise * extraCaveImpact * depthFactor;
                }
            }

            bool useSurfaceCaveMask = surfaceCaveMaskToggle > 0.5f && math.abs(surfaceCaveMaskStrength) > 0.0001f && math.abs(surfaceCaveMaskScale) > 0.0001f && surfaceCaveMaskFalloff > 0.0001f;
            if (useSurfaceCaveMask)
            {
                float maskNoise = noise.snoise(worldPos.xz * surfaceCaveMaskScale);
                float denom = math.max(1f - surfaceCaveMaskThreshold, 0.0001f);
                float mask = math.saturate((maskNoise - surfaceCaveMaskThreshold) / denom);
                float depthAttenuation = math.saturate(1f - math.abs(surfaceDensity) / surfaceCaveMaskFalloff);
                finalDensity -= surfaceCaveMaskStrength * mask * depthAttenuation;
            }

            float riverNoiseValue = noise.snoise(worldPos.xz * riverNoiseScale);
            float riverProximity = math.abs(riverNoiseValue);

            if (riverProximity < riverThreshold)
            {
                float carveFactor = 1f - math.smoothstep(0f, riverThreshold, riverProximity);
                if (surfaceDensity < 0f && surfaceDensity > -20f)
                {
                    finalDensity -= riverDepth * carveFactor;
                }
            }

            return finalDensity;
        }

        private static float FractalNoise2D(float2 pos, int octaves, float frequency, float lacunarity, float persistence)
        {
            if (octaves <= 0)
            {
                return 0f;
            }

            float sum = 0f;
            float maxAmplitude = 0f;
            float amp = 1f;
            float freq = frequency;

            for (int i = 0; i < octaves; i++)
            {
                maxAmplitude += amp;
                amp *= persistence;
            }

            if (maxAmplitude <= 0f)
            {
                return 0f;
            }

            amp = 1f;
            freq = frequency;
            for (int i = 0; i < octaves; i++)
            {
                sum += noise.snoise(pos * freq) * amp;
                freq *= lacunarity;
                amp *= persistence;
            }

            return sum / maxAmplitude;
        }

        private static float FractalNoise3D(float3 pos, int octaves, float frequency, float lacunarity, float persistence)
        {
            if (octaves <= 0)
            {
                return 0f;
            }

            float sum = 0f;
            float maxAmplitude = 0f;
            float amp = 1f;
            float freq = frequency;

            for (int i = 0; i < octaves; i++)
            {
                maxAmplitude += amp;
                amp *= persistence;
            }

            if (maxAmplitude <= 0f)
            {
                return 0f;
            }

            amp = 1f;
            freq = frequency;
            for (int i = 0; i < octaves; i++)
            {
                sum += noise.snoise(pos * freq) * amp;
                freq *= lacunarity;
                amp *= persistence;
            }

            return sum / maxAmplitude;
        }
    }
}
