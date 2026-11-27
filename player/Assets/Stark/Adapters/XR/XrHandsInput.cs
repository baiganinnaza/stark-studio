using System.Collections.Generic;
using Stark.Core.Gestures;
using Stark.Device;
using UnityEngine;
using UnityEngine.XR.Hands;
using NVector3 = System.Numerics.Vector3;

namespace Stark.Adapters.XR
{
    /// <summary>
    /// Суставы рук из XR Hands (26 суставов OpenXR; XREAL SDK 3.x отдаёт их через XRHandSubsystem).
    /// Позы суставов приходят в системе XR Origin, переводим в мир.
    /// [Нужно устройство] качество трекинга (свет, перчатки, края кадра) проверяется только на очках.
    /// </summary>
    public sealed class XrHandsInput : IHandInput
    {
        readonly Transform _origin;
        readonly List<XRHandSubsystem> _found = new List<XRHandSubsystem>();
        XRHandSubsystem _subsystem;
        float _nextSearch;

        public XrHandsInput(Transform origin) { _origin = origin; }

        public bool IsAvailable
        {
            get
            {
                if (_subsystem != null && _subsystem.running) return true;
                if (Time.unscaledTime < _nextSearch) return false;
                _nextSearch = Time.unscaledTime + 1f; // подсистема может стартовать позже сцены
                _found.Clear();
                SubsystemManager.GetSubsystems(_found);
                _subsystem = null;
                foreach (var s in _found)
                    if (s.running) { _subsystem = s; break; }
                return _subsystem != null;
            }
        }

        public HandFrame GetFrame(Hand hand)
        {
            if (_subsystem == null) return HandFrame.NotTracked;
            var h = hand == Hand.Left ? _subsystem.leftHand : _subsystem.rightHand;
            if (!h.isTracked) return HandFrame.NotTracked;
            if (!TryJoint(h, XRHandJointID.ThumbTip, out var thumb) ||
                !TryJoint(h, XRHandJointID.IndexTip, out var index))
                return HandFrame.NotTracked;
            TryJoint(h, XRHandJointID.Palm, out var palm);
            return new HandFrame { Tracked = true, ThumbTip = thumb, IndexTip = index, Palm = palm };
        }

        bool TryJoint(XRHand hand, XRHandJointID id, out NVector3 world)
        {
            world = default;
            if (!hand.GetJoint(id).TryGetPose(out Pose pose)) return false;
            var p = _origin != null ? _origin.TransformPoint(pose.position) : pose.position;
            world = new NVector3(p.x, p.y, p.z);
            return true;
        }
    }
}
