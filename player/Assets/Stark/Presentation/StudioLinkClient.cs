using System;
using System.Collections;
using System.IO;
using Stark.Core.Link;
using UnityEngine;
using UnityEngine.Networking;

namespace Stark.Presentation
{
    /// <summary>
    /// Связь со Stark Studio по Wi‑Fi. Инженер сканирует QR-код из Studio камерой Beam Pro,
    /// страница Studio открывает ссылку stark://link?u=…&amp;t=… — Android запускает плеер с ней.
    /// Плеер запоминает Studio и раз в 3 секунды спрашивает, что отправлено «На очки».
    /// Новая версия скачивается, проверяется по SHA-256, сохраняется в папку Packages
    /// (работает и без сети при следующем запуске) и открывается на том же месте стола и шаге.
    /// </summary>
    public sealed class StudioLinkClient : MonoBehaviour
    {
        public StarkPlayer player;
        [Tooltip("Только редактор: вставьте адрес со страницы Studio (http://IP:8041/s/…) или ссылку stark://link?…")]
        public string editorPairingLink = "";
        [Tooltip("Открывать новую версию сразу, как только Studio её отправила.")]
        public bool autoLoad = true;

        const string PrefUrl = "stark.link.url";
        const string PrefToken = "stark.link.token";

        StudioPairing _pairing;
        int _failures;
        bool _wake;
        bool _downloading;
        string _lastStatus;

        public StudioPairing Pairing => _pairing;
        public RemotePackage LastRemote { get; private set; }
        public bool IsConnected => _pairing != null && _failures == 0 && _everReached;
        bool _everReached;

        void Awake()
        {
            if (player == null) player = GetComponent<StarkPlayer>();
            Application.deepLinkActivated += OnDeepLink;
        }

        void OnDestroy() => Application.deepLinkActivated -= OnDeepLink;

        void Start()
        {
            Restore();
            // Холодный старт по ссылке из QR: Android передаёт её в absoluteURL.
            if (!string.IsNullOrEmpty(Application.absoluteURL)) OnDeepLink(Application.absoluteURL);
            if (Application.isEditor && !string.IsNullOrEmpty(editorPairingLink)) Pair(editorPairingLink);
            StartCoroutine(PollLoop());
        }

        void OnDeepLink(string url)
        {
            Debug.Log($"[Stark] Ссылка: {Redact(url)}");
            Pair(url);
        }

        /// <summary>Подключиться к Studio по ссылке из QR. false — ссылка не от Studio.</summary>
        public bool Pair(string link)
        {
            if (!StudioLink.TryParse(link, out var p))
            {
                Status("Это не ссылка Stark Studio. Отсканируйте QR-код в окне «QR» в Studio");
                return false;
            }
            _pairing = p;
            _failures = 0;
            _everReached = false;
            PlayerPrefs.SetString(PrefUrl, p.BaseUrl);
            PlayerPrefs.SetString(PrefToken, p.Token);
            PlayerPrefs.Save();
            Status($"Подключаюсь к Stark Studio ({p.Host})…");
            _wake = true;
            return true;
        }

        [ContextMenu("Подключить по ссылке из поля")]
        void PairFromField() => Pair(editorPairingLink);

        [ContextMenu("Забыть Studio")]
        public void Unpair()
        {
            _pairing = null;
            PlayerPrefs.DeleteKey(PrefUrl);
            PlayerPrefs.DeleteKey(PrefToken);
            PlayerPrefs.Save();
            Status("Studio забыта. Для подключения отсканируйте QR-код заново");
        }

        void Restore()
        {
            var url = PlayerPrefs.GetString(PrefUrl, "");
            var token = PlayerPrefs.GetString(PrefToken, "");
            if (url.Length > 0 && token.Length > 0 &&
                StudioLink.TryParse($"{StudioLink.Scheme}://link?u={Uri.EscapeDataString(url)}&t={Uri.EscapeDataString(token)}", out var p))
                _pairing = p;
        }

        // ───────────────────────── опрос ─────────────────────────

        IEnumerator PollLoop()
        {
            while (true)
            {
                if (_pairing != null && !_downloading) yield return PollOnce();
                float delay = _pairing == null ? 1f : StudioLink.PollDelaySeconds(_failures);
                for (float t = 0; t < delay && !_wake; t += Time.unscaledDeltaTime) yield return null;
                _wake = false;
            }
        }

