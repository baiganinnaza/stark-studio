using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Stark.Presentation
{
    /// <summary>
    /// Где плеер ищет пакеты .stark, по порядку:
    /// 1. путь из инспектора (только редактор, удобно для разработки);
    /// 2. папка Packages в persistentDataPath, новые сверху. На Beam Pro это
    ///    Android/data/&lt;bundle id&gt;/files/Packages: копировать по USB или `adb push`;
    /// 3. пакеты, вшитые в сборку (StreamingAssets/Stark), например демо-редуктор.
    /// </summary>
    public static class PackageSource
    {
        public const string StreamingFolder = "Stark";
        public static string UserFolder => Path.Combine(Application.persistentDataPath, "Packages");

        public readonly struct Entry
        {
            public readonly string DisplayName;
            public readonly string Location; // путь к файлу или URL
            public Entry(string name, string location) { DisplayName = name; Location = location; }
            public override string ToString() => DisplayName;
        }

        public static List<Entry> List(string editorOverridePath, IEnumerable<string> bundledNames)
        {
            var result = new List<Entry>();
            if (!string.IsNullOrEmpty(editorOverridePath) && File.Exists(editorOverridePath))
                result.Add(new Entry(Path.GetFileName(editorOverridePath), editorOverridePath));

            try
            {
                Directory.CreateDirectory(UserFolder);
                var files = new List<FileInfo>(new DirectoryInfo(UserFolder).GetFiles("*.stark"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                foreach (var f in files) result.Add(new Entry(f.Name, f.FullName));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Stark] Папка пакетов недоступна: {e.Message}");
            }

            foreach (var name in bundledNames)
                if (!string.IsNullOrEmpty(name))
                    result.Add(new Entry(name + " (демо)", Path.Combine(Application.streamingAssetsPath, StreamingFolder, name)));
            return result;
        }

        /// <summary>
        /// Читает файл. На Android StreamingAssets лежат внутри APK (jar:file://…),
        /// их можно прочитать только через UnityWebRequest.
        /// </summary>
        public static async Task<byte[]> ReadAsync(Entry entry)
        {
            string loc = entry.Location;
            bool isUrl = loc.Contains("://");
            if (!isUrl && File.Exists(loc)) return File.ReadAllBytes(loc);

            string url = isUrl ? loc : "file://" + loc;
            using (var req = UnityWebRequest.Get(url))
            {
                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Yield();
                if (req.result != UnityWebRequest.Result.Success)
                    throw new IOException($"Не удалось прочитать {entry.DisplayName}: {req.error}");
                return req.downloadHandler.data;
            }
        }
    }
}
