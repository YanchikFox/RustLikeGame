using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// ??????????? ?????????? ?????????? "????????" ??? ??????? ? ?????? ?????????????.
/// </summary>
public class GhostModeController : MonoBehaviour
{
    [Header("Ghost Materials")]
    [Tooltip("???????? ??? ???????????, ????? ????????? ???????? (?????, ??????????????).")]
    public Material ghostMaterialValid;

    [Tooltip("???????? ??? ???????????, ????? ????????? ?????????? (???????, ??????????????).")]
    public Material ghostMaterialInvalid;

    private Renderer[] _renderers;
    private List<Material[]> _originalMaterials = new List<Material[]>();
    private bool _isGhostModeActive = false;
    private int _originalLayer; // НОВОЕ: Поле для хранения оригинального слоя

    /// <summary>
    /// Включает режим "призрака", сохраняя оригинальные материалы и делая объект неинтерактивным.
    /// </summary>
    public void EnableGhostMode()
    {
        if (_isGhostModeActive) return;

        if (ghostMaterialValid == null || ghostMaterialInvalid == null)
        {
            Debug.LogError("[GhostModeController] Ghost materials are not assigned in the inspector!", this);
            return;
        }

        _renderers = GetComponentsInChildren<Renderer>();
        _originalMaterials.Clear();

        foreach (var r in _renderers)
        {
            _originalMaterials.Add(r.materials);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        _originalLayer = gameObject.layer; // Сохраняем оригинальный слой
        SetObjectPhysics(false, LayerMask.NameToLayer("Ignore Raycast")); // Переключаем на Ignore Raycast
        _isGhostModeActive = true;
        SetPlacementValid(false);
    }

    /// <summary>
    /// Выключает режим "призрака", восстанавливая оригинальные материалы и физические свойства.
    /// </summary>
    public void DisableGhostMode()
    {
        if (!_isGhostModeActive) return;

        for (int i = 0; i < _renderers.Length; i++)
        {
            if (_renderers[i] != null)
            {
                _renderers[i].materials = _originalMaterials[i];
                _renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }

        SetObjectPhysics(true, _originalLayer); // Восстанавливаем сохраненный слой
        _isGhostModeActive = false;
    }

    /// <summary>
    /// ????????????? ?????????? ????????? ? ??????????? ?? ????, ??????? ?? ??????? ??????? ??? ?????????.
    /// </summary>
    public void SetPlacementValid(bool isValid)
    {
        if (!_isGhostModeActive) return;

        Material materialToApply = isValid ? ghostMaterialValid : ghostMaterialInvalid;
        foreach (var r in _renderers)
        {
            var materials = new Material[r.materials.Length];
            for (int i = 0; i < materials.Length; i++)
            {
                materials[i] = materialToApply;
            }
            r.materials = materials;
        }
    }

    /// <summary>
    /// Управляет коллайдерами и слоями объекта.
    /// </summary>
    private void SetObjectPhysics(bool isTangible, int layer)
    {
        var colliders = GetComponentsInChildren<Collider>();
        foreach (var col in colliders)
        {
            col.enabled = isTangible;
        }

        SetLayerRecursively(gameObject, layer);
    }

    private void SetLayerRecursively(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
        {
            SetLayerRecursively(child.gameObject, layer);
        }
    }
}
