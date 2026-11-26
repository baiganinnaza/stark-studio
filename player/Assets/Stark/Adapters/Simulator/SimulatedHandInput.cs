using System;
using Stark.Core.Gestures;
using Stark.Device;
using UnityEngine;
using UnityEngine.InputSystem;
using NVector3 = System.Numerics.Vector3;

namespace Stark.Adapters.Simulator
{
    /// <summary>
    /// Виртуальные руки из мыши. Кадры идут в тот же GestureRecognizer, что и на очках,
    /// поэтому симулятор проверяет настоящую логику жестов, а не обходной путь.
    ///
    /// Правая рука = точка на луче мыши на высоте ~12 см над столом (как рука над моделью).
    ///   ЛКМ зажата = щипок. Короткий клик = Tap, быстрый рывок вбок = Flick (листать),
    ///   удержание и движение = Drag (вращение модели).
    ///   ЛКМ по кнопке = нажатие пальцем (поки).
    /// Колесо мыши = щипок двумя руками, руки сходятся/расходятся (масштаб).
    /// </summary>
    public sealed class SimulatedHandInput : IHandInput
    {
        /// <summary>Высота «руки» над столом (м).</summary>
        public float handHeightAboveTable = 0.12f;
        public float fallbackDistance = 0.6f;
        const float Open = 0.06f;
        const float Closed = 0.008f;

        readonly Func<Camera> _camera;
        readonly Func<bool> _suspended;
        readonly float _handPlaneY;
        float _pinchDepth;
        bool _pinch;
        bool _pressingButton;
        float _twoHandTimer;
        float _twoHandSeparation;

        public SimulatedHandInput(Func<Camera> camera, Func<bool> suspended, float tableTopY)
        {
            _camera = camera;
            _suspended = suspended;
            _handPlaneY = tableTopY + handHeightAboveTable;
        }

        /// <summary>Расстояние по лучу до плоскости «рук» над столом.</summary>
        float DepthAlong(Ray ray)
        {
            var plane = new Plane(Vector3.up, new Vector3(0, _handPlaneY, 0));
            return plane.Raycast(ray, out float d) && d > 0.25f && d < 2.0f ? d : fallbackDistance;
        }

        public bool IsAvailable => _camera() != null && Mouse.current != null;

        /// <summary>Вызывает SimulatorRig каждый кадр.</summary>
        public void Tick(float dt)
        {
            var mouse = Mouse.current;
            var cam = _camera();
            if (mouse == null || cam == null) return;

            if (mouse.leftButton.wasPressedThisFrame && !_suspended())
            {
                // Клик по поки-кнопке нажимает её и не начинает щипок.
                var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (Physics.Raycast(ray, out var hit, 5f) &&
                    hit.collider.GetComponentInParent<IPointerPressable>() is IPointerPressable button)
                {
                    button.PressFromPointer();
                    _pressingButton = true;
                }
            }
            if (!mouse.leftButton.isPressed) _pressingButton = false;
            bool pinch = mouse.leftButton.isPressed && !_pressingButton && !_suspended();
            // Во время щипка глубина фиксирована: движение мыши = движение руки в плоскости.
            if (pinch && !_pinch) _pinchDepth = DepthAlong(cam.ScreenPointToRay(mouse.position.ReadValue()));
            _pinch = pinch;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                if (_twoHandTimer <= 0) _twoHandSeparation = 0.25f;
                // Один «щелчок» колеса (120 единиц на Windows, 1 на macOS) ≈ 10 % масштаба.
                float notches = Mathf.Clamp(scroll / (Mathf.Abs(scroll) > 10f ? 120f : 1f), -3f, 3f);
                _twoHandSeparation = Mathf.Clamp(_twoHandSeparation * Mathf.Pow(1.1f, notches), 0.05f, 0.9f);
                _twoHandTimer = 0.35f;
            }
            else if (_twoHandTimer > 0)
            {
                _twoHandTimer -= dt;
            }
        }

        public HandFrame GetFrame(Hand hand)
        {
            var cam = _camera();
            var mouse = Mouse.current;
            if (cam == null || mouse == null) return HandFrame.NotTracked;

            if (_twoHandTimer > 0)
            {
                var centerRay = new Ray(cam.transform.position, cam.transform.forward);
                var center = centerRay.GetPoint(DepthAlong(centerRay));
                float side = hand == Hand.Left ? -0.5f : 0.5f;
                return Make(center + cam.transform.right * (side * _twoHandSeparation), cam, true);
            }

            if (hand == Hand.Left) return HandFrame.NotTracked;
            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            return Make(ray.GetPoint(_pinch ? _pinchDepth : DepthAlong(ray)), cam, _pinch);
        }

        static HandFrame Make(Vector3 pinchPoint, Camera cam, bool pinched)
        {
            float gap = pinched ? Closed : Open;
            var up = cam.transform.up;
            var thumb = pinchPoint - up * (gap * 0.5f);
            var index = pinchPoint + up * (gap * 0.5f);
            return new HandFrame
            {
                Tracked = true,
                ThumbTip = new NVector3(thumb.x, thumb.y, thumb.z),
                IndexTip = new NVector3(index.x, index.y, index.z),
                Palm = new NVector3(pinchPoint.x, pinchPoint.y - 0.05f, pinchPoint.z),
            };
        }
    }
}
