using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Stark.Adapters.Simulator;
using Stark.Core.Gestures;
using Stark.Core.Steps;
using Stark.Device;
using Stark.Presentation;
using UnityEngine;
using UnityEngine.TestTools;
using NVector3 = System.Numerics.Vector3;

namespace Stark.PlayMode.Tests
{
    /// <summary>
    /// Проверка «без очков»: симулятор + настоящий StarkPlayer проходят все шаги пакета,
    /// на каждом шаге сохраняется скриншот (player/TestScreenshots/&lt;пакет&gt;/NN.png).
    /// </summary>
    public class PlayerWalkthroughTests
    {
        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static string ScreenshotRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "TestScreenshots"));

        /// <summary>Демо-редуктор из samples/ и все пакеты из samples/open (планетарный редуктор и т.д.).</summary>
        public static IEnumerable<string> Packages()
        {
            var list = new List<string>();
            var demo = Path.Combine(RepoRoot, "samples", "gearbox_module.stark");
            if (File.Exists(demo)) list.Add(demo);
            var open = Path.Combine(RepoRoot, "samples", "open");
            if (Directory.Exists(open)) list.AddRange(Directory.GetFiles(open, "*.stark"));
            return list;
        }

        readonly List<GameObject> _spawned = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _spawned) if (go != null) Object.Destroy(go);
            _spawned.Clear();
            foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                if (go != null && go.parent == null && go.name.StartsWith("Simulated")) Object.Destroy(go.gameObject);
        }

        (SimulatorRig rig, StarkPlayer player, DeviceServices services) Spawn(string packagePath, IHandInput hands = null)
        {
            var rigGo = new GameObject("Simulator Rig");
            _spawned.Add(rigGo);
            var rig = rigGo.AddComponent<SimulatorRig>();
            var services = rig.Build();
            if (hands != null) services.Hands = hands;

            var playerGo = new GameObject("Stark Player");
            _spawned.Add(playerGo);
            var player = playerGo.AddComponent<StarkPlayer>();
            player.editorPackagePath = packagePath;
            player.bundledPackages = new string[0];
            player.Initialize(services);
            return (rig, player, services);
        }

        static IEnumerator WaitFor(System.Func<bool> condition, float timeout, string what)
        {
            float t = 0;
            while (!condition())
            {
                t += Time.unscaledDeltaTime;
                if (t > timeout) Assert.Fail("Не дождались: " + what);
                yield return null;
            }
        }

        static Pose TableCenter(SimulatorRig rig, DeviceServices services)
        {
            var target = rig.transform.TransformPoint(new Vector3(0, rig.tableHeight, rig.tableDistance));
            var cam = services.Camera.transform.position;
            Assert.IsTrue(services.Placement.TryGetSurfacePose(new Ray(cam, target - cam), out var pose), "луч не попал в стол");
            return pose;
        }

        [UnityTest]
        public IEnumerator WalksAllStepsAndSavesScreenshots([ValueSource(nameof(Packages))] string packagePath)
        {
            var (rig, player, services) = Spawn(packagePath);
            yield return WaitFor(() => player.CurrentMode == StarkPlayer.Mode.Placing || player.CurrentMode == StarkPlayer.Mode.Error, 60f, "загрузка пакета");
            Assert.AreEqual(StarkPlayer.Mode.Placing, player.CurrentMode, "пакет не загрузился");
            Assert.IsEmpty(player.Model.MissingNodes, "в model.glb нет узлов для деталей");
            Assert.AreEqual(player.Package.Parts.Count, player.Model.Parts.Count);

            string dir = Path.Combine(ScreenshotRoot, Path.GetFileNameWithoutExtension(packagePath));
            Directory.CreateDirectory(dir);
            StartRecording(services.Camera, Path.GetFileNameWithoutExtension(packagePath));
            yield return new WaitForSeconds(1.0f);
            Capture(services.Camera, Path.Combine(dir, "00_placing.png"));

            Assert.IsTrue(player.PlaceAt(TableCenter(rig, services)), "модель не встала на стол");
            yield return new WaitForSeconds(0.8f);
            yield return FlyTo(services.Camera, player, 1.2f);

            int count = player.Steps.Count;
            for (int i = 0; i < count; i++)
            {
                Assert.AreEqual(i, player.Steps.CurrentIndex);
                var step = player.Steps.Current;
                foreach (var id in step.Parts)
                    Assert.AreEqual(PartState.Current, player.Model.Parts[id].State, $"шаг {i + 1}: {id}");
                foreach (var f in step.Fasteners)
                foreach (var id in f.PartIds)
                    Assert.AreEqual(PartState.CurrentFastener, player.Model.Parts[id].State, $"шаг {i + 1}: {id}");

                // Середина анимации установки: деталь видна на пути. Для видео держим шаг 2.5 с.
                yield return new WaitForSeconds(0.7f);
                Capture(services.Camera, Path.Combine(dir, $"{i + 1:00}_step.png"));
                yield return new WaitForSeconds(1.8f);
                player.Next("test");
                yield return null;
            }

            Assert.IsTrue(player.Steps.IsFinished);
            foreach (var pv in player.Model.Parts.Values) Assert.AreEqual(PartState.Done, pv.State);
            yield return new WaitForSeconds(0.2f);
            Capture(services.Camera, Path.Combine(dir, $"{count + 1:00}_finished.png"));

            // Разлёт на готовой сборке.
            player.Prev("test");
            player.ToggleExplode();
            yield return new WaitForSeconds(1.0f);
            Capture(services.Camera, Path.Combine(dir, $"{count + 2:00}_explode.png"));
            yield return new WaitForSeconds(1.5f);
            Debug.Log($"[Stark test] {Path.GetFileName(packagePath)}: {count} шагов, скриншоты в {dir}");
        }

        /// <summary>
        /// Жесты по-настоящему: кадры рук идут через GestureRecognizer в StarkPlayer.
        /// Щипок ставит модель, смахивание влево листает вперёд, вправо назад.
        /// </summary>
        [UnityTest]
        public IEnumerator PinchPlacesAndFlickTurnsSteps()
        {
            var hands = new ScriptedHands();
            var demo = Path.Combine(RepoRoot, "samples", "gearbox_module.stark");
            var (rig, player, services) = Spawn(demo, hands);
            yield return WaitFor(() => player.CurrentMode == StarkPlayer.Mode.Placing, 60f, "загрузка пакета");

            // Взгляд на стол: в тесте мыши нет, поэтому направляем камеру на центр стола.
            var target = rig.transform.TransformPoint(new Vector3(0, rig.tableHeight, rig.tableDistance));
            services.Camera.transform.LookAt(target);
            yield return null;
            yield return null;

            StartRecording(services.Camera, "gestures");
            var hand = services.Camera.transform.position + services.Camera.transform.forward * 0.5f;
            yield return hands.Pinch(hand, Vector3.zero, 0.15f); // короткий щипок = Tap
            Assert.AreEqual(StarkPlayer.Mode.Assembling, player.CurrentMode, "щипок не поставил модель");
            Assert.AreEqual(0, player.Steps.CurrentIndex);

            var left = -services.Camera.transform.right * 0.15f;
            yield return hands.Pinch(hand, left, 0.25f);
            Assert.AreEqual(1, player.Steps.CurrentIndex, "смахивание влево = следующий шаг");

            yield return hands.Pinch(hand, -left, 0.25f);
            Assert.AreEqual(0, player.Steps.CurrentIndex, "смахивание вправо = предыдущий шаг");

            // Нажатие кнопки «Далее» пальцем сверху.
            var next = GameObject.Find("Button Next");
            Assert.IsNotNull(next);
            yield return hands.Poke(next.transform.position);
            Assert.AreEqual(1, player.Steps.CurrentIndex, "поки-кнопка «Далее»");
        }

        /// <summary>Плавно подвести камеру к модели (как сборщик садится за стол).</summary>
        static IEnumerator FlyTo(Camera cam, StarkPlayer player, float seconds)
        {
            var fromPos = cam.transform.position;
            var fromRot = cam.transform.rotation;
            FrameModel(cam, player);
            var toPos = cam.transform.position;
            var toRot = cam.transform.rotation;
            for (float t = 0; t < seconds; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0, 1, t / seconds);
                cam.transform.SetPositionAndRotation(Vector3.Lerp(fromPos, toPos, k), Quaternion.Slerp(fromRot, toRot, k));
                yield return null;
            }
            cam.transform.SetPositionAndRotation(toPos, toRot);
        }

        static void FrameModel(Camera cam, StarkPlayer player)
        {
            var center = player.Model.transform.TransformPoint(player.Model.LocalBounds.center);
            float r = Mathf.Max(0.05f, player.Model.Radius * player.ModelScale);
            // Сверху-спереди, как видит сборщик, сидящий за столом; в кадре модель, кнопки и подсказка.
            var back = (cam.transform.position - center);
            back.y = 0;
            back = back.sqrMagnitude > 1e-4f ? back.normalized : Vector3.back;
            float dist = r * 4.5f + 0.25f;
            cam.transform.position = center + back * dist + Vector3.up * dist * 0.8f;
            cam.transform.LookAt(center + Vector3.up * r * 0.5f);
        }

        static void Capture(Camera cam, string path, int w = 1280, int h = 720)
        {
            var rt = new RenderTexture(w, h, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            var old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            cam.targetTexture = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex);
            rt.Release();
            Object.Destroy(rt);
        }

        /// <summary>
        /// Запись видео: каждые 1/15 с кадр с камеры в frames/NNNNN.png.
        /// В CI ffmpeg собирает их в mp4 (player/TestScreenshots/&lt;пакет&gt;.mp4).
        /// </summary>
        sealed class FrameRecorder : MonoBehaviour
        {
            public Camera cam;
            public string dir;
            public float interval = 1f / 15f;
            int _n;
            float _next;

            void LateUpdate()
            {
                if (cam == null || Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + interval;
                Capture(cam, Path.Combine(dir, $"{_n++:00000}.png"), 960, 540);
            }
        }

        FrameRecorder StartRecording(Camera cam, string name)
        {
            string dir = Path.Combine(ScreenshotRoot, "video", name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var go = new GameObject("Frame Recorder");
            _spawned.Add(go);
            var r = go.AddComponent<FrameRecorder>();
            r.cam = cam;
            r.dir = dir;
            return r;
        }

        /// <summary>Руки по сценарию теста.</summary>
        sealed class ScriptedHands : IHandInput
        {
            HandFrame _right = HandFrame.NotTracked;

            public bool IsAvailable => true;
            public HandFrame GetFrame(Hand hand) => hand == Hand.Right ? _right : HandFrame.NotTracked;

            static HandFrame Frame(Vector3 at, float gap) => new HandFrame
            {
                Tracked = true,
                ThumbTip = new NVector3(at.x, at.y - gap / 2, at.z),
                IndexTip = new NVector3(at.x, at.y + gap / 2, at.z),
                Palm = new NVector3(at.x, at.y - 0.05f, at.z),
            };

            /// <summary>Щипок в start, движение на move за duration секунд, отпускание.</summary>
            public IEnumerator Pinch(Vector3 start, Vector3 move, float duration)
            {
                _right = Frame(start, 0.06f);
                yield return null;
                float t = 0;
                while (t < duration)
                {
                    _right = Frame(start + move * (t / duration), 0.008f);
                    t += Time.deltaTime;
                    yield return null;
                }
                _right = Frame(start + move, 0.008f);
                yield return null;
                _right = Frame(start + move, 0.06f);
                yield return null;
                yield return null;
                _right = HandFrame.NotTracked;
                yield return new WaitForSeconds(0.1f);
            }

            /// <summary>Кончик указательного пальца опускается на кнопку сверху.</summary>
            public IEnumerator Poke(Vector3 button)
            {
                for (int i = 0; i < 6; i++)
                {
                    var tip = button + Vector3.up * (0.04f - i * 0.008f);
                    _right = new HandFrame { Tracked = true, IndexTip = new NVector3(tip.x, tip.y, tip.z), ThumbTip = new NVector3(tip.x - 0.05f, tip.y, tip.z) };
                    yield return null;
                }
                _right = HandFrame.NotTracked;
                yield return new WaitForSeconds(0.1f);
            }
        }
    }
}
