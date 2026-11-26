using System.Collections.Generic;
using Stark.Device;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Stark.Adapters.XR
{
    /// <summary>
    /// Стол через AR Foundation: луч взгляда → горизонтальная плоскость → ARAnchor.
    /// Используются только API, общие для AR Foundation 5.1 и 6.x.
    /// [Нужно устройство] устойчивость якоря при обходе стола и время поиска плоскости
    /// проверяются только на Air 2 Ultra (этап 0, критерий go/no-go).
    /// </summary>
    public sealed class ArPlacementService : IPlacementService
    {
        readonly ARRaycastManager _raycast;
        readonly ARPlaneManager _planes;
        readonly ARAnchorManager _anchors;
        readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>();
        bool _visualize = true;

        public ArPlacementService(ARRaycastManager raycast, ARPlaneManager planes, ARAnchorManager anchors)
        {
            _raycast = raycast;
            _planes = planes;
            _anchors = anchors;
            if (_planes != null) _planes.requestedDetectionMode = PlaneDetectionMode.Horizontal;
        }

        public bool IsReady => _raycast != null && ARSession.state == ARSessionState.SessionTracking;

        public int SurfaceCount
        {
            get
            {
                if (_planes == null) return 0;
                int n = 0;
                foreach (var p in _planes.trackables)
                    if (p.alignment == PlaneAlignment.HorizontalUp) n++;
                return n;
            }
        }

        public bool TryGetSurfacePose(Ray ray, out Pose pose)
        {
            pose = default;
            if (_raycast == null) return false;
            if (!_raycast.Raycast(ray, _hits, TrackableType.PlaneWithinPolygon)) return false;
            foreach (var hit in _hits)
            {
                // Только горизонтальные поверхности, смотрящие вверх (стол), и не дальше вытянутой руки×2.
                if (hit.trackable is ARPlane plane && plane.alignment != PlaneAlignment.HorizontalUp) continue;
                if (hit.distance > 2.5f) continue;
                pose = hit.pose;
                return true;
            }
            return false;
        }

        public Transform CreateAnchor(Pose pose)
        {
            var go = new GameObject("Stark Anchor");
            go.transform.SetPositionAndRotation(pose.position, pose.rotation);
            // ARAnchor на объекте = система трекинга держит его в мире (работает в AR Foundation 5 и 6,
            // нужен ARAnchorManager в сцене).
            if (_anchors != null) go.AddComponent<ARAnchor>();
            return go.transform;
        }

        public void ReleaseAnchor(Transform anchor)
        {
            if (anchor != null) Object.Destroy(anchor.gameObject);
        }

        public void SetSurfaceVisualization(bool visible)
        {
            _visualize = visible;
            Tick();
        }

        /// <summary>
        /// Вызывает XrRig каждый кадр: новые плоскости во время сборки не должны мелькать
        /// поверх модели. (Без события trackablesChanged, его нет в AR Foundation 5.)
        /// </summary>
        public void Tick()
        {
            if (_planes == null) return;
            foreach (var p in _planes.trackables)
                if (p.gameObject.activeSelf != _visualize) p.gameObject.SetActive(_visualize);
        }
    }
}
