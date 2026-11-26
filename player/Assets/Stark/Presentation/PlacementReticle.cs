using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>Метка на столе в режиме размещения: кольцо размером с будущую модель.</summary>
    public sealed class PlacementReticle : MonoBehaviour
    {
        Transform _ring;
        Transform _arrow;

        public static PlacementReticle Create(Transform parent, Material material)
        {
            var go = new GameObject("Placement Reticle");
            go.transform.SetParent(parent, false);
            var r = go.AddComponent<PlacementReticle>();

            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(ring.GetComponent<Collider>());
            ring.name = "Ring";
            ring.transform.SetParent(go.transform, false);
            ring.GetComponent<Renderer>().sharedMaterial = material;
            r._ring = ring.transform;

            // Стрелка «к пользователю»: показывает, какой стороной встанет модель.
            var arrow = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(arrow.GetComponent<Collider>());
            arrow.name = "Front";
            arrow.transform.SetParent(go.transform, false);
            arrow.GetComponent<Renderer>().sharedMaterial = material;
            r._arrow = arrow.transform;
            r.SetDiameter(0.3f);
            go.SetActive(false);
            return r;
        }

        public void SetDiameter(float d)
        {
            d = Mathf.Max(0.05f, d);
            _ring.localScale = new Vector3(d, 0.001f, d);
            _arrow.localScale = new Vector3(0.02f, 0.002f, d * 0.25f);
            _arrow.localPosition = new Vector3(0, 0.001f, -d * 0.5f);
        }

        public void Show(Pose pose)
        {
            gameObject.SetActive(true);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
        }

        public void Hide() => gameObject.SetActive(false);
    }
}
