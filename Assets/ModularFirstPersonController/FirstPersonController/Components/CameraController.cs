using UnityEngine;

namespace ModularFirstPersonController
{
    public class CameraController : MonoBehaviour
    {
        [Header("Camera Settings")]
        public Camera playerCamera;
        public float fov = 60f;
        public bool invertCamera = false;
        public float mouseSensitivity = 2f;
        public float maxLookAngle = 50f;
        
        [Header("Zoom Settings")]
        public bool enableZoom = true;
        public bool holdToZoom = false;
        public KeyCode zoomKey = KeyCode.Mouse1;
        public float zoomFOV = 30f;
        public float zoomStepTime = 5f;
        
        private float yaw = 0.0f;
        private float pitch = 0.0f;
        private bool isZoomed = false;
        private bool isSprinting = false;
        
        public bool CanMove { get; set; } = true;
        public float SprintFOV { get; set; } = 80f;
        public float SprintFOVStepTime { get; set; } = 10f;
        
        private void Start()
        {
            playerCamera.fieldOfView = fov;
        }
        
        private void Update()
        {
            if (!CanMove) return;
            
            HandleMouseLook();
            HandleZoom();
        }
        
        private void HandleMouseLook()
        {
            yaw = transform.localEulerAngles.y + Input.GetAxis("Mouse X") * mouseSensitivity;
            
            if (!invertCamera)
                pitch -= mouseSensitivity * Input.GetAxis("Mouse Y");
            else
                pitch += mouseSensitivity * Input.GetAxis("Mouse Y");
            
            pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);
            
            transform.localEulerAngles = new Vector3(0, yaw, 0);
            playerCamera.transform.localEulerAngles = new Vector3(pitch, 0, 0);
        }
        
        private void HandleZoom()
        {
            if (!enableZoom || isSprinting) return;
            
            if (Input.GetKeyDown(zoomKey))
            {
                isZoomed = holdToZoom || !isZoomed;
            }
            else if (Input.GetKeyUp(zoomKey) && holdToZoom)
            {
                isZoomed = false;
            }
            
            float targetFOV = isZoomed ? zoomFOV : (isSprinting ? SprintFOV : fov);
            playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFOV, 
                (isZoomed ? zoomStepTime : SprintFOVStepTime) * Time.deltaTime);
        }
        
        public void SetSprintState(bool sprinting)
        {
            isSprinting = sprinting;
            if (sprinting) isZoomed = false;
        }
    }
}
