using System.Collections.Generic;
using System.Linq;
using UnityEngine;
[CreateAssetMenu(fileName = "New Construction", menuName = "Construction")]
public class ConstructionFactory : ScriptableObject 
{
    public int IdC;
    public int Id;
    public GameObject objectInScene;
    public List<ResourceCost> placementCost;
    public List<TierAppearance> appearances;
    
    public Construction CreateConstruction(int IdC) // ????? ???????? ?????????
    {
        switch (IdC)
        {
            case 1:
                return new Base(IdC, Id);
            case 2:
                return new Wall(IdC, Id);
            case 3:
                return new Window(IdC, Id);
            case 4:
                return new HalfWall(IdC, Id);
            case 5:
                return new DoorFrame(IdC, Id);
            case 6:
                return new Door(IdC, Id);
            case 7:
                return new Roof(IdC, Id);
            case 8:
                return new Ceiling(IdC, Id);
            default:
                break;
        }
        return null;
    }
}

public enum ConstructionType // ??? ?????????
{
    Default,
    Base,
    Wall,
    Window,
    LowerWall,
    Door,
}
public enum ConstructionAttributes // ?????? ???????? (? ??????? ????????)
{
    Default,
    Stable,
    Whos,
    

}


public abstract class Construction // ?????? ?????????
{
    public int Id;
    public int IdC;
    public GameObject objectInScene;
    public bool wasConnected = false; // ???????? ????, ????? ???????? ????????? ???????????
    public float stability = 0f; // 0 to 1, where 1 is 100%
    public ConstructionFactory sourceFactory;

    public Construction(int in_IdC, int in_Id)
    {
        Id = in_Id;
        IdC = in_IdC;
    }   
    
    public GameObject player;
    
    public void Add(GameObject gameObject, GameObject caller, ConstructionFactory factory)
    {
        objectInScene = Object.Instantiate(gameObject, Vector3.zero, Quaternion.identity);
        //objectInScene.GetComponent<IntersectionChecker>().GetBounds();
        player = caller;
        sourceFactory = factory;

        // Add and initialize ConstructionHealth
        var health = objectInScene.AddComponent<ConstructionHealth>();
        if (factory.appearances != null && factory.appearances.Count > 0)
        {
            health.Initialize(this, factory.appearances[0].tier);
        }
        else
        {
            Debug.LogError($"[Construction] Factory '{factory.name}' has no appearances defined!");
        }
    }
    public void Remove()
    {
        Object.Destroy(objectInScene);
    }

    /// <summary>
    /// ???????? ????????? ???? ?????? ? ??????, ????????? UniversalConnector.
    /// </summary>
    /// <returns>True, ???? ?????????? ???????, ????? false.</returns>
    public bool Connect(RaycastHit hit)
    {
        var connector = objectInScene.GetComponent<UniversalConnector>();
        if (connector != null)
        {
            return connector.Connect(hit);
        }
        else
        {
            Debug.LogWarning($"[Construction] No UniversalConnector found on '{objectInScene.name}'. Snapping will not work.");
        }
        return false;
    }

    abstract public void OnInstantiate();
    public abstract bool Place(bool forceRecheck = false);

    public virtual void AfterPlace()
    {
        var health = objectInScene.GetComponent<ConstructionHealth>();
        if (health != null)
        {
            // Neighbors are now registered by UniversalConnector.
            // We just need to trigger the stability update.
            health.RecalculateStability();
        }
    }
}

public class Base : Construction
{
    private Construction constructionImplementation;
    private GroundIntersectionChecker groundChecker;

    public Base(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
        objectInScene.GetComponent<AreaDetection>().GetObjectBounds();
        groundChecker = objectInScene.AddComponent<GroundIntersectionChecker>();
        groundChecker.targetObject = objectInScene;
    }

    public override bool Place(bool forceRecheck = false)
    {
        var collider = objectInScene.GetComponent<Collider>();
        if (collider == null)
        {
            Debug.LogError("[Placer] Placed object requires a Collider for placement checks.", objectInScene);
            return false;
        }

        // ?????????? ????? ????????????? ????? ??? ????????? ?????? ??????????
        if (!ColliderUtils.GetColliderBounds(objectInScene, out Vector3 localCenter, out Vector3 localSize))
        {
            return false; // ?????? ??? ????? ???????? ? ColliderUtils
        }

        // ???????????? ????????? ??? OBB (Oriented Bounding Box) ?? ?????? ?????????? ????????? ??????
        Vector3 center = objectInScene.transform.TransformPoint(localCenter);
        Vector3 halfExtents = Vector3.Scale(localSize, objectInScene.transform.lossyScale) * 0.5f;
        Quaternion orientation = objectInScene.transform.rotation;

        // ??????? ?????, ??????? ?????????? ???? 'Ground' ? 'Ignore Raycast'
        int layerMask = ~LayerMask.GetMask("Ignore Raycast", "Ground");

        // ?????????? OverlapBox ??? ????????? ????????? ?????????? ? ????????????
        Collider[] overlaps = Physics.OverlapBox(center, halfExtents, orientation, layerMask, QueryTriggerInteraction.Ignore);

        if (overlaps.Length > 0)
        {
            // ?????: ??? ????????? ??????????? ??????? ??? ???????,
            // ?? ??? ??????? ????????? ??? ??????? ? ????????? ??????, ????? ?? ???????? ???????.
            #if UNITY_EDITOR
            Debug.LogWarning($"[Placer] Placement failed: Overlap detected with {overlaps.Length} objects.");
            Debug.Log($"[Placer] OverlapBox params: Center={center}, HalfExtents={halfExtents}, Orientation={orientation.eulerAngles}");
            foreach (var overlapCollider in overlaps)
            {
                // ?????????? ??????????? ?????? ????, ???? ? ??????? ??? ???? ?????????
                if (overlapCollider == collider) continue;
                
                Debug.LogWarning($"--> Overlapped with: Name='{overlapCollider.gameObject.name}', Tag='{overlapCollider.tag}', Layer='{LayerMask.LayerToName(overlapCollider.gameObject.layer)}'");
            }
            #endif
            
            // ????????, ??? ?? ?? ??????? ??????????? ? ????? ????? (???? ?????????? ?? ?????????)
            if (overlaps.Any(c => c != collider))
            {
                 return false; //  ,  
            }
        }

        if (!groundChecker.IsGroundedEnough(forceRecheck))
        {
            return false;
        }

        if (!groundChecker.IsSurfaceAngleValid())
        {
            return false;
        }

        return true;
    }

