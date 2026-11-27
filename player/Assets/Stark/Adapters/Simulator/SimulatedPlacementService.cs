using System;
using Stark.Device;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Stark.Adapters.Simulator
{
    /// <summary>
    /// Стол симулятора вместо детекции плоскостей. Указатель = мышь (IPointerRayProvider),
    /// как будто пользователь смотрит туда, куда наведена мышь.
    /// </summary>
    public sealed class SimulatedPlacementService : IPlacementService, IPointerRayProvider
    {
        readonly Pose _tableTop;
        readonly Vector2 _size;
        readonly Func<Camera> _camera;
        readonly Func<bool> _pointerEnabled;
        GameObject _surfaceViz;

        public SimulatedPlacementService(Pose tableTop, Vector2 size, Func<Camera> camera, Func<bool> pointerEnabled)
        {
            _tableTop = tableTop;
            _size = size;
            _camera = camera;
            _pointerEnabled = pointerEnabled;
        }

        public bool IsReady => true;
        public int SurfaceCount => 1;

        public bool TryGetSurfacePose(Ray ray, out Pose pose)
        {
            pose = default;
            var plane = new Plane(Vector3.up, _tableTop.position);
            if (!plane.Raycast(ray, out float dist) || dist > 5f) return false;
            var p = ray.GetPoint(dist);
            var local = Quaternion.Inverse(_tableTop.rotation) * (p - _tableTop.position);
            if (Mathf.Abs(local.x) > _size.x / 2f || Mathf.Abs(local.z) > _size.y / 2f) return false;
            pose = new Pose(p, _tableTop.rotation);
            return true;
        }

        public Transform CreateAnchor(Pose pose)
        {
            var go = new GameObject("Simulated Anchor");
            go.transform.SetPositionAndRotation(pose.position, pose.rotation);
            return go.transform;
        }

        public void ReleaseAnchor(Transform anchor)
        {
            if (anchor != null) UnityEngine.Object.Destroy(anchor.gameObject);
        }

        public void SetSurfaceVisualization(bool visible)
        {
            if (_surfaceViz == null)
            {
                _surfaceViz = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _surfaceViz.name = "Simulated Plane";
                UnityEngine.Object.Destroy(_surfaceViz.GetComponent<Collider>());
                _surfaceViz.transform.SetPositionAndRotation(_tableTop.position + Vector3.up * 0.001f,
                    _tableTop.rotation * Quaternion.Euler(90, 0, 0));
                _surfaceViz.transform.localScale = new Vector3(_size.x, _size.y, 1);
                var shader = (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                                 ? Shader.Find("Universal Render Pipeline/Unlit")
                                 : null) ?? Shader.Find("Unlit/Color");
                if (shader != null)
                {
                    var m = new Material(shader);
                    var c = new Color(0.2f, 0.6f, 0.9f);
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                    if (m.HasProperty("_Color")) m.SetColor("_Color", c);
                    _surfaceViz.GetComponent<Renderer>().sharedMaterial = m;
                }
            }
            _surfaceViz.SetActive(visible);
        }

        public bool TryGetPointerRay(out Ray ray)
        {
            ray = default;
            var cam = _camera();
            var mouse = Mouse.current;
            if (cam == null || mouse == null || !_pointerEnabled()) return false;
            ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            return true;
        }
    }
}
