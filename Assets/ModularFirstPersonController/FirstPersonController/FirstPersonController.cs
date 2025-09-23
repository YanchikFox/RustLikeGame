// CHANGE LOG
// 
// CHANGES || version VERSION
//
// "Enable/Disable Headbob, Changed look rotations - should result in reduced camera jitters" || version 1.0.1
using UnityEngine;
using UnityEngine.UI;
using Zenject;

#if UNITY_EDITOR
    using UnityEditor;
#endif

public class FirstPersonController : MonoBehaviour
{
    private Rigidbody rb;
    private PlayerInput _playerInput;

    [Inject]
    public void Construct(PlayerInput playerInput)
    {
        _playerInput = playerInput;
    }

    #region Camera Movement Variables

    public Camera playerCamera;

    public float fov = 60f;
    public bool invertCamera = false;
    public bool cameraCanMove = true;
    public float mouseSensitivity = 2f;
    public float maxLookAngle = 50f;

    // Crosshair
    public bool lockCursor = true;
    public bool crosshair = true;
    public Sprite crosshairImage;
    public Color crosshairColor = Color.white;
    [Tooltip("The Image component for the crosshair.")]
    public Image crosshairObject; // Made public

    // Internal Variables
    private float yaw = 0.0f;
    private float pitch = 0.0f;
    // private Image crosshairObject; // No longer needed

    #region Camera Zoom Variables

    public bool enableZoom = true;
    public bool holdToZoom = true; // Note: Toggle zoom logic removed for simplicity with the new Input System.
    public float zoomFOV = 30f;
    public float zoomStepTime = 5f;

    // Internal Variables
    private bool isZoomed = false;

    #endregion
    #endregion

    #region Movement Variables

    public bool playerCanMove = true;
    public float walkSpeed = 5f;
    public float maxVelocityChange = 10f;

    // Internal Variables
    public bool isWalking = false;

    #region Sprint

    public bool enableSprint = true;
    public bool unlimitedSprint = false;
    // sprintKey is now managed by InputManager
    public float sprintSpeed = 7f;
    public float sprintDuration = 5f;
    public float sprintCooldown = .5f;
    public float sprintFOV = 80f;
    public float sprintFOVStepTime = 10f;

    // Sprint Bar
    public bool useSprintBar = true;
    public bool hideBarWhenFull = true;
    public Image sprintBarBG;
    public Image sprintBar;
    public float sprintBarWidthPercent = .3f;
    public float sprintBarHeightPercent = .015f;
    [Tooltip("The CanvasGroup for the sprint bar.")]
    public CanvasGroup sprintBarCG; // Made public

    // Internal Variables
    // private CanvasGroup sprintBarCG; // No longer needed
    private bool isSprinting = false;
    private float sprintRemaining;
    private float sprintBarWidth;
    private float sprintBarHeight;
    private bool isSprintCooldown = false;
    private float sprintCooldownReset;

    #endregion

    #region Jump

    public bool enableJump = true;
    // jumpKey is now managed by InputManager
    public float jumpPower = 5f;

    // Internal Variables
    public bool isGrounded = false;

    #endregion

    #region Crouch

    public bool enableCrouch = true;
    public bool holdToCrouch = true;
    // crouchKey is now managed by InputManager
    public float crouchHeight = .75f;
    public float speedReduction = .5f;

    // Internal Variables
    private bool isCrouched = false;
    private Vector3 originalScale;

    #endregion
    #endregion

    #region Head Bob

    public bool enableHeadBob = true;
    public Transform joint;
    public float bobSpeed = 10f;
    public Vector3 bobAmount = new Vector3(.15f, .05f, 0f);

    // Internal Variables
    private Vector3 jointOriginalPos;
    private float timer = 0;

    #endregion

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // crosshairObject = GetComponentInChildren<Image>(); // REMOVED: Unsafe search

        // Set internal variables
        playerCamera.fieldOfView = fov;
        originalScale = transform.localScale;
        jointOriginalPos = joint.localPosition;

