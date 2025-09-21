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

        private float CalculateDensity(float3 worldPos)
        {
            // --- Step 1: Calculate all biome parameters ---
            
            // Get noise values for biomes, temperature, and humidity
            float biomeValue = noise.snoise(worldPos.xz * biomeNoiseScale);
            float temperatureValue = noise.snoise(worldPos.xz * temperatureNoiseScale);
            float humidityValue = noise.snoise(worldPos.xz * humidityNoiseScale);

            // Find current and next biome for interpolation
            int currentBiomeIndex = 0;
            for (int i = 0; i < biomeCount; i++)
            {
                if (biomeValue >= biomeThresholds[i]) { currentBiomeIndex = i; }
                else { break; }
            }
            int nextBiomeIndex = math.min(currentBiomeIndex + 1, biomeCount - 1);

            // Calculate blend factor between biomes
            float blendFactor = 0.0f;
            if (currentBiomeIndex != nextBiomeIndex)
            {
                float range = biomeThresholds[nextBiomeIndex] - biomeThresholds[currentBiomeIndex];
                if (range > 0.0001f)
                {
                    blendFactor = math.saturate((biomeValue - biomeThresholds[currentBiomeIndex]) / range);
                }
            }

            // Interpolate base parameters between biomes
            float baseGroundLevel = math.lerp(biomeGroundLevels[currentBiomeIndex], biomeGroundLevels[nextBiomeIndex], blendFactor);
            float baseHeightImpact = math.lerp(biomeHeightImpacts[currentBiomeIndex], biomeHeightImpacts[nextBiomeIndex], blendFactor);
            float baseHeightScale = math.lerp(biomeHeightScales[currentBiomeIndex], biomeHeightScales[nextBiomeIndex], blendFactor);
            float baseCaveImpact = math.lerp(biomeCaveImpacts[currentBiomeIndex], biomeCaveImpacts[nextBiomeIndex], blendFactor);
            float baseCaveScale = math.lerp(biomeCaveScales[currentBiomeIndex], biomeCaveScales[nextBiomeIndex], blendFactor);
            int finalOctaves = (int)math.round(math.lerp(biomeOctaves[currentBiomeIndex], biomeOctaves[nextBiomeIndex], blendFactor));
            float finalLacunarity = math.lerp(biomeLacunarity[currentBiomeIndex], biomeLacunarity[nextBiomeIndex], blendFactor);
            float finalPersistence = math.lerp(biomePersistence[currentBiomeIndex], biomePersistence[nextBiomeIndex], blendFactor);

            // --- Step 2: Apply temperature and humidity modifiers ---

            float groundLevelModifier = 0f;
            float heightImpactModifier = 1f;
            float caveImpactModifier = 1f;

            // Temperature modifiers
            if (temperatureValue > 0.6f)
            {
                float hotFactor = math.saturate((temperatureValue - 0.6f) / 0.4f);
                heightImpactModifier *= math.lerp(1f, 0.3f, hotFactor); // Reduce height variation in hot areas
            }

            // Humidity modifiers
            if (humidityValue > 0.5f)
            {
                float humidFactor = math.saturate((humidityValue - 0.5f) / 0.5f);
                heightImpactModifier *= math.lerp(1f, 1.3f, humidFactor); // Increase height variation in humid areas
                groundLevelModifier += math.lerp(0f, -2.0f, humidFactor); // Lower ground in humid areas
            }
            
            // Tundra modifiers (cold and dry)
            if (temperatureValue < -0.3f && humidityValue < 0.0f)
            {
                float tundraFactor = math.saturate((-temperatureValue - 0.3f) / 0.7f) * math.saturate(-humidityValue);
                caveImpactModifier *= math.lerp(1f, 1.5f, tundraFactor); // Increase cave formation in tundra
            }

            // --- Step 3: Calculate final parameters ---

            float finalGroundLevel = baseGroundLevel + groundLevelModifier;
            float finalHeightImpact = baseHeightImpact * heightImpactModifier;
            float finalCaveImpact = baseCaveImpact * caveImpactModifier;

            // --- Step 4: Calculate 2D heightmap noise and create surface ---
            
            // Calculate 2D heightmap noise with normalization
            float sum2D = 0f;
            float maxAmplitude2D = 0f;
            float freq = baseHeightScale;
            float amp = 1f;
            
            // Calculate maximum possible amplitude for normalization
            for (int i = 0; i < finalOctaves; i++)
            {
                maxAmplitude2D += amp;
                amp *= finalPersistence;
            }
            
            // Compute the actual 2D noise
            freq = baseHeightScale;
            amp = 1f;
            for (int i = 0; i < finalOctaves; i++)
            {
                sum2D += noise.snoise(worldPos.xz * freq) * amp;
                freq *= finalLacunarity;
                amp *= finalPersistence;
            }
            
            // Normalize the 2D noise sum
            sum2D /= maxAmplitude2D;
            
            // Calculate surface density using ONLY the 2D noise.
            // Below the surface we want negative values (solid), above positive (air).
            float surfaceHeight = finalGroundLevel + (sum2D * finalHeightImpact);
            float surfaceDensity = worldPos.y - surfaceHeight;
            float finalDensity = surfaceDensity; // Start with surface density
            
            // --- Step 5: Conditional cave carving (only underground) ---
            
            // Only apply cave carving if we're underground
            if (surfaceDensity < 0)
            {
                // Calculate 3D cave noise with normalization
                float sum3D = 0f;
                float maxAmplitude3D = 0f;
                
                // Calculate maximum possible amplitude for normalization
                freq = baseCaveScale;
                amp = 1f;
                for (int i = 0; i < finalOctaves; i++)
                {
                    maxAmplitude3D += amp;
                    amp *= finalPersistence;
                }
                
                // Compute the actual 3D noise
                freq = baseCaveScale;
                amp = 1f;
                for (int i = 0; i < finalOctaves; i++)
                {
                    sum3D += noise.snoise(worldPos * freq) * amp;
                    freq *= finalLacunarity;
                    amp *= finalPersistence;
                }
                
                // Normalize the 3D noise sum
                sum3D /= maxAmplitude3D;
                
                // Apply cave carving - intensity increases with depth
                float depthFactor = math.min(-surfaceDensity / 10f, 1f); // Scale cave intensity with depth
                finalDensity -= (sum3D * finalCaveImpact * depthFactor);
            }
            
            // --- Step 6: River carving (last step) ---
            
            float riverNoiseValue = noise.snoise(worldPos.xz * riverNoiseScale);
            float riverProximity = math.abs(riverNoiseValue); // How close to a river center
            
            if (riverProximity < riverThreshold)
            {
                // This is a river location
                float carveFactor = 1.0f - math.smoothstep(0, riverThreshold, riverProximity);
                
                // Only apply river carving near the surface
                // Use surfaceDensity (pre-cave) to determine if we're near the surface
                if (surfaceDensity < 0 && surfaceDensity > -20) 
                {
                    finalDensity -= riverDepth * carveFactor;
                }
            }
            
            return finalDensity;
        }
    }
}