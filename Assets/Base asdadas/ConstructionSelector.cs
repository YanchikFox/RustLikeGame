using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Zenject;

public class ConstructionSelector : MonoBehaviour //TODO rename ConstructionFactorySelector
{
    private Dictionary<int, ConstructionFactory> _factories;
    public int selectedIdC = -1;
    public ConstructionFactory selectedObject = null;
    private UIManager _uiManager;

    [Inject]
    public void Construct(List<ConstructionFactory> factories, UIManager uiManager)
    {
        _factories = factories.ToDictionary(f => f.IdC, f => f);
        _uiManager = uiManager;
    }

    public void SelectObjectWithIdC(int idC)
    {
        if (_factories.TryGetValue(idC, out var factory))
        {
            selectedObject = factory;
            selectedIdC = idC;
        }
        else
        {
            selectedIdC = -1;
            selectedObject = null;
            Debug.LogWarning($"Object with IdC {idC} not found.");
        }

        // После выбора объекта, закрываем все окна UI
        if (_uiManager != null)
        {
            _uiManager.CloseAllWindows();
        }
    }
}
