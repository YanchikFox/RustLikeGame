using System.Collections.Generic;
using UnityEngine;
using Zenject;

public class Placer : MonoBehaviour
{
    private readonly float maxDistance = 7f;
    private readonly float minDistance = 3f;
    private readonly float scrollSensitivity = 0.3f;
    private float distanceFromPlayer = 5f;
    public ConstructionSelector constructionSelector;
    private Construction currentConstruction;
    private GameObject player;
    private HashSet<int> usedIds = new HashSet<int>();
    private PlayerInput _playerInput;
    private InventoryManager _inventoryManager;

    // --- Cache: Variables for optimization purposes ---
    private float _nextPlacementCheckTime;
    private const float PlacementCheckInterval = 0.1f; // Check 10 times per second
    private bool _lastPlacementCheckResult;
    private bool _lastResourceCheckResult; 
    private bool _isFirstPlacementFrame; // Cache: Flag for first frame

    [Inject]
    public void Construct(PlayerInput playerInput, InventoryManager inventoryManager)
    {
        _playerInput = playerInput;
        _inventoryManager = inventoryManager;
    }

    public void StartPlacement()
    {
        Debug.Log("[Placer] StartPlacement called.");
        var factory = constructionSelector.selectedObject;
        currentConstruction = factory.CreateConstruction(factory.IdC);
        currentConstruction.Add(factory.objectInScene, gameObject, factory);
        
        player = gameObject;
        currentConstruction.OnInstantiate();

        // Enable ghost mode
        var ghostController = currentConstruction.objectInScene.GetComponent<GhostModeController>();
        if (ghostController != null)
        {
            ghostController.EnableGhostMode();
        }
        // Initialize cache values for placement checks
        _nextPlacementCheckTime = Time.time;
        _lastPlacementCheckResult = false;
        _lastResourceCheckResult = false;
        _isFirstPlacementFrame = true; // ?????: ????????????? ????
    }

    public void StopPlacement()
    {
        Debug.Log("[Placer] StopPlacement called.");
        var ghostController = currentConstruction.objectInScene.GetComponent<GhostModeController>();
        if (ghostController != null)
        {
            ghostController.DisableGhostMode();
        }

        // ---------------------------------------------------------------------------------
        // ?????: ????????? ?????????, ??-???????????????? ???????? ????? ??????????
        bool canPlace = currentConstruction.Place(true); // ??????????: ?????????????? ????????
        // ---------------------------------------------------------------------------------
        Debug.Log($"[Placer] Final placement validity result: {canPlace}");

        if (canPlace && currentConstruction.objectInScene != null)
        {
            // ????????? ???????? ???????? ????? ?????????
            var factory = currentConstruction.sourceFactory;
            if (_inventoryManager.ConsumeItems(factory.placementCost))
            {
                // ??????? ???? ? ???????. ????????? ??????????.
                int newId = GenerateUniqueId();
                currentConstruction.Id = newId;
                currentConstruction.IdC = constructionSelector.selectedIdC;
                currentConstruction.AfterPlace();
            }
            else
            {
                // ???????? ?? ???????. ???????? ??????????.
                Debug.Log("[Placer] Not enough resources to place the object. Placement cancelled.");
                currentConstruction.Remove();
            }
        }
        else
        {
            // ????? ?? ????????. ???????? ?????????? ??? ???????? ????????.
            currentConstruction.Remove();
        }
    }

    public void PerformPlacement()
    {
        // ????????? ????????? ? ??????? ?????? ???? ?? PlayerInput
        float scrollDelta = _playerInput.Scroll;
        distanceFromPlayer = Mathf.Clamp(distanceFromPlayer - scrollDelta * scrollSensitivity, minDistance, maxDistance);

        // ??????? ??? ?? ?????? ??????
        Ray ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0));
        
        Physics.Raycast(ray, out RaycastHit hit, maxDistance, ~LayerMask.GetMask("Ignore Raycast"));
        
        // --- ������������ ������� ---
        // 1. ������� ������������� ������
        
        // �������� ����������� � ������� �������
        currentConstruction.wasConnected = currentConstruction.Connect(hit);

        // ���� �������� �� ����, ������ ������ �� ����
        if (!currentConstruction.wasConnected)
        {
            currentConstruction.objectInScene.transform.position = ray.GetPoint(distanceFromPlayer);
            
            // � ����������� ��� ����� � ������
            if (player != null)
            {
                Vector3 playerPosition = player.transform.position;
                Vector3 currentPos = currentConstruction.objectInScene.transform.position;
                Vector3 lookDirection = new Vector3(playerPosition.x - currentPos.x, 0f, playerPosition.z - currentPos.z);
            
                if (lookDirection.sqrMagnitude > 0.001f)
                {
                    currentConstruction.objectInScene.transform.rotation = Quaternion.LookRotation(lookDirection);
                }
            }
        }

        // 2. ������, ����� ������ �� �����, ��������� ��������
        bool isPlacementValid;
        
        if (_isFirstPlacementFrame)
        {
            // � ������ ����� ��������� ������, �������������� ��������
            _lastPlacementCheckResult = currentConstruction.Place(true);
            _lastResourceCheckResult = _inventoryManager.HasItems(currentConstruction.sourceFactory.placementCost);
            isPlacementValid = _lastPlacementCheckResult && _lastResourceCheckResult;

            _nextPlacementCheckTime = Time.time + PlacementCheckInterval;
            _isFirstPlacementFrame = false;
        }
        else
        {
            // � ����������� ������ ���������� "����������������" ��������
            if (Time.time >= _nextPlacementCheckTime)
            {
                _nextPlacementCheckTime = Time.time + PlacementCheckInterval;
                _lastPlacementCheckResult = currentConstruction.Place();
                _lastResourceCheckResult = _inventoryManager.HasItems(currentConstruction.sourceFactory.placementCost);
            }
            isPlacementValid = _lastPlacementCheckResult && _lastResourceCheckResult;
        }

        // 3. ������������� ����
        var ghostController = currentConstruction.objectInScene.GetComponent<GhostModeController>();
        if (ghostController != null)
        {
            ghostController.SetPlacementValid(isPlacementValid);
        }
    }

    private int GenerateUniqueId()
    {
        int newId;
        do
        {
            newId = Random.Range(1000, 10000);
        } while (usedIds.Contains(newId));

        usedIds.Add(newId);
        return newId;
    }
}
