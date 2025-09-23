using UnityEngine;
using Zenject;

/// <summary>
/// Universal manager for all user interface management, such as menus and windows.
/// </summary>
public class UIManager : MonoBehaviour
{
    [Tooltip("Inventory canvas that will be shown/hidden.")]
    [SerializeField] private Canvas inventoryCanvas;

    [Tooltip("Building menu canvas that will be shown/hidden.")]
    [SerializeField] private Canvas buildingCanvas;

    private InputManager _inputManager;
    private FirstPersonController _playerController;

    private bool _isAnyWindowOpen = false;

    [Inject]
    public void Construct(InputManager inputManager, FirstPersonController playerController)
    {
        _inputManager = inputManager;
        _playerController = playerController;
    }

    private void Start()
    {
        // Ensure all windows are closed at start
        if (inventoryCanvas != null) inventoryCanvas.gameObject.SetActive(false);
        if (buildingCanvas != null) buildingCanvas.gameObject.SetActive(false);
        UpdateCursorAndCameraState();
    }

    private void Update()
    {
        if (Input.GetKeyDown(_inputManager.toggleInventoryUIKey))
        {
            ToggleWindow(inventoryCanvas);
        }

        if (Input.GetKeyDown(_inputManager.toggleBuildingUIKey))
        {
            ToggleWindow(buildingCanvas);
        }
    }

    /// <summary>
    /// Open a specific window and close all others.
    /// </summary>
    public void OpenWindow(Canvas canvasToOpen)
    {
        if (canvasToOpen == null) return;

        // First close all other windows
        CloseAllWindows();

        // Open the target window
        canvasToOpen.gameObject.SetActive(true);
        UpdateCursorAndCameraState();
    }

    /// <summary>
    /// Toggle the visibility of a specific window.
    /// If the window is open, it closes. If the window is closed, it opens (and closes other windows).
    /// </summary>
    private void ToggleWindow(Canvas canvasToToggle)
    {
        if (canvasToToggle == null) return;

        bool isCurrentlyVisible = canvasToToggle.gameObject.activeSelf;

        // If the window is already open, just close it.
        // Otherwise, open it (and close others through OpenWindow).
        if (isCurrentlyVisible)
        {
            CloseAllWindows();
        }
        else
        {
            OpenWindow(canvasToToggle);
        }
    }

    /// <summary>
    /// Close all open windows and update cursor state.
    /// </summary>
    public void CloseAllWindows()
    {
        if (inventoryCanvas != null) inventoryCanvas.gameObject.SetActive(false);
        if (buildingCanvas != null) buildingCanvas.gameObject.SetActive(false);
        UpdateCursorAndCameraState(); // Update cursor state after closing
    }

    /// <summary>
    /// Update cursor and camera movement state depending on whether any window is open.
    /// </summary>
    private void UpdateCursorAndCameraState()
    {
        _isAnyWindowOpen = (inventoryCanvas != null && inventoryCanvas.gameObject.activeSelf) || 
                           (buildingCanvas != null && buildingCanvas.gameObject.activeSelf);

        if (_isAnyWindowOpen)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _playerController.cameraCanMove = false; // Disable camera movement
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (_playerController != null) _playerController.cameraCanMove = true; // Enable camera movement
        }
    }
}
