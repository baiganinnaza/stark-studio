using Stark.Device;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Stark.Adapters.Simulator
{
    /// <summary>
    /// Разработка без очков: комната со столом, камера на высоте глаз, виртуальные руки от мыши.
    /// Камера: зажать ПКМ и двигать мышь = смотреть; ПКМ + WASD = ходить вокруг стола.
    /// Модель держится на «якоре» так же, как на очках, поэтому обход стола проверяет логику якоря.
    /// </summary>
    public sealed class SimulatorRig : MonoBehaviour
    {
        public float eyeHeight = 1.6f;
        public float tableHeight = 0.75f;
        public Vector2 tableSize = new Vector2(1.2f, 0.75f);
        public float tableDistance = 0.85f;
        public float lookSpeed = 0.15f;
        public float moveSpeed = 1.2f;

        public Camera Camera { get; private set; }
        public SimulatedPlacementService Placement { get; private set; }
        public SimulatedHandInput Hands { get; private set; }
        public bool IsLooking { get; private set; }

        float _pitch = 35f;
        float _yaw;

        public DeviceServices Build()
        {
            var camGo = new GameObject("Simulator Camera");
            camGo.transform.SetParent(transform, false);
            camGo.transform.localPosition = new Vector3(0, eyeHeight, 0);
            Camera = camGo.AddComponent<Camera>();
            Camera.nearClipPlane = 0.05f;
            Camera.fieldOfView = 46f; // примерно вертикальный FOV Air 2 Ultra при 52° по диагонали
            Camera.clearFlags = CameraClearFlags.SolidColor;
            Camera.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
            camGo.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();

            var lightGo = new GameObject("Simulator Light");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Floor";
            floor.transform.SetParent(transform, false);
            floor.transform.localScale = Vector3.one * 0.6f;
            Tint(floor, new Color(0.18f, 0.19f, 0.21f));

            var table = GameObject.CreatePrimitive(PrimitiveType.Cube);
            table.name = "Table";
            table.transform.SetParent(transform, false);
            table.transform.localPosition = new Vector3(0, tableHeight / 2f, tableDistance);
            table.transform.localScale = new Vector3(tableSize.x, tableHeight, tableSize.y);
            Tint(table, new Color(0.42f, 0.33f, 0.25f));
            Destroy(table.GetComponent<Collider>()); // кнопки и модель не должны ловить луч стола

            var top = new Pose(transform.TransformPoint(new Vector3(0, tableHeight, tableDistance)), transform.rotation);
            Placement = new SimulatedPlacementService(top, tableSize, () => Camera, () => !IsLooking);
            Hands = new SimulatedHandInput(() => Camera, () => IsLooking, top.position.y);

            _yaw = transform.eulerAngles.y;
            ApplyLook();

            var keyboard = new Stark.Adapters.Keyboard.KeyboardSystemInput { SuppressLetters = () => IsLooking };
            return new DeviceServices
            {
                Name = "Симулятор редактора (мышь + клавиатура)",
                Camera = Camera,
                Placement = Placement,
                Hands = Hands,
                SystemInputs = new ISystemInput[] { keyboard },
            };
        }

        static void Tint(GameObject go, Color c)
        {
            var r = go.GetComponent<Renderer>();
            var shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                ? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Standard");
            if (shader == null) return;
            var m = new Material(shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            r.sharedMaterial = m;
        }

        void Update()
        {
            if (Camera == null) return;
            var mouse = Mouse.current;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            IsLooking = mouse != null && mouse.rightButton.isPressed;
            if (IsLooking)
            {
                var d = mouse.delta.ReadValue();
                _yaw += d.x * lookSpeed;
                _pitch = Mathf.Clamp(_pitch - d.y * lookSpeed, -60f, 80f);
                ApplyLook();
                if (kb != null)
                {
                    var move = Vector3.zero;
                    if (kb.wKey.isPressed) move += Vector3.forward;
                    if (kb.sKey.isPressed) move += Vector3.back;
                    if (kb.aKey.isPressed) move += Vector3.left;
                    if (kb.dKey.isPressed) move += Vector3.right;
                    var flat = Quaternion.Euler(0, _yaw, 0);
                    Camera.transform.position += flat * move * (moveSpeed * Time.deltaTime);
                }
            }
            Hands.Tick(Time.deltaTime);
        }

        void ApplyLook() => Camera.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
    }
}
