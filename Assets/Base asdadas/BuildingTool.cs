using UnityEngine;
using Zenject;
using TMPro;
using UnityEngine.UI; // Required for Image

public class BuildingTool : MonoBehaviour
{
    private InventoryManager _inventoryManager;
    private Camera _mainCamera;
    private ConstructionHealth _currentlyHighlighted;
    private float _raycastDistance = 10f;
    private PlayerInput _playerInput;
    private float _nextRepairTime;

    [Header("Highlighting")]
    [Tooltip("The layer mask for all buildable objects.")]
    [SerializeField] private LayerMask buildableLayer;
    [Tooltip("The color of the emission highlight when looking at a buildable.")]
    [SerializeField] private Color highlightColor = new Color(0, 0.5f, 1f, 1f);
    [Tooltip("The intensity of the emission highlight.")]
    [SerializeField] private float highlightIntensity = 1.5f;
    
    private Renderer[] _highlightedRenderers;
    private MaterialPropertyBlock _propBlock;
    private static readonly int EmissionColorID = Shader.PropertyToID("_EmissionColor");

    [Header("Repair")]
    [Tooltip("How much health is restored per repair tick.")]
    public float repairAmountPerTick = 25f;
    [Tooltip("How often (in seconds) a repair tick can happen.")]
    public float repairTickRate = 0.25f;

    [Header("UI Info Panel")]
    [Tooltip("The parent panel for all building info UI elements.")]
    public GameObject infoPanel;
    public TextMeshProUGUI stabilityText;
    public TextMeshProUGUI healthText;
    public Image healthBar;

    [Inject]
    public void Construct(InventoryManager inventoryManager, PlayerInput playerInput)
    {
        _inventoryManager = inventoryManager;
        _playerInput = playerInput;
        _mainCamera = Camera.main;
    }

    void Start()
    {
        _propBlock = new MaterialPropertyBlock();
        // Ensure the panel is hidden at the start
        if (infoPanel != null)
        {
            infoPanel.SetActive(false);
        }
    }

    void Update()
    {
        Item selectedItem = _inventoryManager.GetSelectedItem(false);

        if (selectedItem != null && selectedItem.type == ItemType.Tool && selectedItem.toolType == ToolType.Hammer)
        {
            HandleHammerInteraction(selectedItem);
        }
        else
        {
            // If we are no longer holding a hammer, clear everything.
            if (_currentlyHighlighted != null)
            {
                ClearHighlight();
            }
        }
    }

    private void HandleHammerInteraction(Item currentTool)
    {
        if (_mainCamera == null) _mainCamera = Camera.main;

        Ray ray = _mainCamera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0));
        
        // ќѕ“»ћ»«ј÷»я: Raycast теперь использует LayerMask, чтобы провер€ть только объекты на слое "Buildable"
        if (Physics.Raycast(ray, out RaycastHit hit, _raycastDistance, buildableLayer))
        {
            var health = hit.collider.GetComponentInParent<ConstructionHealth>();

            if (health != null)
            {
                // If we are looking at a new object, update the highlight.
                if (_currentlyHighlighted != health)
                {
                    ClearHighlight(); // Clear previous highlight first
                    _currentlyHighlighted = health;
                    ApplyHighlight(health);
                }
                
                UpdateInfoPanel(health);

                // --- DAMAGE / UPGRADE LOGIC (Left Mouse Button) ---
                if (_playerInput.PlaceObject) // Mapped to Mouse0 Down
                {
                    if (currentTool.damageTypes != null && currentTool.damageTypes.Count > 0)
                    {
                        health.TakeDamage(currentTool.damageTypes);
                    }
                    else if (health.CanUpgrade()) // Check if upgrading is allowed
                    {
                        health.Upgrade(_inventoryManager);
                    }
                }
                
                // --- REPAIR LOGIC (Right Mouse Button) ---
                if (_playerInput.Zoom) // Mapped to Mouse1 Held
                {
                    if (health.CanRepair()) // Check if repairing is allowed
                    {
                        HandleRepair(health);
                    }
                }
            }
            else
            {
                // If we are looking at something else, clear the highlight.
                ClearHighlight();
            }
        }
        else
        {
            // If we are looking at nothing, clear the highlight.
            ClearHighlight();
        }
    }

    private void HandleRepair(ConstructionHealth health)
    {
        if (Time.time < _nextRepairTime) return;
        if (health.currentHealth >= health.maxHealth) return;

        _nextRepairTime = Time.time + repairTickRate;
        
        health.Repair(repairAmountPerTick, _inventoryManager);
    }

    private void ApplyHighlight(ConstructionHealth health)
    {
        if (health == null) return;
        
        _highlightedRenderers = health.GetComponentsInChildren<Renderer>();
        if (_highlightedRenderers == null || _highlightedRenderers.Length == 0) return;

        _propBlock.SetColor(EmissionColorID, highlightColor * highlightIntensity);
        foreach (var r in _highlightedRenderers)
        {
            r.SetPropertyBlock(_propBlock);
        }
    }

    private void RemoveHighlight()
    {
        if (_highlightedRenderers == null) return;

        _propBlock.SetColor(EmissionColorID, Color.black);
        foreach (var r in _highlightedRenderers)
        {
            if (r != null) // Object might have been destroyed
            {
                r.SetPropertyBlock(_propBlock);
            }
        }
        _highlightedRenderers = null;
    }

    private void UpdateInfoPanel(ConstructionHealth health)
    {
        if (infoPanel == null) return;

        if (!infoPanel.activeSelf)
        {
            infoPanel.SetActive(true);
        }

        // Update Stability
        int stabilityPercent = Mathf.RoundToInt(health.owner.stability * 100);
        stabilityText.text = $"Stability: {stabilityPercent}%";

        // Update Health
        healthText.text = $"Health: {Mathf.Ceil(health.currentHealth)} / {health.maxHealth}";
        healthBar.transform.localScale = new Vector3(health.currentHealth / health.maxHealth, 1, 1);
    }

    private void ClearHighlight()
    {
        if (_currentlyHighlighted == null) return;

        RemoveHighlight();

        if (infoPanel != null && infoPanel.activeSelf)
        {
            infoPanel.SetActive(false);
        }

        _currentlyHighlighted = null;
    }
}