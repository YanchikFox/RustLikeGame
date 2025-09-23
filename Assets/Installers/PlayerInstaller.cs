using UnityEngine;
using Zenject;

public class PlayerInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        // 1. Register PlayerInput as a singleton instance.
        Container.Bind<PlayerInput>().AsSingle();

        // 2. Register InputManager from hierarchy as a singleton instance.
        Container.Bind<InputManager>().FromComponentInHierarchy().AsSingle();
        
        // 3. New: Register FirstPersonController from scene.
        // Now UIManager and other classes can access it for camera control.
        Container.Bind<FirstPersonController>().FromComponentInHierarchy().AsSingle();

        // 4. New: Register UIManager from scene.
        Container.Bind<UIManager>().FromComponentInHierarchy().AsSingle();
        
        Container.Bind<InventoryManager>().FromComponentInHierarchy().AsSingle();
    }
}