        // Пока приложение свёрнуто, опрашивать незачем; при возврате — сразу проверить.
        void OnApplicationPause(bool paused)
        {
            if (!paused) _wake = true;
        }

        IEnumerator PollOnce()
        {
            var p = _pairing;
            RemotePackage remote;
            using (var req = UnityWebRequest.Get(p.CurrentUrl))
            {
                AddHeaders(req, p);
                req.timeout = 5;
                yield return req.SendWebRequest();
                if (p != _pairing) yield break; // за время запроса отсканировали другой QR

                if (req.responseCode == 403)
                {
                    Fail("Studio не узнаёт эти очки (новый QR-код?). Отсканируйте QR-код в Studio заново");
                    yield break;
                }
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Fail($"Нет связи со Studio ({p.Host}). Компьютер включён и в той же Wi‑Fi сети?");
                    yield break;
                }
                try
                {
                    remote = StudioLink.ParseCurrent(req.downloadHandler.text);
                }
                catch (FormatException e)
                {
                    Fail(e.Message);
                    yield break;
                }
            }

            bool firstContact = !_everReached || _failures > 0;
            _failures = 0;
            _everReached = true;
            LastRemote = remote;

            if (remote == null)
            {
                if (firstContact) Status("Studio подключена. Нажмите «На очки» в Studio");
                yield break;
            }
            if (!StudioLink.IsNew(remote, player != null ? player.LoadedSha256 : null))
            {
                if (firstContact) Status($"Studio подключена: «{remote.Name}», версия {remote.Version}");
                yield break;
            }
            if (!autoLoad || player == null || player.CurrentMode == StarkPlayer.Mode.Loading) yield break;
            yield return Download(p, remote);
        }

        IEnumerator Download(StudioPairing p, RemotePackage remote)
        {
            _downloading = true;
            try
            {
                Status($"Загружаю из Studio «{remote.Name}», версия {remote.Version}…");
                byte[] data;
                using (var req = UnityWebRequest.Get(p.PackageUrl))
                {
                    AddHeaders(req, p);
                    req.timeout = 120;
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Fail($"Не удалось скачать пакет: {req.error}");
                        yield break;
                    }
                    data = req.downloadHandler.data;
                }
                try
                {
                    StudioLink.Verify(remote, data);
                }
                catch (FormatException e)
                {
                    Fail(e.Message); // обычно инженер успел отправить ещё раз: следующий опрос возьмёт новую
                    yield break;
                }

                string path = null;
                try
                {
                    path = SaveToPackages(remote, data);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Stark] Не удалось сохранить пакет из Studio: {e.Message}");
                }

                var task = player.LoadPackageAsync(data, keepPlacement: true);
                while (!task.IsCompleted) yield return null;
                if (task.IsFaulted)
                {
                    var msg = task.Exception?.GetBaseException().Message ?? "неизвестная ошибка";
                    Debug.LogException(task.Exception);
                    if (player.CurrentMode == StarkPlayer.Mode.Error) player.ShowError("Пакет из Studio не открылся", msg);
                    else Status($"Пакет из Studio не открылся: {msg}");
                    yield break;
                }
                if (path != null) player.SelectPackageFile(path);
                Status($"Обновлено из Studio: «{remote.Name}», версия {remote.Version}");
            }
            finally
            {
                _downloading = false;
            }
        }

        static string SaveToPackages(RemotePackage remote, byte[] data)
        {
            Directory.CreateDirectory(PackageSource.UserFolder);
            var path = Path.Combine(PackageSource.UserFolder, StudioLink.FileNameFor(remote));
            var tmp = path + ".part";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            return path;
        }

        static void AddHeaders(UnityWebRequest req, StudioPairing p)
        {
            req.SetRequestHeader(StudioLink.TokenHeader, p.Token);
            req.SetRequestHeader(StudioLink.DeviceHeader, SystemInfo.deviceModel);
        }

        void Fail(string message)
        {
            _failures++;
            // не мигать одним и тем же сообщением на каждом опросе
            if (_failures == 1 || message != _lastStatus) Status(message);
        }

        void Status(string message)
        {
            _lastStatus = message;
            Debug.Log($"[Stark] Studio: {message}");
            if (player != null) player.ShowStatus(message);
        }

        static string Redact(string url)
        {
            int t = url.IndexOf("t=", StringComparison.Ordinal);
            return t < 0 ? url : url.Substring(0, t + 2) + "…";
        }
    }
}
