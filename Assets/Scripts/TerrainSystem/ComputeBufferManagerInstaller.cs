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

            Container.Bind<ComputeBufferManager>()
                .FromComponentInNewPrefab(computeBufferManagerPrefab)
                .AsSingle()
                .NonLazy();

            Container.Bind<IComputeBufferPool>()
                .To<ComputeBufferManager>()
                .FromResolve()
                .AsSingle();
        }
    }
}