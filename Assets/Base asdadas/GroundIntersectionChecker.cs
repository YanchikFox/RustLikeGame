using UnityEngine;
using System.Collections.Generic;

public class GroundIntersectionChecker : MonoBehaviour
{
    public GameObject targetObject;

    [Tooltip("The percentage of the base area that must be under the ground surface.")]
    [Range(0f, 1f)]
    public float requiredPercentage = 0.95f;

    [Tooltip("How many points to check along each axis of the base. 10x10=100 total points.")]
    [Range(2, 30)]
    public int density = 10;

    [Tooltip("A small vertical offset to prevent floating point issues at the surface.")]
    public float surfaceOffset = 0.01f;

    [Header("Performance")]
    [Tooltip("How often (in seconds) to perform the expensive check. 0.1 means 10 times per second.")]
    public float checkInterval = 0.1f;

    private float _nextCheckTime;
    private bool _lastCheckResult;

    public bool IsGroundedEnough(bool forceRecheck = false)
    {
        if (!forceRecheck && Time.time < _nextCheckTime)
        {
            // Убрал лог для чистоты консоли, но логика кэширования осталась
            return _lastCheckResult;
        }

        _nextCheckTime = Time.time + checkInterval;
        _lastCheckResult = PerformCheck();
        
        // Этот лог тоже можно будет убрать после отладки
        Debug.Log($"[GroundIntersectionChecker] Grounded check result: {_lastCheckResult} (Forced: {forceRecheck})");
        return _lastCheckResult;
    }

    public bool PerformCheck()
    {
        if (targetObject == null)
        {
            Debug.LogError("[GroundIntersectionChecker] Target object is null.");
            return false;
        }

        // ?????????? ????? ????????????? ????? ??? ????????? ?????? ??????????
        if (!ColliderUtils.GetColliderBounds(targetObject, out Vector3 localCenter, out Vector3 localSize))
        {
            return false; // ?????? ??? ????? ???????? ? GetColliderBounds
        }

        Transform objTransform = targetObject.transform;
        int groundLayerMask = LayerMask.GetMask("Ground");

        int submergedPointsCount = 0;
        
        float halfX = localSize.x / 2;
        float halfZ = localSize.z / 2;
        float bottomY = localCenter.y - localSize.y / 2;

        // ??????? 5 ????? ??? ????????: ????? ? 4 ????
        Vector3[] checkPoints = {
            new Vector3(localCenter.x, bottomY, localCenter.z), // ?????
            new Vector3(localCenter.x + halfX, bottomY, localCenter.z + halfZ), // ???? 1
            new Vector3(localCenter.x + halfX, bottomY, localCenter.z - halfZ), // ???? 2
            new Vector3(localCenter.x - halfX, bottomY, localCenter.z + halfZ), // ???? 3
            new Vector3(localCenter.x - halfX, bottomY, localCenter.z - halfZ)  // ???? 4
        };

        foreach (var localPoint in checkPoints)
        {
            Vector3 worldPoint = objTransform.TransformPoint(localPoint);
            Vector3 rayOrigin = worldPoint + Vector3.up * 10f; // ???????? ??? ????

            // ??????? ??? ????, ????? ????? ??????????? ?????
            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 20f, groundLayerMask))
            {
                // ???? ????? ??????????????? ? ?????? ????, ??? ??? ????? ?????, ??????, ??? ??? ??????
                if (hit.point.y > worldPoint.y - surfaceOffset)
                {
                    submergedPointsCount++;
                }
            }
        }

        // ??? 5 ?????, ????????? ??????? ????????, ??? ??? ??????? N ?? ??? ?????? ???? ??? ??????.
        // ????????, 80% (requiredPercentage = 0.8) ????????, ??? 4 ?? 5 ????? ?????? ???? ?????????.
        float insidePercentage = (float)submergedPointsCount / checkPoints.Length;

        Debug.Log($"[GroundIntersectionChecker] Submerged points: {submergedPointsCount}/{checkPoints.Length}, Inside percentage: {insidePercentage:P0}");

        return insidePercentage >= requiredPercentage;
    }

    public bool IsSurfaceAngleValid()
    {
        var collider = targetObject.GetComponent<Collider>();
        if (collider == null)
        {
            Debug.LogError("[GroundIntersectionChecker] Target object must have a Collider.", targetObject);
            return false;
        }

        // ?????????? ????? ????????????? ?????
        if (!ColliderUtils.GetColliderBounds(targetObject, out Vector3 localCenter, out Vector3 localSize))
        {
            return false;
        }

        Transform objTransform = targetObject.transform;
        int groundLayerMask = LayerMask.GetMask("Ground");
        
        // ????????? ????????? ????? ??? ????????? ??????? ???????????
        List<Vector3> normals = new List<Vector3>();
        
        float halfX = localSize.x / 2;
        float halfZ = localSize.z / 2;
        float bottomY = localCenter.y - localSize.y / 2;

        // ????????? 5 ?????: ????? ? 4 ????
        Vector3[] checkPoints = {
            new Vector3(localCenter.x, bottomY, localCenter.z), // ?????
            new Vector3(localCenter.x + halfX * 0.8f, bottomY, localCenter.z + halfZ * 0.8f), // ????
            new Vector3(localCenter.x - halfX * 0.8f, bottomY, localCenter.z + halfZ * 0.8f),
            new Vector3(localCenter.x + halfX * 0.8f, bottomY, localCenter.z - halfZ * 0.8f),
            new Vector3(localCenter.x - halfX * 0.8f, bottomY, localCenter.z - halfZ * 0.8f)
        };

        foreach (Vector3 localPoint in checkPoints)
        {
            Vector3 worldPoint = objTransform.TransformPoint(localPoint);
            Vector3 rayOrigin = worldPoint + Vector3.up * 10f;

            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 20f, groundLayerMask))
            {
                normals.Add(hit.normal);
            }
        }

        if (normals.Count == 0)
        {
            Debug.LogWarning("[GroundIntersectionChecker] No ground detected for surface angle check.");
            return false;
        }

        // ????????? ??????? ???????
        Vector3 averageNormal = Vector3.zero;
        foreach (Vector3 normal in normals)
        {
            averageNormal += normal;
        }
        averageNormal /= normals.Count;
        averageNormal.Normalize();

        float angle = Vector3.Angle(averageNormal, Vector3.up);
        Debug.Log($"[GroundIntersectionChecker] Surface angle: {angle:F1}?, Average normal: {averageNormal}");
        
        return angle <= 35f;
    }
}