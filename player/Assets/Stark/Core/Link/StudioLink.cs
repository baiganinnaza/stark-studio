using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Stark.Core.Json;

namespace Stark.Core.Link
{
    /// <summary>Адрес Studio и токен доступа, полученные из QR-кода (stark://link?u=…&amp;t=…).</summary>
    public sealed class StudioPairing
    {
        public readonly string BaseUrl; // http://192.168.1.10:8041, без «/» в конце
        public readonly string Token;

        public StudioPairing(string baseUrl, string token)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            Token = token;
        }

        public string CurrentUrl => BaseUrl + "/api/v1/current";
        public string PackageUrl => BaseUrl + "/api/v1/current.stark";
        public string Host => new Uri(BaseUrl).Authority;
    }

    /// <summary>Что Studio сейчас отправила на очки (ответ GET /api/v1/current).</summary>
    public sealed class RemotePackage
    {
        public string Id;
        public string Name;
        public int Version;
        public string Sha256;
        public long Size;
        public int Steps;
        public string SentAt;
    }

    /// <summary>
    /// Протокол «Studio → очки» без Unity (spec/stark-link-v1.md). Сеть и хранение делает слой
    /// Presentation (UnityWebRequest, PlayerPrefs), здесь только разбор и решения, чтобы их можно было тестировать.
    /// </summary>
    public static class StudioLink
    {
        public const int Protocol = 1;
        public const string Scheme = "stark";
        public const string TokenHeader = "X-Stark-Token";
        public const string DeviceHeader = "X-Stark-Device";
        public const long MaxPackageBytes = 300L * 1024 * 1024;

        /// <summary>
        /// Разобрать ссылку из QR / со страницы Studio. Принимает stark://link?u=&lt;base&gt;&amp;t=&lt;token&gt;
        /// и, для ручного ввода, http://host:port/s/&lt;token&gt;.
        /// </summary>
        public static bool TryParse(string link, out StudioPairing pairing)
        {
            pairing = null;
            if (string.IsNullOrWhiteSpace(link)) return false;
            link = link.Trim();
            Uri uri;
            try { uri = new Uri(link); }
            catch (UriFormatException) { return false; }

            string baseUrl, token;
            if (uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
            {
                // Uri не всегда считает «link» хостом у нестандартной схемы, поэтому query берём из строки.
                int q = link.IndexOf('?');
                if (q < 0) return false;
                var head = link.Substring(0, q).TrimEnd('/');
                if (!head.Equals(Scheme + "://link", StringComparison.OrdinalIgnoreCase)) return false;
                var args = ParseQuery(link.Substring(q + 1));
                if (!args.TryGetValue("u", out baseUrl) || !args.TryGetValue("t", out token)) return false;
            }
            else if (IsHttp(uri) && uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal))
            {
                baseUrl = uri.GetLeftPart(UriPartial.Authority);
                token = Uri.UnescapeDataString(uri.AbsolutePath.Substring(3).TrimEnd('/'));
            }
            else return false;

            if (!ValidToken(token)) return false;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) || !IsHttp(b) || b.AbsolutePath.Trim('/').Length > 0)
                return false;
            pairing = new StudioPairing(b.GetLeftPart(UriPartial.Authority), token);
            return true;
        }

        static bool IsHttp(Uri u) => u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;

        static bool ValidToken(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Length < 16 || t.Length > 128) return false;
            foreach (char c in t)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_') return false;
            return true;
        }

        static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in q.Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return d;
        }

        /// <summary>Разобрать ответ /api/v1/current. null = Studio ещё ничего не отправила.</summary>
        public static RemotePackage ParseCurrent(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root))
                throw new FormatException("Ответ Studio не JSON-объект");
            if (root.TryGetValue("protocol", out var p) && p is double pv && (int)pv > Protocol)
                throw new FormatException($"Studio использует протокол v{(int)pv}, плеер понимает v{Protocol}. Обновите Stark Player.");
            if (!root.TryGetValue("package", out var pkgObj) || pkgObj == null) return null;
            if (!(pkgObj is Dictionary<string, object> pkg)) throw new FormatException("package должен быть объектом");

            var r = new RemotePackage
            {
                Id = Str(pkg, "id"),
                Name = Str(pkg, "name"),
                Version = (int)Num(pkg, "version"),
                Sha256 = Str(pkg, "sha256")?.ToLowerInvariant(),
                Size = (long)Num(pkg, "size"),
                Steps = (int)Num(pkg, "steps"),
                SentAt = Str(pkg, "sentAt"),
            };
            if (string.IsNullOrEmpty(r.Id) || string.IsNullOrEmpty(r.Sha256) || r.Sha256.Length != 64)
                throw new FormatException("В ответе Studio нет id или sha256 пакета");
            if (r.Size <= 0 || r.Size > MaxPackageBytes)
                throw new FormatException($"Недопустимый размер пакета: {r.Size} байт");
            return r;
        }

        static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
        static double Num(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is double x ? x : 0;

        /// <summary>Нужно ли скачивать: другая версия, чем загружена сейчас (сравнение по содержимому).</summary>
        public static bool IsNew(RemotePackage remote, string loadedSha256) =>
            remote != null && !string.Equals(remote.Sha256, loadedSha256, StringComparison.OrdinalIgnoreCase);

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Проверить, что скачано именно то, что объявила Studio (обрыв Wi‑Fi, чужой ответ).</summary>
        public static void Verify(RemotePackage remote, byte[] data)
        {
            if (data == null || data.Length == 0) throw new FormatException("Пустой ответ Studio");
            var got = Sha256Hex(data);
            if (got != remote.Sha256)
                throw new FormatException("Пакет пришёл повреждённым (контрольная сумма не совпала), попробую ещё раз");
        }

        /// <summary>Имя файла в папке Packages: только безопасные символы из id проекта.</summary>
        public static string FileNameFor(RemotePackage remote)
        {
            var sb = new StringBuilder();
            foreach (char c in remote.Id ?? "")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            var name = sb.ToString().Trim('-');
            if (name.Length == 0) name = "studio";
            if (name.Length > 60) name = name.Substring(0, 60);
            return name + ".stark";
        }

        /// <summary>Пауза между опросами: 3 с, при ошибках сети растёт до 30 с.</summary>
        public static float PollDelaySeconds(int consecutiveFailures) =>
            consecutiveFailures <= 0 ? 3f : Math.Min(30f, 3f * (float)Math.Pow(2, Math.Min(consecutiveFailures, 4)));
    }
}
