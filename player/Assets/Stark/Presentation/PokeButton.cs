using System;
using Stark.Core.Gestures;
using Stark.Device;
using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>
    /// Плоская кнопка, лежащая на столе перед моделью. Нажимается кончиком указательного
    /// пальца сверху (логика в Core: PokeDetector), в симуляторе кликом мыши.
    /// Размер 6×6 см: крупная цель, руки у сборщика в работе и трекинг не идеален.
    /// </summary>
    public sealed class PokeButton : MonoBehaviour, IPointerPressable
    {
        public static readonly Vector3 DefaultSize = new Vector3(0.06f, 0.016f, 0.06f);

        public event Action Pressed;

        PokeDetector _left;
        PokeDetector _right;
        Renderer _visual;
        TextMesh _label;
        MaterialPropertyBlock _block;
        float _flash;
        bool _interactable = true;

        public static PokeButton Create(string name, string label, Transform parent, Vector3 localPos, Material material)
        {
            var go = new GameObject("Button " + name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var b = go.AddComponent<PokeButton>();
            b.Build(label, material);
            return b;
        }

        void Build(string label, Material material)
        {
            var half = DefaultSize * 0.5f;
            _left = new PokeDetector(half.ToNumerics());
            _right = new PokeDetector(half.ToNumerics());
            _block = new MaterialPropertyBlock();

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Visual";
            cube.transform.SetParent(transform, false);
            cube.transform.localScale = DefaultSize;
            _visual = cube.GetComponent<Renderer>();
            _visual.sharedMaterial = material;
            _visual.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // Коллайдер куба оставляем: по нему симулятор находит кнопку лучом мыши.

            _label = WorldText.Create("Label", transform, 0.016f, TextAnchor.MiddleCenter, Color.white);
            _label.text = label;
            // Подпись над кнопкой, наклонена к пользователю (он смотрит сверху-спереди).
            _label.transform.localPosition = new Vector3(0, half.y + 0.022f, -half.z * 0.2f);
            _label.transform.localRotation = Quaternion.Euler(50f, 0, 0);
            SetColor(StarkMaterials.ButtonColor);
        }

        public string Label
        {
            get => _label.text;
            set => _label.text = value;
        }

        public bool Interactable
        {
            get => _interactable;
            set
            {
                _interactable = value;
                SetColor(value ? StarkMaterials.ButtonColor : new Color(0.25f, 0.25f, 0.28f, 1f));
            }
        }

        /// <summary>Вызывается StarkPlayer каждый кадр с кадрами рук.</summary>
        public void Track(HandFrame left, HandFrame right, float time)
        {
            if (!isActiveAndEnabled) return;
            bool pressed = Check(_left, left, time) | Check(_right, right, time);
            bool hover = _left.IsHovered || _right.IsHovered;
            if (pressed) Fire();
            if (_flash > 0) _flash -= Time.deltaTime;
            else if (_interactable) SetColor(hover ? StarkMaterials.ButtonHoverColor : StarkMaterials.ButtonColor);
        }

        bool Check(PokeDetector d, HandFrame f, float time)
        {
            var local = Quaternion.Inverse(transform.rotation) * (f.IndexTip.ToUnity() - transform.position);
            // Кнопка может быть внутри масштабированного объекта: переводим в метры кнопки.
            var s = transform.lossyScale;
            local = new Vector3(local.x / Mathf.Max(1e-4f, s.x), local.y / Mathf.Max(1e-4f, s.y), local.z / Mathf.Max(1e-4f, s.z));
            return d.Update(local.ToNumerics(), f.Tracked, time);
        }

        public void PressFromPointer() => Fire();

        void Fire()
        {
            if (!_interactable) return;
            _flash = 0.15f;
            SetColor(Color.white);
            Pressed?.Invoke();
        }

        void SetColor(Color c)
        {
            if (_visual == null) return;
            StarkMaterials.SetColor(_block, c);
            _visual.SetPropertyBlock(_block);
        }
    }
}