        if (!unlimitedSprint)
        {
            sprintRemaining = sprintDuration;
            sprintCooldownReset = sprintCooldown;
        }
    }

    void Start()
    {
        if(lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
        }

        if(crosshair)
        {
            crosshairObject.sprite = crosshairImage;
            crosshairObject.color = crosshairColor;
        }
        else
        {
            crosshairObject.gameObject.SetActive(false);
        }

        #region Sprint Bar

        // sprintBarCG = GetComponentInChildren<CanvasGroup>(); // REMOVED: Unsafe search

        if(useSprintBar)
        {
            if (sprintBarCG == null || sprintBarBG == null || sprintBar == null)
            {
                Debug.LogError("Please assign all sprint bar objects in the inspector.");
            }
            else
            {
                sprintBarBG.gameObject.SetActive(true);
                sprintBar.gameObject.SetActive(true);

                float screenWidth = Screen.width;
                float screenHeight = Screen.height;

                sprintBarWidth = screenWidth * sprintBarWidthPercent;
                sprintBarHeight = screenHeight * sprintBarHeightPercent;

                sprintBarBG.rectTransform.sizeDelta = new Vector3(sprintBarWidth, sprintBarHeight, 0f);
                sprintBar.rectTransform.sizeDelta = new Vector3(sprintBarWidth - 2, sprintBarHeight - 2, 0f);

                if(hideBarWhenFull)
                {
                    sprintBarCG.alpha = 0;
                }
            }
        }
        else
        {
            sprintBarBG.gameObject.SetActive(false);
            sprintBar.gameObject.SetActive(false);
        }

        #endregion
    }

    float camRotation;

    private void Update()
    {
        #region Camera

        // Control camera movement
        if(cameraCanMove)
        {
            yaw = transform.localEulerAngles.y + _playerInput.Look.x * mouseSensitivity;

            if (!invertCamera)
            {
                pitch -= mouseSensitivity * _playerInput.Look.y;
            }
            else
            {
                // Inverted Y
                pitch += mouseSensitivity * _playerInput.Look.y;
            }

            // Clamp pitch between lookAngle
            pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);

            transform.localEulerAngles = new Vector3(0, yaw, 0);
            playerCamera.transform.localEulerAngles = new Vector3(pitch, 0, 0);
        }

        #region Camera Zoom

        if (enableZoom)
        {
            // Simplified to always use hold-to-zoom logic with the new input system
            if (holdToZoom && !isSprinting)
            {
                isZoomed = _playerInput.Zoom;
            }
            else
            {
                isZoomed = false;
            }

            // Lerps camera.fieldOfView to allow for a smooth transistion
            if(isZoomed)
            {
                playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, zoomFOV, zoomStepTime * Time.deltaTime);
            }
            else if(!isZoomed && !isSprinting)
            {
                playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, fov, zoomStepTime * Time.deltaTime);
            }
        }

        #endregion
        #endregion

        #region Sprint

        if(enableSprint)
        {
            if(isSprinting)
            {
                isZoomed = false;
                playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, sprintFOV, sprintFOVStepTime * Time.deltaTime);

                // Drain sprint remaining while sprinting
                if(!unlimitedSprint)
                {
                    sprintRemaining -= 1 * Time.deltaTime;
                    if (sprintRemaining <= 0)
                    {
                        isSprinting = false;
                        isSprintCooldown = true;
                    }
                }
            }
            else
            {
                // Regain sprint while not sprinting
                sprintRemaining = Mathf.Clamp(sprintRemaining += 1 * Time.deltaTime, 0, sprintDuration);
            }

            // Handles sprint cooldown 
            // When sprint remaining == 0 stops sprint ability until hitting cooldown
            if(isSprintCooldown)
            {
                sprintCooldown -= 1 * Time.deltaTime;
                if (sprintCooldown <= 0)
                {
                    isSprintCooldown = false;
                }
            }
            else
            {
                sprintCooldown = sprintCooldownReset;
            }

            // Handles sprintBar 
            if(useSprintBar && !unlimitedSprint)
            {
                float sprintRemainingPercent = sprintRemaining / sprintDuration;
                sprintBar.transform.localScale = new Vector3(sprintRemainingPercent, 1f, 1f);
            }
        }

        #endregion

        #region Jump

        // Gets input and calls jump method
        if(enableJump && _playerInput.Jump && isGrounded)
        {
            Jump();
        }

        #endregion

        #region Crouch

        if (enableCrouch)
        {
            // Simplified to use a single press/release for crouch
            if (_playerInput.Crouch)
            {
                Crouch();
            }
        }

        #endregion

        CheckGround();

        if(enableHeadBob)
        {
            HeadBob();
        }
    }

    void FixedUpdate()
    {
        #region Movement

        if (playerCanMove)
        {
            // Calculate how fast we should be moving
            Vector3 targetVelocity = new Vector3(_playerInput.Move.x, 0, _playerInput.Move.y);

            // Checks if player is walking and isGrounded
            // Will allow head bob
            if (targetVelocity.x != 0 || targetVelocity.z != 0 && isGrounded)
            {
                isWalking = true;
            }
            else
            {
                isWalking = false;
            }

            // All movement calculations shile sprint is active
            if (enableSprint && _playerInput.Sprint && sprintRemaining > 0f && !isSprintCooldown)
            {
                targetVelocity = transform.TransformDirection(targetVelocity) * sprintSpeed;

                // Apply a force that attempts to reach our target velocity
                Vector3 velocity = rb.linearVelocity;
                Vector3 velocityChange = (targetVelocity - velocity);
                velocityChange.x = Mathf.Clamp(velocityChange.x, -maxVelocityChange, maxVelocityChange);
                velocityChange.z = Mathf.Clamp(velocityChange.z, -maxVelocityChange, maxVelocityChange);
                velocityChange.y = 0;

                // Player is only moving when valocity change != 0
                // Makes sure fov change only happens during movement
                if (velocityChange.x != 0 || velocityChange.z != 0)
                {
                    isSprinting = true;

                    if (isCrouched)
                    {
                        Crouch();
                    }

                    if (hideBarWhenFull && !unlimitedSprint)
                    {
                        sprintBarCG.alpha += 5 * Time.deltaTime;
                    }
                }

                rb.AddForce(velocityChange, ForceMode.VelocityChange);
            }
            // All movement calculations while walking
            else
            {
                isSprinting = false;

                if (hideBarWhenFull && sprintRemaining == sprintDuration)
                {
                    sprintBarCG.alpha -= 3 * Time.deltaTime;
                }

                targetVelocity = transform.TransformDirection(targetVelocity) * walkSpeed;

                // Apply a force that attempts to reach our target velocity
                Vector3 velocity = rb.linearVelocity;
                Vector3 velocityChange = (targetVelocity - velocity);
                velocityChange.x = Mathf.Clamp(velocityChange.x, -maxVelocityChange, maxVelocityChange);
                velocityChange.z = Mathf.Clamp(velocityChange.z, -maxVelocityChange, maxVelocityChange);
                velocityChange.y = 0;

                rb.AddForce(velocityChange, ForceMode.VelocityChange);
            }
        }

        #endregion
    }

    // Sets isGrounded based on a raycast sent straigth down from the player object
    private void CheckGround()
    {
        Vector3 origin = new Vector3(transform.position.x, transform.position.y - (transform.localScale.y * .5f), transform.position.z);
        Vector3 direction = transform.TransformDirection(Vector3.down);
        float distance = .75f;

        if (Physics.Raycast(origin, direction, out RaycastHit hit, distance))
        {
            Debug.DrawRay(origin, direction * distance, Color.red);
            isGrounded = true;
        }
        else
        {
            isGrounded = false;
        }
    }

    private void Jump()
    {
        // Adds force to the player rigidbody to jump
        if (isGrounded)
        {
            rb.AddForce(0f, jumpPower, 0f, ForceMode.Impulse);
            isGrounded = false;
        }

        // When crouched and using toggle system, will uncrouch for a jump
        if(isCrouched && !holdToCrouch)
        {
            Crouch();
        }
    }

    private void Crouch()
    {
        // Stands player up to full height
        // Brings walkSpeed back to original speed
        if(isCrouched)
        {
            transform.localScale = new Vector3(originalScale.x, originalScale.y, originalScale.z);
            walkSpeed /= speedReduction;

            isCrouched = false;
        }
        // Crouches player down to set height
        // Reduces walkSpeed
        else
        {
            transform.localScale = new Vector3(originalScale.x, crouchHeight, originalScale.z);
            walkSpeed *= speedReduction;

            isCrouched = true;
        }
    }

    private void HeadBob()
    {
        if(isWalking)
        {
            // Calculates HeadBob speed during sprint
            if(isSprinting)
            {
                timer += Time.deltaTime * (bobSpeed + sprintSpeed);
            }
            // Calculates HeadBob speed during crouched movement
            else if (isCrouched)
            {
                timer += Time.deltaTime * (bobSpeed * speedReduction);
            }
            // Calculates HeadBob speed during walking
            else
            {
                timer += Time.deltaTime * bobSpeed;
            }
            // Applies HeadBob movement
            joint.localPosition = new Vector3(jointOriginalPos.x + Mathf.Sin(timer) * bobAmount.x, jointOriginalPos.y + Mathf.Sin(timer) * bobAmount.y, jointOriginalPos.z + Mathf.Sin(timer) * bobAmount.z);
        }
        else
        {
            // Resets when play stops moving
            timer = 0;
            joint.localPosition = new Vector3(Mathf.Lerp(joint.localPosition.x, jointOriginalPos.x, Time.deltaTime * bobSpeed), Mathf.Lerp(joint.localPosition.y, jointOriginalPos.y, Time.deltaTime * bobSpeed), Mathf.Lerp(joint.localPosition.z, jointOriginalPos.z, Time.deltaTime * bobSpeed));
        }
    }
}



