using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Stark.Core.Tests
{
    static class TestData
    {
        /// <summary>
        /// samples/gearbox_module.stark из корня репозитория. Ищем вверх от текущего каталога
        /// и от каталога сборки: работает и в Unity (cwd = player/), и в dotnet test.
        /// </summary>
        public static string SamplePath
        {
            get
            {
                foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                {
                    var dir = new DirectoryInfo(start);
                    while (dir != null)
                    {
                        var candidate = Path.Combine(dir.FullName, "samples", "gearbox_module.stark");
                        if (File.Exists(candidate)) return candidate;
                        dir = dir.Parent;
                    }
                }
                throw new FileNotFoundException("Не найден samples/gearbox_module.stark");
            }
        }

        /// <summary>Пакет из строк: для проверки ошибок формата.</summary>
        public static byte[] MakePackage(string manifest, string steps, string hintsRu = "{}", bool withGlb = true)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    if (manifest != null) Put(zip, "manifest.json", Encoding.UTF8.GetBytes(manifest));
                    if (steps != null) Put(zip, "steps.json", Encoding.UTF8.GetBytes(steps));
                    if (hintsRu != null) Put(zip, "hints/ru.json", Encoding.UTF8.GetBytes(hintsRu));
                    if (withGlb) Put(zip, "model.glb", new byte[] { 0x67, 0x6C, 0x54, 0x46, 2, 0, 0, 0, 12, 0, 0, 0 });
                }
                return ms.ToArray();
            }
        }

        static void Put(ZipArchive zip, string name, byte[] data)
        {
            var e = zip.CreateEntry(name);
            using (var s = e.Open()) s.Write(data, 0, data.Length);
        }

        public const string Manifest = "{\"formatVersion\":1,\"productId\":\"t\",\"productName\":\"Тест\",\"units\":\"m\",\"upAxis\":\"Y\",\"languages\":[\"ru\"]}";

        public const string TwoSteps = @"{
  ""formatVersion"": 1,
  ""parts"": [
    {""id"": ""A"", ""name"": ""Основание"", ""qty"": 1, ""tags"": []},
    {""id"": ""B"", ""name"": ""Крышка"", ""qty"": 1, ""tags"": []},
    {""id"": ""F1"", ""name"": ""Болт:1"", ""qty"": 1, ""tags"": [""fastener""]},
    {""id"": ""F2"", ""name"": ""Болт:2"", ""qty"": 1, ""tags"": [""fastener""]}
  ],
  ""steps"": [
    {""id"": ""S01"", ""title"": ""Основание"", ""parts"": [""A""], ""fasteners"": [], ""hintKey"": ""H1""},
    {""id"": ""S02"", ""title"": ""Крышка"", ""parts"": [""B""],
     ""fasteners"": [{""partIds"": [""F1"", ""F2""], ""name"": ""Болт"", ""qty"": 2, ""torqueNm"": 2.5,
                      ""partPaths"": {""F1"": [[0, 0.05, 0], [0, 0, 0]]}}],
     ""insertPath"": [[0, 0.04, 0], [0, 0, 0]], ""hintKey"": ""H2"", ""tool"": ""Ключ 8""}
  ]
}";
    }
}
