using UnityEngine;
using Zenject;

public class PlayerInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        // 1. Привязываем PlayerInput как единичный экземпляр.
        Container.Bind<PlayerInput>().AsSingle();

        // 2. Привязываем InputManager из иерархии как единичный экземпляр.
        Container.Bind<InputManager>().FromComponentInHierarchy().AsSingle();
        
        // 3. НОВОЕ: Привязываем FirstPersonController из сцены.
        // Теперь UIManager и другие классы смогут его получить.
        Container.Bind<FirstPersonController>().FromComponentInHierarchy().AsSingle();

        // 4. НОВОЕ: Привязываем UIManager из сцены.
        Container.Bind<UIManager>().FromComponentInHierarchy().AsSingle();
        
        Container.Bind<InventoryManager>().FromComponentInHierarchy().AsSingle();
    }
}
