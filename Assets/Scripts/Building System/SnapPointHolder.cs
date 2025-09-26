using UnityEngine;

/// <summary>
/// ?????? ?????? ????? ???????? ?? ??????? ??? ??????? snap.
/// </summary>
public class SnapPointHolder : MonoBehaviour
{
    [Tooltip("?????, ? ??????? ????? ???????????? ?????? ???????")] 
    public SnapPoint[] snapPoints;

    void Awake()
    {
        UpdateSnapPoints();
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        UpdateSnapPoints();
    }
#endif

    private void UpdateSnapPoints()
    {
        var col = GetComponent<Collider>();
        if (col == null || snapPoints == null) return;

        // Рассчитываем локальные размеры (полуширины) объекта.
        // Этот метод более надежен, так как работает в локальном пространстве объекта.
        Vector3 extents;
        Vector3 center;

        if (col is BoxCollider bc)
        {
            extents = bc.size * 0.5f;
            center = bc.center;
        }
        else if (col is MeshCollider mc && mc.sharedMesh != null)
        {
            extents = mc.sharedMesh.bounds.extents;
            center = mc.sharedMesh.bounds.center;
        }
        else
        {
            // Запасной вариант, менее точный
            extents = col.bounds.extents;
            center = Vector3.zero; // Не можем точно определить центр для произвольного коллайдера
        }

        // Проходим по всем точкам и выставляем их позиции и повороты согласно их ID
        for (int i = 0; i < snapPoints.Length; i++)
        {
            var p = snapPoints[i];
            if (string.IsNullOrEmpty(p.id)) continue;

            // Новая, расширенная логика расстановки точек
            // Формат ID: [Позиция]_[Грань/Угол]_[Направление]
            // Пример: Edge_Top_X+, Corner_Bottom_X-Z+, Face_Center
            string[] parts = p.id.Split('_');
            if (parts.Length == 0) continue;

            Vector3 position = center;
            Vector3 eulerAngles = Vector3.zero;

            // Определяем вертикальное положение (Top, Mid, Bottom)
            float yPos = center.y;
            if (p.id.Contains("Top")) yPos = center.y + extents.y;
            else if (p.id.Contains("Bottom")) yPos = center.y - extents.y;

            // Определяем горизонтальное положение и ориентацию
            if (p.id.Contains("Edge")) // Точка на грани
            {
                if (p.id.Contains("X+")) { position.x += extents.x; eulerAngles.y = 90; }
                if (p.id.Contains("X-")) { position.x -= extents.x; eulerAngles.y = -90; }
                if (p.id.Contains("Z+")) { position.z += extents.z; eulerAngles.y = 0; }
                if (p.id.Contains("Z-")) { position.z -= extents.z; eulerAngles.y = 180; }
            }
            else if (p.id.Contains("Corner")) // Точка в углу
            {
                if (p.id.Contains("X+")) position.x += extents.x;
                if (p.id.Contains("X-")) position.x -= extents.x;
                if (p.id.Contains("Z+")) position.z += extents.z;
                if (p.id.Contains("Z-")) position.z -= extents.z;
                // Углы пока без авто-поворота, т.к. ориентация неоднозначна
            }
            else if (p.id.Contains("Face")) // Точка на плоскости
            {
                 if (p.id.Contains("Top")) { eulerAngles.x = -90; } // Смотрит вверх
                 else if (p.id.Contains("Bottom")) { eulerAngles.x = 90; } // Смотрит вниз (исправлено обратно)
                 // Можно добавить грани X/Z по аналогии
            }
            else
            {
                // Поддержка старых ID для обратной совместимости
                switch (p.id)
                {
                    case "EdgeX+": position = new Vector3(center.x + extents.x, p.localPosition.y, p.localPosition.z); eulerAngles = new Vector3(0, 90, 0); break;
                    case "EdgeX-": position = new Vector3(center.x - extents.x, p.localPosition.y, p.localPosition.z); eulerAngles = new Vector3(0, -90, 0); break;
                    case "EdgeZ+": position = new Vector3(p.localPosition.x, p.localPosition.y, center.z + extents.z); eulerAngles = new Vector3(0, 0, 0); break;
                    case "EdgeZ-": position = new Vector3(p.localPosition.x, p.localPosition.y, center.z - extents.z); eulerAngles = new Vector3(0, 180, 0); break;
                    default: continue; // Если ID не распознан, пропускаем, чтобы не сбить ручные настройки
                }
            }

            p.localPosition = new Vector3(position.x, yPos, position.z);
            p.localEulerAngles = eulerAngles;
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (snapPoints == null) return;

        foreach (var p in snapPoints)
        {
            // ????????????? ??????? ?????????????, ????? Gizmos ?????????? ? ????????? ???????????? ???????
            Gizmos.matrix = transform.localToWorldMatrix;

            // ???????? ????????? ???????? ?? ????? ??????
            Quaternion localRotation = p.LocalRotation;

            // ?????? ????? ? ????????? ??????? ?????
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(p.localPosition, 0.1f);

            // ?????? ???, ????? ???????? ?????????? ?????
            // ??? Z (??????) - ?????
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(p.localPosition, p.localPosition + localRotation * Vector3.forward * 0.5f);
            // ??? Y (?????) - ???????
            Gizmos.color = Color.green;
            Gizmos.DrawLine(p.localPosition, p.localPosition + localRotation * Vector3.up * 0.5f);
            // ??? X (??????) - ???????
            Gizmos.color = Color.red;
            Gizmos.DrawLine(p.localPosition, p.localPosition + localRotation * Vector3.right * 0.5f);
        }
        // ?????????? ??????? ? ???????????
        Gizmos.matrix = Matrix4x4.identity;
    }
#endif
}
