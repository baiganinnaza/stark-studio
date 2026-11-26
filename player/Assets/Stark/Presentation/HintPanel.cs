using System.Text;
using Stark.Core.Package;
using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>
    /// Подсказка над моделью: номер шага, текст (≤80 символов), инструмент, крепёж и момент,
    /// пункты самопроверки. Поворачивается к пользователю только вокруг вертикали.
    /// </summary>
    public sealed class HintPanel : MonoBehaviour
    {
        TextMesh _title;
        TextMesh _body;
        TextMesh _status;
        Camera _camera;

        public static HintPanel Create(Transform parent, Camera camera)
        {
            var go = new GameObject("Hint Panel");
            go.transform.SetParent(parent, false);
            var p = go.AddComponent<HintPanel>();
            p._camera = camera;
            p._title = WorldText.Create("Title", go.transform, 0.020f, TextAnchor.LowerCenter, new Color(0.55f, 0.9f, 1f));
            p._title.transform.localPosition = new Vector3(0, 0.075f, 0);
            p._body = WorldText.Create("Body", go.transform, 0.016f, TextAnchor.LowerCenter, Color.white);
            p._body.transform.localPosition = new Vector3(0, 0.0f, 0);
            p._status = WorldText.Create("Status", go.transform, 0.014f, TextAnchor.UpperCenter, new Color(1f, 0.85f, 0.4f));
            p._status.transform.localPosition = new Vector3(0, -0.012f, 0);
            return p;
        }

        public void SetCamera(Camera camera) => _camera = camera;

        public void ShowStep(StarkPackage pkg, AssemblyStep step, int index, int count, string lang)
        {
            _title.text = $"Шаг {index + 1} из {count}";
            var sb = new StringBuilder();
            sb.Append(Wrap(pkg.HintFor(step, lang), 42));
            if (!string.IsNullOrEmpty(step.Tool)) sb.Append("\nИнструмент: ").Append(step.Tool);
            foreach (var f in step.Fasteners)
            {
                sb.Append("\n<color=#FF9A1F>").Append(f.Name).Append(" ×").Append(f.Qty > 0 ? f.Qty : f.PartIds.Count);
                if (f.TorqueNm.HasValue) sb.Append(" · ").Append(f.TorqueNm.Value.ToString("0.#")).Append(" Н·м");
                sb.Append("</color>");
            }
            foreach (var c in step.Checks) sb.Append("\n- ").Append(c);
            if (!string.IsNullOrEmpty(step.Review)) sb.Append("\n<color=#FFCC55>! Шаг не проверен инженером</color>");
            _body.text = sb.ToString();
        }

        public void ShowMessage(string title, string body)
        {
            _title.text = title ?? "";
            _body.text = Wrap(body ?? "", 42);
        }

        public void SetStatus(string text) => _status.text = text ?? "";

        void LateUpdate()
        {
            if (_camera == null) return;
            var toCam = transform.position - _camera.transform.position;
            toCam.y = 0;
            if (toCam.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(toCam, Vector3.up);
        }

        /// <summary>Перенос по словам: TextMesh сам строки не переносит.</summary>
        public static string Wrap(string text, int width)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= width) return text;
            var sb = new StringBuilder();
            int line = 0;
            foreach (var word in text.Split(' '))
            {
                if (line > 0 && line + 1 + word.Length > width)
                {
                    sb.Append('\n');
                    line = 0;
                }
                else if (line > 0)
                {
                    sb.Append(' ');
                    line++;
                }
                sb.Append(word);
                line += word.Length;
            }
            return sb.ToString();
        }
    }
}