// Custom Editor
#if UNITY_EDITOR
    [CustomEditor(typeof(FirstPersonController)), InitializeOnLoadAttribute]
    public class FirstPersonControllerEditor : Editor
    {
    FirstPersonController fpc;
    SerializedObject SerFPC;

    private void OnEnable()
    {
        fpc = (FirstPersonController)target;
        SerFPC = new SerializedObject(fpc);
    }

    public override void OnInspectorGUI()
    {
        SerFPC.Update();

        EditorGUILayout.Space();
        GUILayout.Label("Modular First Person Controller", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 16 });
        GUILayout.Label("By Jess Case", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Normal, fontSize = 12 });
        GUILayout.Label("version 1.0.1", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Normal, fontSize = 12 });
        EditorGUILayout.Space();

        #region Camera Setup

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        GUILayout.Label("Camera Setup", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.ExpandWidth(true));
        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(SerFPC.FindProperty("playerCamera"), new GUIContent("Camera", "Camera attached to the controller."));
        EditorGUILayout.Slider(SerFPC.FindProperty("fov"), fpc.zoomFOV, 179f, new GUIContent("Field of View", "The camera’s view angle. Changes the player camera directly."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("cameraCanMove"), new GUIContent("Enable Camera Rotation", "Determines if the camera is allowed to move."));

        GUI.enabled = fpc.cameraCanMove;
        EditorGUILayout.PropertyField(SerFPC.FindProperty("invertCamera"), new GUIContent("Invert Camera Rotation", "Inverts the up and down movement of the camera."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("mouseSensitivity"), new GUIContent("Look Sensitivity", "Determines how sensitive the mouse movement is."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("maxLookAngle"), new GUIContent("Max Look Angle", "Determines the max and min angle the player camera is able to look."));
        GUI.enabled = true;

        EditorGUILayout.PropertyField(SerFPC.FindProperty("lockCursor"), new GUIContent("Lock and Hide Cursor", "Turns off the cursor visibility and locks it to the middle of the screen."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("crosshair"), new GUIContent("Auto Crosshair", "Determines if the basic crosshair will be turned on, and sets is to the center of the screen."));

        if(fpc.crosshair) 
        { 
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(SerFPC.FindProperty("crosshairObject"), new GUIContent("Crosshair Object", "The Image component used for the crosshair."));
            EditorGUILayout.PropertyField(SerFPC.FindProperty("crosshairImage"), new GUIContent("Crosshair Image", "Sprite to use as the crosshair."));
            EditorGUILayout.PropertyField(SerFPC.FindProperty("crosshairColor"), new GUIContent("Crosshair Color", "Determines the color of the crosshair."));
            EditorGUI.indentLevel--; 
        }

        EditorGUILayout.Space();

        #region Camera Zoom Setup

        GUILayout.Label("Zoom", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.ExpandWidth(true));

        EditorGUILayout.PropertyField(SerFPC.FindProperty("enableZoom"), new GUIContent("Enable Zoom", "Determines if the player is able to zoom in while playing."));

        GUI.enabled = fpc.enableZoom;
        EditorGUILayout.PropertyField(SerFPC.FindProperty("holdToZoom"), new GUIContent("Hold to Zoom", "Requires the player to hold the zoom key instead if pressing to zoom and unzoom."));
        EditorGUILayout.Slider(SerFPC.FindProperty("zoomFOV"), 0.1f, fpc.fov, new GUIContent("Zoom FOV", "Determines the field of view the camera zooms to."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("zoomStepTime"), new GUIContent("Step Time", "Determines how fast the FOV transitions while zooming in."));
        GUI.enabled = true;

        #endregion

        #endregion

        #region Movement Setup

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        GUILayout.Label("Movement Setup", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.ExpandWidth(true));
        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(SerFPC.FindProperty("playerCanMove"), new GUIContent("Enable Player Movement", "Determines if the player is allowed to move."));

        GUI.enabled = fpc.playerCanMove;
        EditorGUILayout.Slider(SerFPC.FindProperty("walkSpeed"), 0.1f, fpc.sprintSpeed, new GUIContent("Walk Speed", "Determines how fast the player will move while walking."));
        GUI.enabled = true;

        EditorGUILayout.Space();

        #region Sprint

        GUILayout.Label("Sprint", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.ExpandWidth(true));

        EditorGUILayout.PropertyField(SerFPC.FindProperty("enableSprint"), new GUIContent("Enable Sprint", "Determines if the player is allowed to sprint."));

        GUI.enabled = fpc.enableSprint;
        EditorGUILayout.PropertyField(SerFPC.FindProperty("unlimitedSprint"), new GUIContent("Unlimited Sprint", "Determines if 'Sprint Duration' is enabled. Turning this on will allow for unlimited sprint."));
        EditorGUILayout.Slider(SerFPC.FindProperty("sprintSpeed"), fpc.walkSpeed, 20f, new GUIContent("Sprint Speed", "Determines how fast the player will move while sprinting."));

        GUI.enabled = !fpc.unlimitedSprint;
        EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintDuration"), new GUIContent("Sprint Duration", "Determines how long the player can sprint while unlimited sprint is disabled."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintCooldown"), new GUIContent("Sprint Cooldown", "Determines how long the recovery time is when the player runs out of sprint."));
        GUI.enabled = fpc.enableSprint;

        EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintFOV"), new GUIContent("Sprint FOV", "Determines the field of view the camera changes to while sprinting."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintFOVStepTime"), new GUIContent("Step Time", "Determines how fast the FOV transitions while sprinting."));

        EditorGUILayout.PropertyField(SerFPC.FindProperty("useSprintBar"), new GUIContent("Use Sprint Bar", "Determines if the default sprint bar will appear on screen."));

        if(fpc.useSprintBar)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintBarCG"), new GUIContent("Sprint Bar Canvas Group", "The CanvasGroup containing the sprint bar elements."));
            EditorGUILayout.PropertyField(SerFPC.FindProperty("hideBarWhenFull"), new GUIContent("Hide Full Bar", "Hides the sprint bar when sprint duration is full, and fades the bar in when sprinting. Disabling this will leave the bar on screen at all times when the sprint bar is enabled."));
            EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintBarBG"), new GUIContent("Bar BG", "Object to be used as sprint bar background."));
            EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintBar"), new GUIContent("Bar", "Object to be used as sprint bar foreground."));
            EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintBarWidthPercent"), new GUIContent("Bar Width", "Determines the width of the sprint bar."));
            EditorGUILayout.PropertyField(SerFPC.FindProperty("sprintBarHeightPercent"), new GUIContent("Bar Height", "Determines the height of the sprint bar."));
            EditorGUI.indentLevel--;
        }
        GUI.enabled = true;

        EditorGUILayout.Space();

        #endregion

        #region Jump

        GUILayout.Label("Jump", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.ExpandWidth(true));

        EditorGUILayout.PropertyField(SerFPC.FindProperty("enableJump"), new GUIContent("Enable Jump", "Determines if the player is allowed to jump."));

        GUI.enabled = fpc.enableJump;
        EditorGUILayout.PropertyField(SerFPC.FindProperty("jumpPower"), new GUIContent("Jump Power", "Determines how high the player will jump."));
        GUI.enabled = true;

        EditorGUILayout.Space();

        #endregion

        #region Crouch

        GUILayout.Label("Crouch", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.ExpandWidth(true));

        EditorGUILayout.PropertyField(SerFPC.FindProperty("enableCrouch"), new GUIContent("Enable Crouch", "Determines if the player is allowed to crouch."));

        GUI.enabled = fpc.enableCrouch;
        EditorGUILayout.PropertyField(SerFPC.FindProperty("holdToCrouch"), new GUIContent("Hold To Crouch", "Requires the player to hold the crouch key instead if pressing to crouch and uncrouch."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("crouchHeight"), new GUIContent("Crouch Height", "Determines the y scale of the player object when crouched."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("speedReduction"), new GUIContent("Speed Reduction", "Determines the percent 'Walk Speed' is reduced by. 1 being no reduction, and .5 being half."));
        GUI.enabled = true;

        #endregion

        #endregion

        #region Head Bob

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        GUILayout.Label("Head Bob Setup", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 13 }, GUILayout.ExpandWidth(true));
        EditorGUILayout.Space();

        EditorGUILayout.PropertyField(SerFPC.FindProperty("enableHeadBob"), new GUIContent("Enable Head Bob", "Determines if the camera will bob while the player is walking."));
        
        GUI.enabled = fpc.enableHeadBob;
        EditorGUILayout.PropertyField(SerFPC.FindProperty("joint"), new GUIContent("Camera Joint", "Joint object position is moved while head bob is active."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("bobSpeed"), new GUIContent("Speed", "Determines how often a bob rotation is completed."));
        EditorGUILayout.PropertyField(SerFPC.FindProperty("bobAmount"), new GUIContent("Bob Amount", "Determines the amount the joint moves in both directions on every axes."));
        GUI.enabled = true;

        #endregion

        SerFPC.ApplyModifiedProperties();
    }

}

#endif