using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>
    /// Материалы подсветки шагов. Сцена получает готовые .mat из меню Stark → Create Player Scene
    /// (так шейдеры гарантированно попадают в сборку). Если поле пустое, создаём материал в рантайме.
    /// </summary>
    [System.Serializable]
    public sealed class StarkMaterials
    {
        public static readonly Color CurrentColor = new Color(0.10f, 0.80f, 1.00f, 1f);   // голубой
        public static readonly Color FastenerColor = new Color(1.00f, 0.55f, 0.05f, 1f);  // оранжевый
        public static readonly Color GhostColor = new Color(0.75f, 0.85f, 1.00f, 0.15f);  // призрак 15%
        public static readonly Color ButtonColor = new Color(0.12f, 0.45f, 0.70f, 1f);
        public static readonly Color ButtonHoverColor = new Color(0.20f, 0.75f, 1.00f, 1f);

        public Material current;
        public Material fastener;
        public Material ghost;
        public Material ui;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        public void EnsureCreated()
        {
            if (current == null) current = CreateOpaque("Stark Current", CurrentColor);
            if (fastener == null) fastener = CreateOpaque("Stark Fastener", FastenerColor);
            if (ghost == null) ghost = CreateTransparent("Stark Ghost", GhostColor);
            if (ui == null) ui = CreateOpaque("Stark UI", ButtonColor);
        }

        /// <summary>URP включён (ассет пайплайна назначен). Иначе работаем на встроенном рендере.</summary>
        public static bool IsUrp => UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null;

        public static Shader FindUnlitShader()
        {
            return (IsUrp ? Shader.Find("Universal Render Pipeline/Unlit") : null)
                   ?? Shader.Find("Unlit/Color")
                   ?? Shader.Find("Sprites/Default");
        }

        public static Shader FindLitShader()
        {
            return (IsUrp ? Shader.Find("Universal Render Pipeline/Lit") : null)
                   ?? Shader.Find("Standard")
                   ?? FindUnlitShader();
        }

        public static Material CreateOpaque(string name, Color color)
        {
            var m = new Material(FindUnlitShader()) { name = name };
            SetColor(m, color);
            return m;
        }

        /// <summary>Прозрачный URP Unlit: те же свойства, что выставляет инспектор URP.</summary>
        public static Material CreateTransparent(string name, Color color)
        {
            // URP Unlit в режиме Transparent; на встроенном рендере Sprites/Default (прозрачный, без света).
            var shader = (IsUrp ? Shader.Find("Universal Render Pipeline/Unlit") : null)
                         ?? Shader.Find("Sprites/Default")
                         ?? FindUnlitShader();
            var m = new Material(shader) { name = name };
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f);   // Transparent
                m.SetFloat("_Blend", 0f);     // Alpha
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
                m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "Transparent");
            }
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            SetColor(m, color);
            return m;
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
            if (m.HasProperty(ColorId)) m.SetColor(ColorId, c);
        }

        /// <summary>Цвет через MaterialPropertyBlock: пульсация без копирования материалов.</summary>
        public static void SetColor(MaterialPropertyBlock block, Color c)
        {
            block.SetColor(BaseColorId, c);
            block.SetColor(ColorId, c);
        }
    }
}
