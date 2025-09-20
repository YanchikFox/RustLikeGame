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

    // --- ?????: ???? ??? ??????????????? ???????? ---
    private float _nextPlacementCheckTime;
    private const float PlacementCheckInterval = 0.1f; // ????????? 10 ??? ? ???????
    private bool _lastPlacementCheckResult;
    private bool _lastResourceCheckResult; 
    private bool _isFirstPlacementFrame; // ?????: ???? ??? ??????? ?????

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

        // ???????? ????? ????????
        var ghostController = currentConstruction.objectInScene.GetComponent<GhostModeController>();
        if (ghostController != null)
        {
            ghostController.EnableGhostMode();
        }
        // ?????????? ?????? ? ?????????? ???????? ??? ?????? ??????????
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
        
        // --- »—œ–¿¬À≈ÕÕ€… œŒ–ﬂƒŒ  ---
        // 1. —Õ¿◊¿À¿ œŒ«»÷»ŒÕ»–”≈Ã Œ¡⁄≈ “
        
        // œ˚Ú‡ÂÏÒˇ ÔË‚ˇÁ‡Ú¸Òˇ Í ‰Û„ÓÏÛ Ó·˙ÂÍÚÛ
        currentConstruction.wasConnected = currentConstruction.Connect(hit);

        // ≈ÒÎË ÔË‚ˇÁÍË ÌÂ ·˚ÎÓ, ÒÚ‡‚ËÏ Ó·˙ÂÍÚ ÔÓ ÎÛ˜Û
        if (!currentConstruction.wasConnected)
        {
            currentConstruction.objectInScene.transform.position = ray.GetPoint(distanceFromPlayer);
            
            // » ÓËÂÌÚËÛÂÏ Â„Ó ÎËˆÓÏ Í Ë„ÓÍÛ
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

        // 2. “≈œ≈–‹,  Œ√ƒ¿ Œ¡⁄≈ “ Õ¿ Ã≈—“≈, ¬€œŒÀÕﬂ≈Ã œ–Œ¬≈– ”
        bool isPlacementValid;
        
        if (_isFirstPlacementFrame)
        {
            // ¬ ÔÂ‚ÓÏ Í‡‰Â ‚˚ÔÓÎÌˇÂÏ ÔÓÎÌÛ˛, ÔËÌÛ‰ËÚÂÎ¸ÌÛ˛ ÔÓ‚ÂÍÛ
            _lastPlacementCheckResult = currentConstruction.Place(true);
            _lastResourceCheckResult = _inventoryManager.HasItems(currentConstruction.sourceFactory.placementCost);
            isPlacementValid = _lastPlacementCheckResult && _lastResourceCheckResult;

            _nextPlacementCheckTime = Time.time + PlacementCheckInterval;
            _isFirstPlacementFrame = false;
        }
        else
        {
            // ¬ ÔÓÒÎÂ‰Û˛˘Ëı Í‡‰‡ı ËÒÔÓÎ¸ÁÛÂÏ "‰ÓÒÒÂÎËÓ‚‡ÌÌÛ˛" ÔÓ‚ÂÍÛ
            if (Time.time >= _nextPlacementCheckTime)
            {
                _nextPlacementCheckTime = Time.time + PlacementCheckInterval;
                _lastPlacementCheckResult = currentConstruction.Place();
                _lastResourceCheckResult = _inventoryManager.HasItems(currentConstruction.sourceFactory.placementCost);
            }
            isPlacementValid = _lastPlacementCheckResult && _lastResourceCheckResult;
        }

        // 3. ”—“¿Õ¿¬À»¬¿≈Ã ÷¬≈“
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
