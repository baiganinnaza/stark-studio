using System.IO;
using Stark.Adapters.XR;
using Stark.App;
using Stark.Presentation;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;

namespace Stark.EditorTools
{
    /// <summary>
    /// Сцена плеера собирается кодом, а не хранится в YAML: так её легко пересоздать после
    /// обновления SDK и не надо мержить .unity файлы. Меню: Stark → Create Player Scene.
    /// </summary>
    public static class StarkSceneBuilder
    {
        const string ScenePath = "Assets/Scenes/StarkPlayer.unity";
        const string GeneratedDir = "Assets/Stark/Generated";
        const string StreamingDir = "Assets/StreamingAssets/" + PackageSource.StreamingFolder;
        const string DemoPackage = "gearbox_module.stark";

        [MenuItem("Stark/Create Player Scene", priority = 1)]
        public static void CreatePlayerScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(GeneratedDir);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            CopyDemoPackage();
            EnsureUrp(); // до материалов: от пайплайна зависит выбор шейдеров

            var mats = CreateMaterials();
            var planePrefab = CreatePlanePrefab(mats.ghost);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // ── XR: AR Session + XR Origin (камера, плоскости, raycast, якоря) ──
            var xrRoot = new GameObject("XR");
            var rig = xrRoot.AddComponent<XrRig>();

            var sessionGo = new GameObject("AR Session");
            sessionGo.transform.SetParent(xrRoot.transform, false);
            sessionGo.AddComponent<ARSession>();

            var originGo = new GameObject("XR Origin");
            originGo.transform.SetParent(xrRoot.transform, false);
            var origin = originGo.AddComponent<XROrigin>();
            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(originGo.transform, false);
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(offset.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black; // оптические очки: чёрный = прозрачный
            cam.nearClipPlane = 0.05f;
            camGo.AddComponent<AudioListener>();
            AddTrackedPoseDriver(camGo);
            origin.Camera = cam;
            origin.CameraFloorOffsetObject = offset;

            var raycast = originGo.AddComponent<ARRaycastManager>();
            var planes = originGo.AddComponent<ARPlaneManager>();
            planes.planePrefab = planePrefab;
            planes.requestedDetectionMode = UnityEngine.XR.ARSubsystems.PlaneDetectionMode.Horizontal;
            var anchors = originGo.AddComponent<ARAnchorManager>();

            rig.origin = origin;
            rig.xrCamera = cam;
            rig.raycastManager = raycast;
            rig.planeManager = planes;
            rig.anchorManager = anchors;

            // ── Stark: загрузчик + плеер ──
            var starkGo = new GameObject("Stark");
            var player = starkGo.AddComponent<StarkPlayer>();
            player.materials = mats;
            var boot = starkGo.AddComponent<StarkBootstrap>();
            boot.player = player;
            boot.xrRig = rig;

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuild(ScenePath);
            Debug.Log("[Stark] Сцена создана: " + ScenePath + ". Нажмите Play: без XR запустится симулятор.");
        }

        /// <summary>
        /// Положение и поворот камеры от XR-провайдера. Две привязки: шлем (XREAL отдаёт очки как XRHMD)
        /// и мобильное AR-устройство (на случай планшета с ARCore).
        /// </summary>
        static void AddTrackedPoseDriver(GameObject camGo)
        {
            var tpd = camGo.AddComponent<TrackedPoseDriver>();
            var pos = new InputAction("Position", InputActionType.Value, expectedControlType: "Vector3");
            pos.AddBinding("<XRHMD>/centerEyePosition");
            pos.AddBinding("<HandheldARInputDevice>/devicePosition");
            var rot = new InputAction("Rotation", InputActionType.Value, expectedControlType: "Quaternion");
            rot.AddBinding("<XRHMD>/centerEyeRotation");
            rot.AddBinding("<HandheldARInputDevice>/deviceRotation");
            tpd.positionInput = new InputActionProperty(pos);
            tpd.rotationInput = new InputActionProperty(rot);
            tpd.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        }

