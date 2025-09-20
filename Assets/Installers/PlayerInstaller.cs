using UnityEngine;
using Zenject;

public class PlayerInstaller : MonoInstaller
{
    public override void InstallBindings()
    {
        // 1. ����������� PlayerInput ��� ��������� ���������.
        Container.Bind<PlayerInput>().AsSingle();

        // 2. ����������� InputManager �� �������� ��� ��������� ���������.
        Container.Bind<InputManager>().FromComponentInHierarchy().AsSingle();
        
        // 3. �����: ����������� FirstPersonController �� �����.
        // ������ UIManager � ������ ������ ������ ��� ��������.
        Container.Bind<FirstPersonController>().FromComponentInHierarchy().AsSingle();

        // 4. �����: ����������� UIManager �� �����.
        Container.Bind<UIManager>().FromComponentInHierarchy().AsSingle();
        
        Container.Bind<InventoryManager>().FromComponentInHierarchy().AsSingle();
    }
}
