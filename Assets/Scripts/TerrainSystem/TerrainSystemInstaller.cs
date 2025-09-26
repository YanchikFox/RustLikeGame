using UnityEngine;
using Zenject;

namespace TerrainSystem
{
    public class TerrainSystemInstaller : MonoInstaller
    {
        [SerializeField] private TerrainSettingsAsset terrainSettings;

        public override void InstallBindings()
        {
            if (terrainSettings != null)
            {
                Container.Bind<ITerrainSettings>()
                    .FromInstance(terrainSettings)
                    .AsSingle();
            }

            Container.Bind<ITerrainLogger>()
                .To<UnityTerrainLogger>()
                .AsSingle();

            Container.Bind<TerrainManager>()
                .FromComponentInHierarchy()
                .AsSingle();

            Container.Bind<PlayerInteraction>()
                .FromComponentInHierarchy()
                .AsSingle();
        }
    }
}