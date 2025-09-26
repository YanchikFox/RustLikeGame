using UnityEngine;
using Zenject;

public class InputManager : MonoBehaviour
{
    // PlayerInput instance is injected via Zenject
    [Inject]
    public PlayerInput PlayerInput { get; private set; }

    // Key bindings can be configured here or in a separate settings class
    public KeyCode sprintKey = KeyCode.LeftShift;
    public KeyCode jumpKey = KeyCode.Space;
    public KeyCode crouchKey = KeyCode.LeftControl;
    public KeyCode zoomKey = KeyCode.Mouse1;
    public KeyCode placeObjectKey = KeyCode.Mouse0;
    public KeyCode toggleBuildingUIKey = KeyCode.B;
    public KeyCode toggleInventoryUIKey = KeyCode.E;

    // Awake() method is not needed since PlayerInput is injected automatically

    void Update()
    {
        // Check that PlayerInput is already initialized, since it's injected
        if (PlayerInput == null)
        {
            return;
        }

        // Movement
        PlayerInput.Move = new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
        
        // Look
        PlayerInput.Look = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));

        // Actions
        PlayerInput.Sprint = Input.GetKey(sprintKey);
        PlayerInput.Jump = Input.GetKeyDown(jumpKey);
        PlayerInput.Crouch = Input.GetKeyDown(crouchKey);
        PlayerInput.Zoom = Input.GetKey(zoomKey);
        PlayerInput.PlaceObject = Input.GetKeyDown(placeObjectKey);
        
        // UI Toggles (using GetKeyDown to register a single press)
        if (Input.GetKeyDown(toggleBuildingUIKey))
        {
            // This could toggle a bool in PlayerInput or fire a specific event
        }
        if (Input.GetKeyDown(toggleInventoryUIKey))
        {
            // This could toggle a bool in PlayerInput or fire a specific event
        }

        // Scroll
        PlayerInput.Scroll = Input.mouseScrollDelta.y;

        // Hotbar digits
        PlayerInput.HotbarDigit = -1; // Reset every frame
        for (int i = 0; i < 9; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                PlayerInput.HotbarDigit = i;
                break; 
            }
        }
    }
}
