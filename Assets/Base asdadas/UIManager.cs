using UnityEngine;
using Zenject;

/// <summary>
/// ??????????? ???????? ??? ?????????? ????? ?????????? UI, ?????? ??? ???? ? ??????.
/// </summary>
public class UIManager : MonoBehaviour
{
    [Tooltip("?????? ?????????, ??????? ????? ???????????/???????????.")]
    [SerializeField] private Canvas inventoryCanvas;

    [Tooltip("?????? ???? ?????????????, ??????? ????? ???????????/???????????.")]
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
        // ????????, ??? ? ?????? ??? ???? ???????
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
    /// ќткрывает указанное окно и закрывает все остальные.
    /// </summary>
    public void OpenWindow(Canvas canvasToOpen)
    {
        if (canvasToOpen == null) return;

        // —начала закрываем все окна
        CloseAllWindows();

        // ќткрываем нужное
        canvasToOpen.gameObject.SetActive(true);
        UpdateCursorAndCameraState();
    }

    /// <summary>
    /// ѕереключает состо€ние видимости указанного окна.
    /// ≈сли окно было открыто, оно закроетс€. ≈сли было закрыто, оно откроетс€, а другие закроютс€.
    /// </summary>
    private void ToggleWindow(Canvas canvasToToggle)
    {
        if (canvasToToggle == null) return;

        bool isCurrentlyVisible = canvasToToggle.gameObject.activeSelf;

        // ≈сли окно уже видно, просто закрываем все.
        // »наче, открываем его (а другие закроютс€ внутри OpenWindow).
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
    /// «акрывает все управл€емые окна и обновл€ет состо€ние курсора.
    /// </summary>
    public void CloseAllWindows()
    {
        if (inventoryCanvas != null) inventoryCanvas.gameObject.SetActive(false);
        if (buildingCanvas != null) buildingCanvas.gameObject.SetActive(false);
        UpdateCursorAndCameraState(); // ќбновл€ем состо€ние после закрыти€
    }

    /// <summary>
    /// ќбновл€ет состо€ние курсора и управлени€ камерой в зависимости от того, открыто ли какое-либо окно.
    /// </summary>
    private void UpdateCursorAndCameraState()
    {
        _isAnyWindowOpen = (inventoryCanvas != null && inventoryCanvas.gameObject.activeSelf) || 
                           (buildingCanvas != null && buildingCanvas.gameObject.activeSelf);

        if (_isAnyWindowOpen)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _playerController.cameraCanMove = false; // ????????? ???????? ??????
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (_playerController != null) _playerController.cameraCanMove = true; // –азблокируем вращение камеры
        }
    }
}
