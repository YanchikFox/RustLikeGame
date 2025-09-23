using UnityEngine;
using Zenject;
using System.Collections.Generic;

public class ConstructionInstaller : MonoInstaller
{
    [SerializeField]
    private List<ConstructionFactory> _constructionFactories;

    public override void InstallBindings()
    {
        // Bind the specific list of factories from inspector as a singleton instance
        Container.Bind<List<ConstructionFactory>>().FromInstance(_constructionFactories).AsSingle();
    }
}