using Stark.Adapters.Simulator;
using Stark.Adapters.XR;
using Stark.Device;
using Stark.Presentation;
using UnityEngine;
using UnityEngine.XR.Management;

namespace Stark.App
{
    /// <summary>
    /// Точка входа: выбирает адаптер устройства и запускает StarkPlayer.
    /// Auto: если XR-провайдер запущен (XREAL на Beam Pro, или XR Simulation в редакторе) → XR,
    /// иначе симулятор редактора (мышь + клавиатура). Единственное место, которое знает
    /// о конкретных адаптерах; новое ODM-устройство = новый адаптер + строчка здесь.
    /// </summary>
    public sealed class StarkBootstrap : MonoBehaviour
    {
        public enum DeviceMode { Auto, Simulator, XR }

        public DeviceMode mode = DeviceMode.Auto;
        public StarkPlayer player;
        public XrRig xrRig;
        public SimulatorRig simulatorRig;

        void Start()
        {
            bool xrRunning = XRGeneralSettings.Instance != null &&
                             XRGeneralSettings.Instance.Manager != null &&
                             XRGeneralSettings.Instance.Manager.activeLoader != null;
            bool useXr = mode == DeviceMode.XR || (mode == DeviceMode.Auto && xrRunning);

            if (useXr && xrRig == null)
            {
                Debug.LogError("[Stark] Нет XR Origin в сцене. Пересоздайте сцену: Stark → Create Player Scene");
                useXr = false;
            }
            if (!useXr && simulatorRig == null)
            {
                var go = new GameObject("Simulator Rig");
                simulatorRig = go.AddComponent<SimulatorRig>();
            }
            if (!useXr && !Application.isEditor)
                Debug.LogWarning("[Stark] XR не запущен на устройстве: проверьте, что очки подключены и в XR Plug-in Management выбран XREAL");

            if (xrRig != null) xrRig.gameObject.SetActive(useXr);
            if (simulatorRig != null) simulatorRig.gameObject.SetActive(!useXr);

            DeviceServices services = useXr ? xrRig.Build() : simulatorRig.Build();
            if (player == null) player = new GameObject("Stark Player").AddComponent<StarkPlayer>();
            player.Initialize(services);
        }
    }
}
