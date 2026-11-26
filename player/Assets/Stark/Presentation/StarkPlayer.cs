using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Stark.Core.Gestures;
using Stark.Core.Layout;
using Stark.Core.Package;
using Stark.Core.Session;
using Stark.Core.Steps;
using Stark.Device;
using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>
    /// Сценарий плеера: загрузить .stark → найти стол и поставить модель → листать шаги.
    /// Работает только через интерфейсы Stark.Device, поэтому одинаков на XREAL,
    /// в симуляторе редактора и (позже) на Quest/Pico/планшете.
    /// </summary>
    public sealed class StarkPlayer : MonoBehaviour
    {
        public enum Mode { Idle, Loading, Placing, Assembling, Error }

        [Header("Пакеты")]
        [Tooltip("Только редактор: путь к .stark для быстрой проверки. Пусто = папка Packages, затем демо.")]
        public string editorPackagePath = "";
        [Tooltip("Файлы в StreamingAssets/Stark, вшитые в сборку.")]
        public string[] bundledPackages = { "gearbox_module.stark" };
        public string language = "ru";

        [Header("Вид")]
        public StarkMaterials materials = new StarkMaterials();
        public bool showHandCursors = true;
        [Tooltip("Насколько дальше края модели можно начать щипок, чтобы вращать её (м).")]
        public float grabMargin = 0.25f;

        public Mode CurrentMode { get; private set; } = Mode.Idle;
        public StarkPackage Package { get; private set; }
        public StepSequencer Steps { get; private set; }
        public ModelView Model { get; private set; }
        public float ModelScale { get; private set; } = 1f;
        /// <summary>Последние распознанные жесты (для отладки и автотестов в редакторе).</summary>
        public event Action<GestureEvent> GestureRecognized;

        DeviceServices _device;
        readonly GestureRecognizer _gestures = new GestureRecognizer();
        readonly List<SystemCommand> _commands = new List<SystemCommand>();
        readonly List<PokeButton> _buttons = new List<PokeButton>();
        readonly List<TextMesh> _fastenerLabels = new List<TextMesh>();
        List<PackageSource.Entry> _entries = new List<PackageSource.Entry>();
        int _entryIndex;

        Transform _anchor;
        Transform _pivot;      // поворот вокруг вертикали + масштаб
        Transform _dock;       // кнопки на столе перед моделью
        HintPanel _hint;
        PlacementReticle _reticle;
        HandCursors _cursors;
        PokeButton _btnPrev, _btnNext, _btnExplode, _btnReplace;
        SessionLog _log;
        bool _explodeOn;
        bool _logStepStarted;
        float _defaultScale = 1f;
        float _yaw;
        Hand? _rotatingHand;
        bool _hasPose;
        Pose _candidatePose;

        Camera Cam => _device?.Camera != null ? _device.Camera : Camera.main;

        // ───────────────────────── запуск ─────────────────────────

        /// <summary>Вызывает App (StarkBootstrap) после выбора адаптера устройства.</summary>
        public void Initialize(DeviceServices device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            materials.EnsureCreated();
            _gestures.Gesture += OnGesture;

            _pivot = new GameObject("Model Pivot").transform;
            _pivot.SetParent(transform, false);
            var modelGo = new GameObject("Model");
            modelGo.transform.SetParent(_pivot, false);
            Model = modelGo.AddComponent<ModelView>();
            Model.materials = materials;
            _pivot.gameObject.SetActive(false);

            _dock = new GameObject("Dock").transform;
            _dock.SetParent(transform, false);
            _btnReplace = PokeButton.Create("Replace", "Место", _dock, new Vector3(-0.15f, 0, 0), materials.ui);
            _btnPrev = PokeButton.Create("Prev", "< Назад", _dock, new Vector3(-0.05f, 0, 0), materials.ui);
            _btnNext = PokeButton.Create("Next", "Далее >", _dock, new Vector3(0.05f, 0, 0), materials.ui);
            _btnExplode = PokeButton.Create("Explode", "Разлёт", _dock, new Vector3(0.15f, 0, 0), materials.ui);
            _buttons.AddRange(new[] { _btnReplace, _btnPrev, _btnNext, _btnExplode });
            _btnPrev.Pressed += () => Prev("button");
            _btnNext.Pressed += () => Next("button");
            _btnExplode.Pressed += ToggleExplode;
            _btnReplace.Pressed += BeginPlacement;
            _dock.gameObject.SetActive(false);

            _hint = HintPanel.Create(transform, Cam);
            _reticle = PlacementReticle.Create(transform, materials.ghost);
            _cursors = HandCursors.Create(transform, materials.ui);

            Debug.Log($"[Stark] Устройство: {device.Name}");
            _entries = PackageSource.List(Application.isEditor ? editorPackagePath : null, bundledPackages);
            _entryIndex = 0;
            _ = LoadCurrentEntryAsync();
        }

        async Task LoadCurrentEntryAsync()
        {
            if (_entries.Count == 0)
            {
                Fail("Нет пакетов", $"Скопируйте .stark в {PackageSource.UserFolder}");
                return;
            }
            var entry = _entries[_entryIndex % _entries.Count];
            try
            {
                CurrentMode = Mode.Loading;
                ShowHintNearUser("Загрузка", entry.DisplayName);
                var bytes = await PackageSource.ReadAsync(entry);
                await LoadPackageAsync(bytes);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Fail("Ошибка пакета", $"{entry.DisplayName}: {e.Message}");
            }
        }

        /// <summary>Загрузить пакет из байтов (также для тестов и будущей загрузки по QR/Wi-Fi).</summary>
        public async Task LoadPackageAsync(byte[] bytes)
        {
            SaveLog();
            CurrentMode = Mode.Loading;
            var pkg = StarkPackageLoader.Load(bytes);
            await Model.LoadAsync(pkg);
            if (this == null) return;

            Package = pkg;
            Steps = new StepSequencer(pkg);
            Steps.StepChanged += OnStepChanged;
            if (!pkg.Hints.ContainsKey(language)) language = pkg.Hints.ContainsKey("ru") ? "ru" : FirstLanguage(pkg);
            _log = new SessionLog(pkg.Manifest.ProductId, () => Time.realtimeSinceStartupAsDouble);
            _log.Mark("loaded", pkg.Manifest.ProductName);
            _logStepStarted = false;

            var size = Model.LocalBounds.size;
            _defaultScale = Manipulation.DefaultScale(Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
            ModelScale = _defaultScale;
            _yaw = 0;
            _explodeOn = false;
            BuildFastenerLabels();

            if (Model.MissingNodes.Count > 0)
                Debug.LogWarning($"[Stark] В model.glb нет узлов: {string.Join(", ", Model.MissingNodes)}");
            Debug.Log($"[Stark] Пакет «{pkg.Manifest.ProductName}»: {pkg.Parts.Count} деталей, {pkg.Steps.Count} шагов");
            BeginPlacement();
        }

        static string FirstLanguage(StarkPackage pkg)
        {
            foreach (var l in pkg.AvailableLanguages) return l;
            return "ru";
        }

        void Fail(string title, string message)
        {
            CurrentMode = Mode.Error;
            ShowHintNearUser(title, message);
        }

        void ShowHintNearUser(string title, string body)
        {
            var cam = Cam;
            if (cam != null && _anchor == null)
            {
                var fwd = cam.transform.forward;
                fwd.y = 0;
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                _hint.transform.SetParent(transform, true);
                _hint.transform.position = cam.transform.position + fwd.normalized * 0.8f - Vector3.up * 0.15f;
            }
            _hint.ShowMessage(title, body);
            _hint.SetStatus("");
        }

        // ───────────────────────── размещение ─────────────────────────

        public void BeginPlacement()
        {
            if (Package == null) return;
            CurrentMode = Mode.Placing;
            _hasPose = false;
            _pivot.gameObject.SetActive(false);
            _dock.gameObject.SetActive(false);
            SetFastenerLabelsActive(false);
            if (_anchor != null)
            {
                _pivot.SetParent(transform, false);
                _dock.SetParent(transform, false);
                _hint.transform.SetParent(transform, true);
                _device.Placement.ReleaseAnchor(_anchor);
                _anchor = null;
            }
            _device.Placement.SetSurfaceVisualization(true);
            _reticle.SetDiameter(Model.Radius * 2f * ModelScale);
            ShowHintNearUser(Package.Manifest.ProductName, "Посмотрите на стол и сделайте щипок, чтобы поставить модель");
        }

        void UpdatePlacement()
        {
            var cam = Cam;
            if (cam == null) return;
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            if (_device.Placement is IPointerRayProvider pointer && pointer.TryGetPointerRay(out var pr)) ray = pr;

            if (!_device.Placement.IsReady)
            {
                _reticle.Hide();
                _hint.SetStatus("Трекинг запускается…");
                _hasPose = false;
                return;
            }
            if (_device.Placement.TryGetSurfacePose(ray, out var pose))
            {
                // Модель «смотрит» на пользователя: +Z от него.
                var away = pose.position - cam.transform.position;
                away.y = 0;
                var rot = away.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(away.normalized, Vector3.up) : pose.rotation;
                _candidatePose = new Pose(pose.position, rot);
                _hasPose = true;
                _reticle.Show(_candidatePose);
                _hint.SetStatus("Щипок / Enter: поставить сюда");
            }
            else
            {
                _hasPose = false;
                _reticle.Hide();
                _hint.SetStatus(_device.Placement.SurfaceCount == 0
                    ? "Осмотрите стол, чтобы очки нашли поверхность"
                    : "Наведите взгляд на стол");
            }
        }

        void ConfirmPlacement()
        {
            if (CurrentMode != Mode.Placing || !_hasPose) return;
            _anchor = _device.Placement.CreateAnchor(_candidatePose);
            _device.Placement.SetSurfaceVisualization(false);
            _reticle.Hide();

            _pivot.SetParent(_anchor, false);
            _pivot.localPosition = Vector3.zero;
            _dock.SetParent(_anchor, false);
            _hint.transform.SetParent(_anchor, false);
            _pivot.gameObject.SetActive(true);
            _dock.gameObject.SetActive(true);
            SetFastenerLabelsActive(true);
            ApplyTransform();

            CurrentMode = Mode.Assembling;
            _log?.Mark("placed");
            if (_log != null && !_logStepStarted)
            {
                _log.EnterStep(Steps.CurrentIndex, StepChangeReason.Jump);
                _logStepStarted = true;
            }
            RefreshStep();
        }

        // ───────────────────────── шаги ─────────────────────────

        public void Next(string source = "")
        {
            if (CurrentMode != Mode.Assembling || Steps == null) return;
            if (Steps.Next()) _log?.Mark("next", source);
        }

        public void Prev(string source = "")
        {
            if (CurrentMode != Mode.Assembling || Steps == null) return;
            if (Steps.Prev()) _log?.Mark("prev", source);
        }

        public void ToggleExplode()
        {
            if (CurrentMode != Mode.Assembling) return;
            _explodeOn = !_explodeOn;
            Model.ExplodeTarget = _explodeOn ? 1f : (Steps.Current?.CameraExplode ?? 0f);
            _btnExplode.Label = _explodeOn ? "Собрать" : "Разлёт";
            _log?.Mark("explode", _explodeOn ? "on" : "off");
        }

        void OnStepChanged(StepChange change)
        {
            if (Steps.IsFinished)
            {
                _log?.Finish();
                SaveLog();
            }
            else
            {
                _log?.EnterStep(change.To, change.Reason);
            }
            RefreshStep();
        }

        void RefreshStep()
        {
            Model.ApplyStep(Steps);
            if (_explodeOn) Model.ExplodeTarget = 1f;
            _btnPrev.Interactable = Steps.CanGoPrev;
            _btnNext.Interactable = Steps.CanGoNext;
            _btnNext.Label = Steps.CurrentIndex >= Steps.Count - 1 ? "Готово" : "Далее >";
            UpdateFastenerLabels();
            if (Steps.IsFinished)
            {
                var minutes = _log != null ? _log.Elapsed / 60.0 : 0;
                _hint.ShowMessage("Сборка завершена", $"{Package.Manifest.ProductName}: {Steps.Count} шагов, {minutes:0.#} мин");
                _hint.SetStatus("Назад: вернуться к последнему шагу");
            }
            else
            {
                _hint.ShowStep(Package, Steps.Current, Steps.CurrentIndex, Steps.Count, language);
                _hint.SetStatus("");
            }
            PositionHint();
        }

        // ───────────────────────── манипуляции ─────────────────────────

        void ApplyTransform()
        {
            ModelScale = Manipulation.Clamp(ModelScale);
            _pivot.localRotation = Quaternion.Euler(0, _yaw, 0);
            _pivot.localScale = Vector3.one * ModelScale;
            // Кнопки перед моделью со стороны пользователя, не вращаются вместе с ней.
            float r = Model.Radius * ModelScale;
            _dock.localPosition = new Vector3(0, PokeButton.DefaultSize.y * 0.5f, -(r + 0.08f));
            _dock.localRotation = Quaternion.identity;
            PositionHint();
        }

        void PositionHint()
        {
            if (_anchor == null || _hint.transform.parent != _anchor) return;
            float top = (Model.LocalBounds.max.y + 0.02f) * ModelScale;
            float r = Model.Radius * ModelScale;
            if (_explodeOn) top += r;
            _hint.transform.localPosition = new Vector3(0, top + 0.06f, r * 0.3f);
        }

        void Rotate(float degrees)
        {
            _yaw = Mathf.Repeat(_yaw + degrees, 360f);
            ApplyTransform();
        }

        void Scale(float factor)
        {
            ModelScale *= factor;
            ApplyTransform();
        }

        // ───────────────────────── ввод ─────────────────────────

        void Update()
        {
            if (_device == null) return;
            var cam = Cam;

            var left = HandFrame.NotTracked;
            var right = HandFrame.NotTracked;
            if (_device.Hands != null && _device.Hands.IsAvailable)
            {
                left = _device.Hands.GetFrame(Hand.Left);
                right = _device.Hands.GetFrame(Hand.Right);
            }
            var userRight = cam != null ? cam.transform.right : Vector3.right;
            _gestures.Update(left, right, Time.time, userRight.ToNumerics());

            if (CurrentMode == Mode.Assembling)
                foreach (var b in _buttons) b.Track(left, right, Time.time);

            if (_cursors != null)
            {
                _cursors.gameObject.SetActive(showHandCursors);
                if (showHandCursors) _cursors.Show(left, right, _gestures.IsPinching(Hand.Left), _gestures.IsPinching(Hand.Right));
            }

            _commands.Clear();
            if (_device.SystemInputs != null)
                foreach (var input in _device.SystemInputs) input?.Poll(_commands);
            foreach (var c in _commands) OnCommand(c);

            if (CurrentMode == Mode.Placing) UpdatePlacement();
            if (CurrentMode == Mode.Assembling) UpdateFastenerLabelPositions();
        }

        void OnGesture(GestureEvent e)
        {
            GestureRecognized?.Invoke(e);
            switch (CurrentMode)
            {
                case Mode.Placing:
                    if (e.Kind == GestureKind.Tap) ConfirmPlacement();
                    break;

                case Mode.Assembling:
                    switch (e.Kind)
                    {
                        case GestureKind.Flick:
                            // Как перелистывание страницы: смахнуть влево = следующий шаг.
                            if (e.Direction < 0) Next("flick");
                            else Prev("flick");
                            break;
                        case GestureKind.Drag:
                            if (_rotatingHand == null && IsNearModel(e.Position - e.Delta))
                                _rotatingHand = e.Hand;
                            if (_rotatingHand == e.Hand)
                                Rotate(Manipulation.YawDelta(e.Delta, (Cam != null ? Cam.transform.right : Vector3.right).ToNumerics()));
                            break;
                        case GestureKind.DragEnd:
                            if (_rotatingHand == e.Hand) _rotatingHand = null;
                            break;
                        case GestureKind.TwoHandScale:
                            if (IsNearModel(e.Position)) Scale(e.Ratio);
                            break;
                    }
                    break;
            }
        }

        bool IsNearModel(System.Numerics.Vector3 worldPos)
        {
            if (_anchor == null) return false;
            var p = worldPos.ToUnity();
            var center = _pivot.TransformPoint(Model.LocalBounds.center);
            float reach = Model.Radius * ModelScale + grabMargin;
            return (p - center).sqrMagnitude <= reach * reach;
        }

        void OnCommand(SystemCommand c)
        {
            switch (c)
            {
                case SystemCommand.Confirm:
                    if (CurrentMode == Mode.Placing) ConfirmPlacement();
                    else Next("key");
                    break;
                case SystemCommand.NextStep: Next("key"); break;
                case SystemCommand.PrevStep: Prev("key"); break;
                case SystemCommand.ToggleExplode: ToggleExplode(); break;
                case SystemCommand.Replace: BeginPlacement(); break;
                case SystemCommand.RotateLeft: if (CurrentMode == Mode.Assembling) Rotate(15f); break;
                case SystemCommand.RotateRight: if (CurrentMode == Mode.Assembling) Rotate(-15f); break;
                case SystemCommand.ScaleUp: if (CurrentMode == Mode.Assembling) Scale(1.15f); break;
                case SystemCommand.ScaleDown: if (CurrentMode == Mode.Assembling) Scale(1f / 1.15f); break;
                case SystemCommand.ResetScale:
                    if (CurrentMode == Mode.Assembling)
                    {
                        ModelScale = Mathf.Approximately(ModelScale, 1f) ? _defaultScale : 1f;
                        ApplyTransform();
                    }
                    break;
                case SystemCommand.ToggleFutureGhosts:
                    Model.showFutureAsGhost = !Model.showFutureAsGhost;
                    Model.RefreshLooks();
                    break;
                case SystemCommand.NextLanguage: CycleLanguage(); break;
                case SystemCommand.NextPackage:
                    if (_entries.Count > 1)
                    {
                        _entryIndex = (_entryIndex + 1) % _entries.Count;
                        _ = LoadCurrentEntryAsync();
                    }
                    break;
            }
        }

        void CycleLanguage()
        {
            if (Package == null) return;
            var langs = new List<string>(Package.AvailableLanguages);
            if (langs.Count < 2) return;
            int i = langs.IndexOf(language);
            language = langs[(i + 1) % langs.Count];
            if (CurrentMode == Mode.Assembling) RefreshStep();
        }

        // ───────────────────────── подписи крепежа ─────────────────────────

        void BuildFastenerLabels()
        {
            foreach (var l in _fastenerLabels) if (l != null) Destroy(l.gameObject);
            _fastenerLabels.Clear();
            for (int i = 0; i < 4; i++)
            {
                var t = WorldText.Create("Fastener Label " + i, transform, 0.018f, TextAnchor.LowerCenter, StarkMaterials.FastenerColor);
                t.gameObject.SetActive(false);
                _fastenerLabels.Add(t);
            }
        }

        void SetFastenerLabelsActive(bool on)
        {
            if (!on) foreach (var l in _fastenerLabels) l.gameObject.SetActive(false);
            else UpdateFastenerLabels();
        }

        void UpdateFastenerLabels()
        {
            var step = Steps?.Current;
            for (int i = 0; i < _fastenerLabels.Count; i++)
            {
                var f = step != null && i < step.Fasteners.Count ? step.Fasteners[i] : null;
                _fastenerLabels[i].gameObject.SetActive(f != null && _anchor != null);
                if (f != null) _fastenerLabels[i].text = "×" + (f.Qty > 0 ? f.Qty : f.PartIds.Count);
            }
            UpdateFastenerLabelPositions();
        }

        void UpdateFastenerLabelPositions()
        {
            var step = Steps?.Current;
            var cam = Cam;
            if (step == null) return;
            for (int i = 0; i < _fastenerLabels.Count && i < step.Fasteners.Count; i++)
            {
                if (!Model.TryGetCenter(step.Fasteners[i].PartIds, out _, out var top)) continue;
                var label = _fastenerLabels[i].transform;
                label.position = Model.transform.TransformPoint(top) + Vector3.up * 0.03f;
                if (cam != null)
                {
                    var away = label.position - cam.transform.position;
                    away.y = 0;
                    if (away.sqrMagnitude > 1e-6f) label.rotation = Quaternion.LookRotation(away, Vector3.up);
                }
            }
        }

        // ───────────────────────── журнал ─────────────────────────

        void SaveLog()
        {
            if (_log == null || Package == null) return;
            try
            {
                var dir = Path.Combine(Application.persistentDataPath, "Sessions");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"{Package.Manifest.ProductId}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                File.WriteAllText(file, _log.ToJson());
                Debug.Log($"[Stark] Журнал сессии: {file}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Stark] Журнал не сохранён: {e.Message}");
            }
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) SaveLog();
        }

        void OnApplicationQuit() => SaveLog();
    }
}
