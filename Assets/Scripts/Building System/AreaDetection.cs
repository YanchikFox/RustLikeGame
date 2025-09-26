using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class FloorData
{
    public Dictionary<string, bool> WallStates { get; private set; }
    public bool HasCeiling { get; set; }
    public Dictionary<int, (string Orientation, GameObject Object)> Objects { get; private set; }

    public FloorData()
    {
        WallStates = new Dictionary<string, bool>
        {
            { "XPositive", false },
            { "XNegative", false },
            { "ZPositive", false },
            { "ZNegative", false }
        };

        HasCeiling = false;
        Objects = new Dictionary<int, (string, GameObject)>();
    }

    public void Reset()
    {
        foreach (var key in WallStates.Keys.ToList())
            WallStates[key] = false;

        HasCeiling = false;
        Objects.Clear();
    }
}

public class AreaDetection : MonoBehaviour
{
    private const float FloorHeight = 3.5f;
    private const float FoundationOffset = 2.27f;


    private LayerMask _layerToIgnore;
    private Vector3 _boxSize;
    private Vector3 _transformPoint;
    public GameObject[] DetectedObjects { get; private set; }
    public GameObject[] ConnectedBases { get; private set; }

    public List<FloorData> Floors { get; private set; }
    

    private void Start()
    {
        InitializeFloors(5);
        _layerToIgnore = 1 << LayerMask.NameToLayer("LayerToIgnore");
    }

    public void InitializeFloors(int count)
    {
        Floors = Enumerable.Range(0, count).Select(_ => new FloorData()).ToList();
    }

    public void GetObjectBounds()
    {
        var meshCollider = GetComponent<MeshCollider>();
        var objectSize = meshCollider.bounds.size;

        _boxSize = new Vector3(objectSize.x - 0.01f, objectSize.y * 10f, objectSize.z - 0.01f);
    }

    public void FindObjectsInArea()
    {
        _transformPoint = transform.TransformPoint(Vector3.zero);
        _transformPoint.y += 10.45f;

        var extents = new Vector3(_boxSize.x * 0.5f, _boxSize.y * 0.5f + 0.85f, _boxSize.z * 0.5f);
        var colliders = Physics.OverlapBox(_transformPoint, extents, transform.rotation, ~_layerToIgnore);

        DetectedObjects = colliders.Select(collider => collider.gameObject).ToArray();
    }

    public void SortObjectsByY()
    {
        DetectedObjects = DetectedObjects.OrderBy(obj => obj.transform.position.y).ToArray();
    }

    public void UpdateFloors()
    {
        var baseHeight = transform.position.y + 0.87f;
        for (var i = 0; i < Floors.Count; i++)
        {
            var minHeight = baseHeight + i * FloorHeight;
            var maxHeight = minHeight + FloorHeight;

            UpdateFloor(Floors[i], minHeight, maxHeight);
        }
    }

    private void UpdateFloor(FloorData floor, float minHeight, float maxHeight)
    {
        floor.Reset();

        foreach (var obj in DetectedObjects)
        {
            var objHeight = obj.transform.position.y;
            if (objHeight > minHeight && objHeight <= maxHeight)
            {
                var orientation = GetOrientation(obj);
                UpdateFloorData(floor, orientation, obj);
            }
        }
    }

    private void UpdateFloorData(FloorData floor, string orientation, GameObject obj)
    {
        if (floor.WallStates.ContainsKey(orientation))
            floor.WallStates[orientation] = true;

        if (orientation == "Center")
            floor.HasCeiling = true;

        floor.Objects[floor.Objects.Count] = (orientation, obj);
    }

    private string GetOrientation(GameObject obj)
    {
        var localPos = transform.InverseTransformPoint(obj.transform.position);
        if (localPos.x > 0.01f) return "XPositive";
        if (localPos.x < -0.01f) return "XNegative";
        if (localPos.z > 0.01f) return "ZPositive";
        if (localPos.z < -0.01f) return "ZNegative";
        return "Center";
    }

    public void CreateFoundationPlaces()
    {
        
    }

     void OnDrawGizmosSelected()
     {
         Vector3 center = new Vector3(0f, -0.85f, 0f);
         Gizmos.color = Color.yellow;
         Gizmos.matrix = Matrix4x4.TRS(_transformPoint, transform.rotation, Vector3.one);
         Gizmos.DrawWireCube(center, _boxSize);
     }
}
