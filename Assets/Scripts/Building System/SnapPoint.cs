using UnityEngine;

[System.Serializable]
public class SnapPoint
{
    [Tooltip("Идентификатор точки, используемый для сопоставления с обратной точкой")] 
    public string id;

    [Tooltip("Локальная позиция точки привязки в координатах префаба")] 
    public Vector3 localPosition;

    [Tooltip("Локальная ориентация (Euler) точки привязки в координатах префаба")] 
    public Vector3 localEulerAngles;

    [Tooltip("Список тегов объектов, которые можно присоединять к этой точке")] 
    public string[] allowedNeighborTags;

    // Удобное представление локального вращения
    public Quaternion LocalRotation => Quaternion.Euler(localEulerAngles);
}
