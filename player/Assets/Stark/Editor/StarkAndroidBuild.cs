using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;
#endif

namespace Stark.EditorTools
{
    /// <summary>
    /// Настройки Android-сборки для связи со Stark Studio по Wi‑Fi:
    /// * разрешить HTTP в локальной сети (Studio на ноутбуке без сертификата) и доступ в интернет;
    /// * зарегистрировать ссылку stark://link, чтобы QR-код из Studio открывал Stark Player.
    /// Правим сгенерированный манифест, а не кладём свой AndroidManifest.xml, чтобы не спорить с XREAL SDK.
    /// </summary>
    public sealed class StarkAndroidBuild : IPreprocessBuildWithReport
#if UNITY_ANDROID
        , IPostGenerateGradleAndroidProject
#endif
    {
        public int callbackOrder => 100;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                Debug.Log("[Stark] Player Settings: разрешён HTTP (связь со Studio в локальной сети)");
            }
            PlayerSettings.Android.forceInternetPermission = true;
        }

#if UNITY_ANDROID
        const string AndroidNs = "http://schemas.android.com/apk/res/android";

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            var manifestPath = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[Stark] Не найден AndroidManifest.xml, ссылка stark:// не зарегистрирована: " + manifestPath);
                return;
            }
            var doc = new XmlDocument();
            doc.Load(manifestPath);
            if (AddDeepLink(doc)) doc.Save(manifestPath);
        }

        /// <summary>Добавить intent-filter VIEW stark://link к стартовой activity. true — манифест изменён.</summary>
        internal static bool AddDeepLink(XmlDocument doc)
        {
            var app = doc.SelectSingleNode("/manifest/application") as XmlElement;
            if (app == null) return false;
            app.SetAttribute("usesCleartextTraffic", AndroidNs, "true");

            XmlElement launcher = null;
            foreach (XmlElement act in app.GetElementsByTagName("activity"))
            {
                foreach (XmlElement cat in act.GetElementsByTagName("category"))
                    if (cat.GetAttribute("name", AndroidNs) == "android.intent.category.LAUNCHER") launcher = act;
                if (launcher != null) break;
            }
            if (launcher == null)
            {
                foreach (XmlElement act in app.GetElementsByTagName("activity"))
                    if (act.GetAttribute("name", AndroidNs).Contains("UnityPlayer")) { launcher = act; break; }
            }
            if (launcher == null)
            {
                Debug.LogWarning("[Stark] Не найдена стартовая activity: QR-код из Studio не будет открывать плеер");
                return true;
            }
            foreach (XmlElement data in launcher.GetElementsByTagName("data"))
                if (data.GetAttribute("scheme", AndroidNs) == "stark") return true; // уже есть

            // singleTask: повторное сканирование QR не создаёт вторую копию приложения
            launcher.SetAttribute("launchMode", AndroidNs, "singleTask");
            var filter = doc.CreateElement("intent-filter");
            filter.AppendChild(El(doc, "action", "android.intent.action.VIEW"));
            filter.AppendChild(El(doc, "category", "android.intent.category.DEFAULT"));
            filter.AppendChild(El(doc, "category", "android.intent.category.BROWSABLE"));
            var d = doc.CreateElement("data");
            d.SetAttribute("scheme", AndroidNs, "stark");
            d.SetAttribute("host", AndroidNs, "link");
            filter.AppendChild(d);
            launcher.AppendChild(filter);
            Debug.Log("[Stark] Зарегистрирована ссылка stark://link (подключение к Studio по QR-коду)");
            return true;
        }

        static XmlElement El(XmlDocument doc, string tag, string name)
        {
            var e = doc.CreateElement(tag);
            e.SetAttribute("name", AndroidNs, name);
            return e;
        }
#endif
    }
}
