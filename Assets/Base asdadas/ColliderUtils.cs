using UnityEngine;

/// <summary>
/// —татический служебный класс дл€ общих операций, св€занных с коллайдерами.
/// </summary>
public static class ColliderUtils
{
    /// <summary>
    /// ”ниверсально получает локальные границы (центр и размер) дл€ BoxCollider или MeshCollider.
    /// </summary>
    /// <param name="targetObject">»гровой объект с коллайдером.</param>
    /// <param name="localCenter">¬ыходной параметр дл€ локального центра.</param>
    /// <param name="localSize">¬ыходной параметр дл€ локального размера.</param>
    /// <returns>True, если коллайдер поддерживаемого типа найден и обработан, иначе false.</returns>
    public static bool GetColliderBounds(GameObject targetObject, out Vector3 localCenter, out Vector3 localSize)
    {
        localCenter = Vector3.zero;
        localSize = Vector3.zero;

        if (targetObject == null)
        {
            Debug.LogError("[ColliderUtils] Target object is null.");
            return false;
        }
        
        var collider = targetObject.GetComponent<Collider>();
        if (collider == null)
        {
            Debug.LogError("[ColliderUtils] Target object must have a Collider.", targetObject);
            return false;
        }

        if (collider is BoxCollider boxCollider)
        {
            localCenter = boxCollider.center;
            localSize = boxCollider.size;
            return true;
        }
        
        if (collider is MeshCollider meshCollider)
        {
            if (meshCollider.sharedMesh == null)
            {
                Debug.LogError("[ColliderUtils] MeshCollider has no mesh assigned.", targetObject);
                return false;
            }
            localCenter = meshCollider.sharedMesh.bounds.center;
            localSize = meshCollider.sharedMesh.bounds.size;
            return true;
        }

        Debug.LogError($"[ColliderUtils] Unsupported collider type: {collider.GetType().Name}.", targetObject);
        return false;
    }
}

