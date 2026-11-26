using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>
    /// Текст в пространстве. Используем TextMesh со встроенным динамическим шрифтом
    /// (LegacyRuntime.ttf = Liberation Sans, есть кириллица), чтобы не требовать
    /// импорта ресурсов TextMeshPro и отдельного SDF-атласа для ru/kk.
    /// На оптических очках чёрный цвет прозрачен, поэтому текст всегда светлый.
    /// </summary>
    public static class WorldText
    {
        static Font _font;

        public static Font Font
        {
            get
            {
                if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return _font;
            }
        }

        /// <param name="height">Высота строки в метрах.</param>
        public static TextMesh Create(string name, Transform parent, float height, TextAnchor anchor, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var tm = go.AddComponent<TextMesh>();
            tm.font = Font;
            tm.fontSize = 64;               // разрешение растра шрифта
            // Высота строки TextMesh ≈ fontSize * characterSize / 10 (единицы мира). [О] уточнить на очках.
            tm.characterSize = height * 10f / tm.fontSize;
            tm.anchor = anchor;
            tm.alignment = anchor == TextAnchor.LowerLeft || anchor == TextAnchor.UpperLeft || anchor == TextAnchor.MiddleLeft
                ? TextAlignment.Left
                : TextAlignment.Center;
            tm.color = color;
            tm.richText = true;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = Font.material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return tm;
        }
    }
}
