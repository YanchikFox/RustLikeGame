using UnityEngine;
using Zenject;

namespace TerrainSystem
{
    public class ComputeBufferManagerInstaller : MonoInstaller
    {
        [SerializeField] private ComputeBufferManager computeBufferManagerPrefab;

        public override void InstallBindings()
        {
            if (computeBufferManagerPrefab == null)
            {
                throw new System.InvalidOperationException("ComputeBufferManager prefab is not assigned.");
            }

            Container.BindInterfacesAndSelfTo<ComputeBufferManager>()
                .FromComponentInNewPrefab(computeBufferManagerPrefab)
                .AsSingle()
                .NonLazy();
        }
    }
}