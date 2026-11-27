using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Stark.Core.Json;

namespace Stark.Core.Package
{
    /// <summary>
    /// Распаковка и проверка .stark. Не трогает Unity: на вход байты архива
    /// (на Android файл из StreamingAssets читается через UnityWebRequest в слое App).
    /// </summary>
    public static class StarkPackageLoader
    {
        public static StarkPackage LoadFile(string path) => Load(File.ReadAllBytes(path));

        public static StarkPackage Load(byte[] archiveBytes)
        {
            if (archiveBytes == null || archiveBytes.Length == 0)
                throw new StarkFormatException("Пустой файл пакета");

            Dictionary<string, byte[]> entries;
            try
            {
                entries = ReadZip(archiveBytes);
            }
            catch (InvalidDataException e)
            {
                throw new StarkFormatException("Файл не является пакетом .stark (не zip)", e);
            }

            var manifest = ParseManifest(Text(entries, "manifest.json", required: true));
            if (manifest.FormatVersion != StarkPackage.SupportedFormatVersion)
                throw new StarkFormatException(
                    $"Версия формата {manifest.FormatVersion} не поддерживается (плеер понимает v{StarkPackage.SupportedFormatVersion}). Обновите Stark Player.");

            if (!entries.TryGetValue("model.glb", out var glb) || glb.Length < 12)
                throw new StarkFormatException("В пакете нет model.glb");
            // Сигнатура glTF binary: "glTF"
            if (glb[0] != 0x67 || glb[1] != 0x6C || glb[2] != 0x54 || glb[3] != 0x46)
                throw new StarkFormatException("model.glb повреждён (нет сигнатуры glTF)");

            var stepsRoot = JsonAccess.Obj(Parse(Text(entries, "steps.json", required: true), "steps.json"), "steps.json");
            var parts = ParseParts(stepsRoot);
            var steps = ParseSteps(stepsRoot);

            var hints = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            var media = new Dictionary<string, byte[]>();
            foreach (var kv in entries)
            {
                if (kv.Key.StartsWith("hints/", StringComparison.Ordinal) && kv.Key.EndsWith(".json", StringComparison.Ordinal))
                {
                    string lang = kv.Key.Substring(6, kv.Key.Length - 11);
                    var table = new Dictionary<string, string>();
                    var obj = JsonAccess.Obj(Parse(Decode(kv.Value), kv.Key), kv.Key);
                    foreach (var h in obj)
                        if (h.Value is string s) table[h.Key] = s;
                    hints[lang] = table;
                }
                else if (kv.Key.StartsWith("media/", StringComparison.Ordinal))
                {
                    media[kv.Key] = kv.Value;
                }
            }

            var pkg = new StarkPackage
            {
                Manifest = manifest,
                Parts = parts,
                Steps = steps,
                Hints = hints,
                ModelGlb = glb,
                Media = media,
            };
            pkg.Index();
            Validate(pkg);
            return pkg;
        }

        /// <summary>
        /// Проверки, без которых плеер не может работать. Узлы glTF проверяет слой Presentation
        /// после загрузки модели (Core не разбирает GLB).
        /// </summary>
        static void Validate(StarkPackage pkg)
        {
            if (pkg.Steps.Count == 0) throw new StarkFormatException("В пакете нет ни одного шага");
            var seen = new HashSet<string>();
            foreach (var step in pkg.Steps)
            {
                bool any = false;
                foreach (var id in step.AllPartIds)
                {
                    any = true;
                    if (pkg.FindPart(id) == null)
                        throw new StarkFormatException($"Шаг {step.Id}: неизвестная деталь {id}");
                    if (!seen.Add(id))
                        throw new StarkFormatException($"Деталь {id} встречается в нескольких шагах");
                }
                if (!any) throw new StarkFormatException($"Шаг {step.Id} без деталей");
            }
        }

