using UnityEngine;
using Zenject;
using System.Collections.Generic;

public class ConstructionInstaller : MonoInstaller
{
    [SerializeField]
    private List<ConstructionFactory> _constructionFactories;

    public override void InstallBindings()
    {
        // Привязываем конкретный список фабрик из инспектора как одиночный экземпляр
        Container.Bind<List<ConstructionFactory>>().FromInstance(_constructionFactories).AsSingle();
    }
}