    public override void AfterPlace()
    {
        var areaDetection = objectInScene.GetComponent<AreaDetection>();
        if (areaDetection != null)
        {
            areaDetection.CreateFoundationPlaces();
            areaDetection.FindObjectsInArea();
            areaDetection.SortObjectsByY();
            areaDetection.UpdateFloors();
        }

        // Stability logic
        var health = objectInScene.GetComponent<ConstructionHealth>();
        if (health != null)
        {
            // ???? ????? ??? ????????? ???????????? 100% ? ???????? ???????? ??? ???????.
            health.SetAsGroundedFoundation();
        }
    }

    private void OnDrawGizmos()
    {
        if (objectInScene == null) return;

        var meshFilter = objectInScene.GetComponent<MeshFilter>();
        if (meshFilter == null || meshFilter.sharedMesh == null) return;

        var mesh = meshFilter.sharedMesh;
        var vertices = mesh.vertices;
        var triangles = mesh.triangles;

        Gizmos.color = Color.green;
        foreach (var triangle in GetTriangles(vertices, triangles, objectInScene.transform))
        {
            Gizmos.DrawLine(triangle.v0, triangle.v1);
            Gizmos.DrawLine(triangle.v1, triangle.v2);
            Gizmos.DrawLine(triangle.v2, triangle.v0);
        }

        Collider objectCollider = objectInScene.GetComponent<Collider>();
        if (objectCollider != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(objectCollider.bounds.center, objectCollider.bounds.size);
        }
    }

    private IEnumerable<Triangle> GetTriangles(Vector3[] vertices, int[] triangles, Transform transform)
    {
        for (int i = 0; i < triangles.Length; i += 3)
        {
            yield return new Triangle
            {
                v0 = transform.TransformPoint(vertices[triangles[i]]),
                v1 = transform.TransformPoint(vertices[triangles[i + 1]]),
                v2 = transform.TransformPoint(vertices[triangles[i + 2]])
            };
        }
    }

    private struct Triangle
    {
        public Vector3 v0;
        public Vector3 v1;
        public Vector3 v2;
    }
}
public class Wall : Construction
{
    public Wall(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
        
    }

    public override bool Place(bool forceRecheck = false)
    {
        //    ,     Placer
        return wasConnected;
    }
    
    // AfterPlace() is now handled by the base class
}
public class Window : Construction
{
    public Window(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
    }

    public override bool Place(bool forceRecheck = false)
    {
        return wasConnected;
    }
    
    // AfterPlace() is now handled by the base class
}
public class HalfWall : Construction //TODO ?????????? ???????????.
{
    public HalfWall(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
    }

    public override bool Place(bool forceRecheck = false)
    {
        return wasConnected;
    }
    
    // AfterPlace() is now handled by the base class
}
public class DoorFrame : Construction //TODO ??????? ?? ?????.
{
    public DoorFrame(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
    }

    public override bool Place(bool forceRecheck = false)
    {
        return wasConnected;
    }
    
    // AfterPlace() is now handled by the base class
}
public class Door : Construction //TODO ??????? ?? ?????.
{
    public Door(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
    }

    public override bool Place(bool forceRecheck = false)
    {
        return wasConnected;
    }
    
    // AfterPlace() is now handled by the base class
}
public class Roof : Construction //TODO ??????? ?? ?????.
{
    public Roof(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
    }

    public override bool Place(bool forceRecheck = false)
    {
        return wasConnected;
    }
    
    // AfterPlace() is now handled by the base class
}
public class Ceiling : Construction //TODO ??????? ????????? ? ????.
{
    public Ceiling(int in_IdC, int in_Id) : base(in_IdC, in_Id)
    {

    }

    public override void OnInstantiate()
    {
    }

    public override bool Place(bool forceRecheck = false)
    {
        return wasConnected;
    }
    
    // AfterPlace() is now handled by the base class
}
