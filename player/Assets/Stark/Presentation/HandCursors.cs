using Stark.Core.Gestures;
using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>
    /// Точки на кончиках большого и указательного пальцев. На очках подтверждают, что рука
    /// видна трекингу; в симуляторе показывают виртуальную руку. Щипок = точки голубые.
    /// </summary>
    public sealed class HandCursors : MonoBehaviour
    {
        Transform[] _dots;
        Renderer[] _renderers;
        MaterialPropertyBlock _block;

        public static HandCursors Create(Transform parent, Material material)
        {
            var go = new GameObject("Hand Cursors");
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HandCursors>();
            h._block = new MaterialPropertyBlock();
            h._dots = new Transform[4];
            h._renderers = new Renderer[4];
            for (int i = 0; i < 4; i++)
            {
                var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(s.GetComponent<Collider>());
                s.name = "Tip " + i;
                s.transform.SetParent(go.transform, false);
                s.transform.localScale = Vector3.one * 0.012f;
                h._dots[i] = s.transform;
                h._renderers[i] = s.GetComponent<Renderer>();
                h._renderers[i].sharedMaterial = material;
                h._renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            return h;
        }

        public void Show(HandFrame left, HandFrame right, bool leftPinch, bool rightPinch)
        {
            Place(0, left, leftPinch);
            Place(2, right, rightPinch);
        }

        void Place(int i, HandFrame f, bool pinching)
        {
            _dots[i].gameObject.SetActive(f.Tracked);
            _dots[i + 1].gameObject.SetActive(f.Tracked);
            if (!f.Tracked) return;
            _dots[i].position = f.ThumbTip.ToUnity();
            _dots[i + 1].position = f.IndexTip.ToUnity();
            StarkMaterials.SetColor(_block, pinching ? StarkMaterials.CurrentColor : new Color(0.9f, 0.9f, 0.9f, 1f));
            _renderers[i].SetPropertyBlock(_block);
            _renderers[i + 1].SetPropertyBlock(_block);
        }
    }
}
