using UnityEngine;
using TerrainSystem;

public class PlayerInteraction : MonoBehaviour
{
    [Header("Terrain Interaction")]
    [SerializeField] private float interactionDistance = 5f;
    [SerializeField] private float modificationRadius = 1.5f;
    [SerializeField] private float modificationStrength = -1.0f; // Negative to dig, positive to add

    private Camera mainCamera;
    private TerrainManager terrainManager;

    private void Start()
    {
        mainCamera = Camera.main;
        if (mainCamera == null)
        {
            Debug.LogError("PlayerInteraction: Main camera not found!", this);
            enabled = false;
            return;
        }

        terrainManager = FindObjectOfType<TerrainManager>();
        if (terrainManager == null)
        {
            Debug.LogError("PlayerInteraction: TerrainManager not found in the scene!", this);
            enabled = false;
            return;
        }
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(0)) // Left-click to dig
        {
            HandleInteraction(modificationStrength);
        }
        
        if (Input.GetMouseButtonDown(1)) // Right-click to add
        {
            HandleInteraction(-modificationStrength); // Invert strength to add terrain
        }
    }

    private void HandleInteraction(float strength)
    {
        Ray ray = mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        if (Physics.Raycast(ray, out RaycastHit hit, interactionDistance))
        {
            // Use the new centralized API on TerrainManager
            terrainManager.RequestTerrainModification(
                hit.point,
                modificationRadius,
                strength
            );
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (mainCamera == null) return;
        
        Ray ray = mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        
        Gizmos.color = Color.red;
        Gizmos.DrawRay(ray.origin, ray.direction * interactionDistance);
        
        if (Physics.Raycast(ray, out RaycastHit hit, interactionDistance))
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(hit.point, modificationRadius);
        }
    }
}