        static StarkMaterials CreateMaterials()
        {
            var m = new StarkMaterials
            {
                current = SaveMaterial(StarkMaterials.CreateOpaque("Stark Current", StarkMaterials.CurrentColor), "Current"),
                fastener = SaveMaterial(StarkMaterials.CreateOpaque("Stark Fastener", StarkMaterials.FastenerColor), "Fastener"),
                ghost = SaveMaterial(StarkMaterials.CreateTransparent("Stark Ghost", StarkMaterials.GhostColor), "Ghost"),
                ui = SaveMaterial(StarkMaterials.CreateOpaque("Stark UI", StarkMaterials.ButtonColor), "UI"),
            };
            return m;
        }

        static Material SaveMaterial(Material m, string name)
        {
            string path = $"{GeneratedDir}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
            {
                existing.shader = m.shader;
                existing.CopyPropertiesFromMaterial(m);
                existing.renderQueue = m.renderQueue;
                EditorUtility.SetDirty(existing);
                return existing;
            }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static GameObject CreatePlanePrefab(Material material)
        {
            string path = $"{GeneratedDir}/AR Plane.prefab";
            var go = new GameObject("AR Plane");
            go.AddComponent<ARPlane>();
            go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            go.AddComponent<ARPlaneMeshVisualizer>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        /// <summary>
        /// URP по плану (Forward, без теней, MSAA 2x). Если пайплайн уже назначен, не трогаем.
        /// </summary>
        [MenuItem("Stark/Setup URP", priority = 3)]
        public static void EnsureUrp()
        {
            if (GraphicsSettings.defaultRenderPipeline != null) return;
            try
            {
                var data = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(data, $"{GeneratedDir}/Stark URP Renderer.asset");
                var asset = UniversalRenderPipelineAsset.Create(data);
                asset.msaaSampleCount = 2;
                asset.supportsHDR = false;
                asset.shadowDistance = 0;
                AssetDatabase.CreateAsset(asset, $"{GeneratedDir}/Stark URP.asset");
                GraphicsSettings.defaultRenderPipeline = asset;
                int current = QualitySettings.GetQualityLevel();
                for (int i = 0; i < QualitySettings.names.Length; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    QualitySettings.renderPipeline = asset;
                }
                QualitySettings.SetQualityLevel(current, false);
                AssetDatabase.SaveAssets();
                Debug.Log("[Stark] Назначен URP: " + GeneratedDir + "/Stark URP.asset");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[Stark] URP не настроен автоматически (" + e.Message + "). " +
                                 "Плеер работает и на встроенном рендере; URP можно назначить вручную в Project Settings → Graphics.");
            }
        }

        [MenuItem("Stark/Copy Demo Package to StreamingAssets", priority = 2)]
        public static void CopyDemoPackage()
        {
            // player/ лежит в корне монорепо рядом с samples/
            string src = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "samples", DemoPackage));
            if (!File.Exists(src))
            {
                Debug.LogWarning("[Stark] Не найден " + src);
                return;
            }
            Directory.CreateDirectory(StreamingDir);
            File.Copy(src, Path.Combine(StreamingDir, DemoPackage), overwrite: true);
            AssetDatabase.Refresh();
        }

        static void AddSceneToBuild(string path)
        {
            var scenes = EditorBuildSettings.scenes;
            foreach (var s in scenes)
                if (s.path == path) return;
            var list = new System.Collections.Generic.List<EditorBuildSettingsScene> { new EditorBuildSettingsScene(path, true) };
            list.AddRange(scenes);
            EditorBuildSettings.scenes = list.ToArray();
        }

        /// <summary>
        /// Базовые настройки Android для Beam Pro по требованиям XREAL SDK 3.x.
        /// Окончательную проверку делает Project Validation самого XREAL SDK
        /// (Project Settings → XR Plug-in Management → Project Validation).
        /// </summary>
        [MenuItem("Stark/Configure Android Settings for XREAL", priority = 20)]
        public static void ConfigureAndroid()
        {
            var android = NamedBuildTarget.Android;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            if (PlayerSettings.GetApplicationIdentifier(android).StartsWith("com.DefaultCompany"))
                PlayerSettings.SetApplicationIdentifier(android, "kz.stark.player");
            PlayerSettings.productName = "Stark Player";
            Debug.Log("[Stark] Android: minSdk 29, IL2CPP, ARM64, OpenGLES3, landscape. " +
                      "Дальше: XR Plug-in Management → Android → XREAL, затем Project Validation → Fix All.");
        }
    }
}
