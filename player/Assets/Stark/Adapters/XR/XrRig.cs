using Stark.Device;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace Stark.Adapters.XR
{
    /// <summary>
    /// Адаптер для XREAL Air 2 Ultra + Beam Pro (и любых устройств с AR Foundation + XR Hands).
    /// Код не ссылается на XREAL SDK напрямую: SDK подключается как XR-провайдер
    /// (Project Settings → XR Plug-in Management → Android → XREAL) и реализует
    /// стандартные подсистемы Unity: плоскости, якоря, raycast, руки. Поэтому тот же адаптер
    /// с другим провайдером (OpenXR) пойдёт на Quest 3 / Pico, а с ARCore на планшет.
    /// </summary>
    public sealed class XrRig : MonoBehaviour
    {
        public XROrigin origin;
        public Camera xrCamera;
        public ARRaycastManager raycastManager;
        public ARPlaneManager planeManager;
        public ARAnchorManager anchorManager;

        ArPlacementService _placement;

        public DeviceServices Build()
        {
            if (origin == null) origin = GetComponentInChildren<XROrigin>(true);
            if (xrCamera == null && origin != null) xrCamera = origin.Camera;
            if (raycastManager == null) raycastManager = GetComponentInChildren<ARRaycastManager>(true);
            if (planeManager == null) planeManager = GetComponentInChildren<ARPlaneManager>(true);
            if (anchorManager == null) anchorManager = GetComponentInChildren<ARAnchorManager>(true);

            if (xrCamera != null)
            {
                // Оптические очки: чёрный = прозрачный. Небо/фон рисовать нельзя.
                xrCamera.clearFlags = CameraClearFlags.SolidColor;
                xrCamera.backgroundColor = Color.black;
                xrCamera.nearClipPlane = 0.05f;
                xrCamera.tag = "MainCamera";
            }

            return new DeviceServices
            {
                Name = "XR (AR Foundation + XR Hands)",
                Camera = xrCamera,
                Placement = _placement = new ArPlacementService(raycastManager, planeManager, anchorManager),
                Hands = new XrHandsInput(origin != null ? origin.transform : transform),
                SystemInputs = new ISystemInput[] { new Stark.Adapters.Keyboard.KeyboardSystemInput() },
            };
        }

        void Update() => _placement?.Tick();
    }
}
