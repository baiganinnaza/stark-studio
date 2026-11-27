using System;
using System.Collections.Generic;
using System.Numerics;

namespace Stark.Core.Package
{
    /// <summary>Содержимое пакета .stark (формат v1, см. spec/stark-format-v1.md).</summary>
    public sealed class StarkPackage
    {
        public const int SupportedFormatVersion = 1;

        public StarkManifest Manifest { get; internal set; }
        public IReadOnlyList<PartInfo> Parts { get; internal set; }
        public IReadOnlyList<AssemblyStep> Steps { get; internal set; }
        /// <summary>lang → (hintKey → текст).</summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Hints { get; internal set; }
        /// <summary>Байты model.glb: грузятся glTFast в слое Presentation.</summary>
        public byte[] ModelGlb { get; internal set; }
        /// <summary>media/*: путь внутри архива → байты.</summary>
        public IReadOnlyDictionary<string, byte[]> Media { get; internal set; }

        readonly Dictionary<string, PartInfo> _partsById = new Dictionary<string, PartInfo>();

        internal void Index()
        {
            _partsById.Clear();
            foreach (var p in Parts) _partsById[p.Id] = p;
        }

        public PartInfo FindPart(string id) => id != null && _partsById.TryGetValue(id, out var p) ? p : null;

        public IEnumerable<string> AvailableLanguages => Hints.Keys;

        /// <summary>
        /// Текст подсказки шага: нужный язык → ru → en → любой → заголовок шага.
        /// </summary>
        public string HintFor(AssemblyStep step, string lang)
        {
            if (step == null) return "";
            foreach (var l in new[] { lang, "ru", "en" })
            {
                if (l != null && Hints.TryGetValue(l, out var table) &&
                    table.TryGetValue(step.HintKey ?? "", out var text) && !string.IsNullOrWhiteSpace(text))
                    return text;
            }
            foreach (var table in Hints.Values)
                if (table.TryGetValue(step.HintKey ?? "", out var text) && !string.IsNullOrWhiteSpace(text))
                    return text;
            return step.Title ?? "";
        }
    }

    public sealed class StarkManifest
    {
        public int FormatVersion;
        public string ProductId = "";
        public string ProductName = "";
        public string Author = "";
        public string CreatedAt = "";
        public string Units = "m";
        public string UpAxis = "Y";
        public string Generator = "";
        public List<string> Languages = new List<string>();
        public int StatsParts;
        public int StatsSteps;
        public int StatsTriangles;
    }

    public sealed class PartInfo
    {
        public string Id = "";
        public string Name = "";
        public int Qty = 1;
        public List<string> Tags = new List<string>();
        public bool IsFastener => Tags.Contains("fastener");
    }

    public sealed class FastenerGroup
    {
        public List<string> PartIds = new List<string>();
        public string Name = "";
        public int Qty;
        public double? TorqueNm;
        /// <summary>partId → путь установки (смещения в метрах, система glTF). Может отсутствовать.</summary>
        public Dictionary<string, List<Vector3>> PartPaths = new Dictionary<string, List<Vector3>>();
    }

    public sealed class AssemblyStep
    {
        public int Index;
        public string Id = "";
        public string Title = "";
        public List<string> Parts = new List<string>();
        public List<FastenerGroup> Fasteners = new List<FastenerGroup>();
        public List<Vector3> InsertPath = new List<Vector3>();
        /// <summary>partId → путь; значение null, если планировщик не нашёл путь.</summary>
        public Dictionary<string, List<Vector3>> PartPaths = new Dictionary<string, List<Vector3>>();
        public string Tool = "";
        public string HintKey = "";
        public List<string> Checks = new List<string>();
        public string CameraFocus = "";
        public float CameraExplode;
        /// <summary>Не null, если Studio просит инженера проверить шаг.</summary>
        public string Review;

        /// <summary>Все детали шага: основные + весь крепёж.</summary>
        public IEnumerable<string> AllPartIds
        {
            get
            {
                foreach (var p in Parts) yield return p;
                foreach (var f in Fasteners)
                foreach (var p in f.PartIds) yield return p;
            }
        }

        /// <summary>
        /// Путь установки детали: свой partPaths → путь из группы крепежа →
        /// insertPath (только для первой основной детали) → null.
        /// </summary>
        public List<Vector3> PathFor(string partId)
        {
            if (PartPaths.TryGetValue(partId, out var path) && path != null && path.Count > 0) return path;
            foreach (var f in Fasteners)
            {
                if (!f.PartIds.Contains(partId)) continue;
                if (f.PartPaths.TryGetValue(partId, out var fp) && fp != null && fp.Count > 0) return fp;
                // Одинаковые болты: путь одного экземпляра подходит остальным.
                foreach (var any in f.PartPaths.Values)
                    if (any != null && any.Count > 0) return any;
            }
            if (PartPaths.Count == 0 && Parts.Count > 0 && Parts[0] == partId && InsertPath.Count > 0) return InsertPath;
            return null;
        }

        public int FastenerCount
        {
            get
            {
                int n = 0;
                foreach (var f in Fasteners) n += f.Qty > 0 ? f.Qty : f.PartIds.Count;
                return n;
            }
        }
    }

    public sealed class StarkFormatException : Exception
    {
        public StarkFormatException(string message) : base(message) { }
        public StarkFormatException(string message, Exception inner) : base(message, inner) { }
    }
}