        static Dictionary<string, byte[]> ReadZip(byte[] bytes)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using (var ms = new MemoryStream(bytes, writable: false))
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue; // каталог
                    string name = e.FullName.Replace('\\', '/').TrimStart('/');
                    if (name.Contains("..")) continue; // защита от путей вне архива
                    using (var s = e.Open())
                    using (var outMs = new MemoryStream())
                    {
                        s.CopyTo(outMs);
                        result[name] = outMs.ToArray();
                    }
                }
            }
            return result;
        }

        static string Text(Dictionary<string, byte[]> entries, string name, bool required)
        {
            if (entries.TryGetValue(name, out var b)) return Decode(b);
            if (required) throw new StarkFormatException($"В пакете нет {name}");
            return null;
        }

        static string Decode(byte[] b) => new UTF8Encoding(false).GetString(b);

        static object Parse(string text, string what)
        {
            try { return MiniJson.Parse(text); }
            catch (FormatException e) { throw new StarkFormatException($"{what}: {e.Message}", e); }
        }

        static StarkManifest ParseManifest(string text)
        {
            var o = JsonAccess.Obj(Parse(text, "manifest.json"), "manifest.json");
            var m = new StarkManifest
            {
                FormatVersion = (int)o.Num("formatVersion", -1),
                ProductId = o.Str("productId"),
                ProductName = o.Str("productName"),
                Author = o.Str("author"),
                CreatedAt = o.Str("createdAt"),
                Units = o.Str("units", "m"),
                UpAxis = o.Str("upAxis", "Y"),
                Generator = o.Str("generator"),
                Languages = o.Strings("languages"),
            };
            if (o.Get("stats") is Dictionary<string, object> stats)
            {
                m.StatsParts = (int)stats.Num("parts");
                m.StatsSteps = (int)stats.Num("steps");
                m.StatsTriangles = (int)stats.Num("triangles");
            }
            if (m.Units != "m") throw new StarkFormatException($"Единицы модели '{m.Units}', ожидались метры");
            if (m.UpAxis != "Y") throw new StarkFormatException($"Ось вверх '{m.UpAxis}', ожидалась Y");
            return m;
        }

        static List<PartInfo> ParseParts(Dictionary<string, object> root)
        {
            var list = new List<PartInfo>();
            foreach (var item in root.ArrOrEmpty("parts"))
            {
                var o = JsonAccess.Obj(item, "parts[]");
                var p = new PartInfo
                {
                    Id = o.Str("id"),
                    Name = o.Str("name"),
                    Qty = (int)o.Num("qty", 1),
                    Tags = o.Strings("tags"),
                };
                if (string.IsNullOrEmpty(p.Id)) throw new StarkFormatException("Деталь без id");
                list.Add(p);
            }
            return list;
        }

        static List<AssemblyStep> ParseSteps(Dictionary<string, object> root)
        {
            var list = new List<AssemblyStep>();
            int index = 0;
            foreach (var item in root.ArrOrEmpty("steps"))
            {
                var o = JsonAccess.Obj(item, "steps[]");
                var s = new AssemblyStep
                {
                    Index = index++,
                    Id = o.Str("id"),
                    Title = o.Str("title"),
                    Parts = o.Strings("parts"),
                    InsertPath = ParsePath(o.Get("insertPath")) ?? new List<Vector3>(),
                    PartPaths = ParsePathMap(o.Get("partPaths")),
                    Tool = o.Str("tool"),
                    HintKey = o.Str("hintKey"),
                    Checks = o.Strings("checks"),
                    Review = ReviewText(o.Get("review")),
                };
                if (o.Get("camera") is Dictionary<string, object> cam)
                {
                    s.CameraFocus = cam.Str("focus");
                    s.CameraExplode = (float)cam.Num("explode");
                }
                foreach (var fItem in o.ArrOrEmpty("fasteners"))
                {
                    var fo = JsonAccess.Obj(fItem, $"steps[{s.Id}].fasteners[]");
                    var ids = fo.Strings("partIds");
                    // Черновик формата: одиночный partId
                    if (ids.Count == 0 && fo.Get("partId") is string single) ids.Add(single);
                    s.Fasteners.Add(new FastenerGroup
                    {
                        PartIds = ids,
                        Name = fo.Str("name"),
                        Qty = (int)fo.Num("qty", ids.Count),
                        TorqueNm = fo.NumOrNull("torqueNm"),
                        PartPaths = ParsePathMap(fo.Get("partPaths")),
                    });
                }
                list.Add(s);
            }
            return list;
        }

        static string ReviewText(object review)
        {
            switch (review)
            {
                case null: return null;
                case string s: return s;
                case Dictionary<string, object> d:
                    return d.Str("reason", d.Str("message", "Требует проверки"));
                default: return "Требует проверки";
            }
        }

        static Dictionary<string, List<Vector3>> ParsePathMap(object o)
        {
            var map = new Dictionary<string, List<Vector3>>();
            if (o is Dictionary<string, object> d)
                foreach (var kv in d)
                    map[kv.Key] = ParsePath(kv.Value);
            return map;
        }

        static List<Vector3> ParsePath(object o)
        {
            if (!(o is List<object> pts)) return null;
            var path = new List<Vector3>(pts.Count);
            foreach (var pt in pts)
            {
                if (pt is List<object> c && c.Count >= 3 && c[0] is double x && c[1] is double y && c[2] is double z)
                    path.Add(new Vector3((float)x, (float)y, (float)z));
                else
                    throw new StarkFormatException("Точка пути должна быть [x, y, z]");
            }
            return path;
        }
    }
}
