using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GLTFast;
using Stark.Core.Layout;
using Stark.Core.Package;
using Stark.Core.Steps;
using UnityEngine;

namespace Stark.Presentation
{
    /// <summary>Одна деталь сборки в сцене (узел glTF с именем = partId).</summary>
    public sealed class PartView
    {
        public string Id;
        public Transform Transform;
        public Vector3 BaseLocalPosition;
        /// <summary>Центр детали в системе ModelView (= сцена glTF после инверсии X).</summary>
        public Vector3 CenterInModel;
        public Renderer[] Renderers;
        public Material[][] OriginalMaterials;
        public PartState State = PartState.Done;
        /// <summary>Путь установки текущего шага в системе Unity (метры, последняя точка 0).</summary>
        public List<System.Numerics.Vector3> ActivePath;
        /// <summary>Путь для разлёта: направление извлечения из шага, где деталь ставится.</summary>
        public List<System.Numerics.Vector3> ExplodePath;
    }

    /// <summary>
    /// Модель изделия: загрузка model.glb через glTFast, состояния деталей по шагам,
    /// разлёт и анимация установки. Не знает про устройство и ввод.
    /// </summary>
    public sealed class ModelView : MonoBehaviour
    {
        public StarkMaterials materials = new StarkMaterials();
        public bool showFutureAsGhost;

        readonly Dictionary<string, PartView> _parts = new Dictionary<string, PartView>();
        readonly List<string> _missingNodes = new List<string>();
        GltfImport _gltf;
        MaterialPropertyBlock _block;
        float _explode;
        float _explodeTarget;
        float _animTime;

        public IReadOnlyDictionary<string, PartView> Parts => _parts;
        /// <summary>partId из steps.json, для которых нет узла в model.glb.</summary>
        public IReadOnlyList<string> MissingNodes => _missingNodes;
        public Bounds LocalBounds { get; private set; }
        public float Radius { get; private set; }
        public float ExplodeAmount => _explode;
        public bool Loaded { get; private set; }

        /// <summary>Целевой разлёт 0..1, к нему плавно идёт текущее значение.</summary>
        public float ExplodeTarget
        {
            get => _explodeTarget;
            set => _explodeTarget = Mathf.Clamp01(value);
        }

        public async Task LoadAsync(StarkPackage pkg)
        {
            Unload();
            materials.EnsureCreated();
            _block = new MaterialPropertyBlock();

            _gltf = new GltfImport();
            bool ok = await _gltf.LoadGltfBinary(pkg.ModelGlb);
            if (!ok) throw new StarkFormatException("glTFast не смог прочитать model.glb");
            ok = await _gltf.InstantiateMainSceneAsync(transform);
            if (!ok) throw new StarkFormatException("glTFast не смог создать сцену модели");
            if (this == null) return; // объект уничтожен во время загрузки

            var byName = new Dictionary<string, Transform>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
                if (t != transform && !byName.ContainsKey(t.name)) byName[t.name] = t;

            var total = new Bounds();
            bool hasBounds = false;
            foreach (var info in pkg.Parts)
            {
                if (!byName.TryGetValue(info.Id, out var t))
                {
                    _missingNodes.Add(info.Id);
                    continue;
                }
                var renderers = t.GetComponentsInChildren<Renderer>(true);
                var originals = new Material[renderers.Length][];
                var b = new Bounds(transform.InverseTransformPoint(t.position), Vector3.zero);
                bool partHasBounds = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    originals[i] = renderers[i].sharedMaterials;
                    renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    // Renderer.bounds пустые у неактивных объектов (модель грузится скрытой),
                    // поэтому берём границы сетки и переводим их матрицами.
                    if (!TryMeshBounds(renderers[i], out var meshBounds)) continue;
                    var toModel = transform.worldToLocalMatrix * renderers[i].transform.localToWorldMatrix;
                    foreach (var corner in Corners(meshBounds))
                    {
                        var local = toModel.MultiplyPoint3x4(corner);
                        if (!partHasBounds) { b = new Bounds(local, Vector3.zero); partHasBounds = true; }
                        else b.Encapsulate(local);
                    }
                }
                if (partHasBounds)
                {
                    if (!hasBounds) { total = b; hasBounds = true; }
                    else total.Encapsulate(b);
                }
                _parts[info.Id] = new PartView
                {
                    Id = info.Id,
                    Transform = t,
                    BaseLocalPosition = t.localPosition,
                    CenterInModel = b.center,
                    Renderers = renderers,
                    OriginalMaterials = originals,
                };
            }

            // Направление разлёта каждой детали = путь установки из её шага.
            foreach (var step in pkg.Steps)
            foreach (var id in step.AllPartIds)
                if (_parts.TryGetValue(id, out var pv))
                    pv.ExplodePath = UnityConvert.PathToUnity(step.PathFor(id));

            LocalBounds = total;
            Radius = Mathf.Max(0.01f, total.extents.magnitude);
            Loaded = true;
        }

        public void Unload()
        {
            Loaded = false;
            _parts.Clear();
            _missingNodes.Clear();
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);
            _gltf?.Dispose();
            _gltf = null;
            _explode = _explodeTarget = 0;
        }

        void OnDestroy() => _gltf?.Dispose();

        /// <summary>Применить состояния деталей для текущего шага.</summary>
        public void ApplyStep(StepSequencer seq)
        {
            var step = seq.Current;
            foreach (var pv in _parts.Values)
            {
                pv.State = seq.StateOf(pv.Id);
                pv.ActivePath = null;
                if (step != null && (pv.State == PartState.Current || pv.State == PartState.CurrentFastener))
                    pv.ActivePath = UnityConvert.PathToUnity(step.PathFor(pv.Id));
                ApplyLook(pv);
            }
            _animTime = 0;
            ExplodeTarget = step?.CameraExplode ?? 0f;
        }

        public void RefreshLooks()
        {
            foreach (var pv in _parts.Values) ApplyLook(pv);
        }

        void ApplyLook(PartView pv)
        {
            bool visible = pv.State != PartState.Future || showFutureAsGhost;
            for (int i = 0; i < pv.Renderers.Length; i++)
            {
                var r = pv.Renderers[i];
                r.enabled = visible;
                r.SetPropertyBlock(null);
                Material replacement = null;
                switch (pv.State)
                {
                    case PartState.Current: replacement = materials.current; break;
                    case PartState.CurrentFastener: replacement = materials.fastener; break;
                    case PartState.Future: replacement = materials.ghost; break;
                }
                if (replacement == null)
                {
                    r.sharedMaterials = pv.OriginalMaterials[i];
                }
                else
                {
                    var mats = new Material[Mathf.Max(1, pv.OriginalMaterials[i].Length)];
                    for (int m = 0; m < mats.Length; m++) mats[m] = replacement;
                    r.sharedMaterials = mats;
                }
            }
        }

        void Update()
        {
            if (!Loaded) return;
            _explode = Mathf.MoveTowards(_explode, _explodeTarget, Time.deltaTime * 1.5f);
            _animTime += Time.deltaTime;

            // Пульсация текущих деталей: яркость 70–100%.
            float pulse = 0.85f + 0.15f * Mathf.Sin(Time.time * Mathf.PI * 2f / 1.2f);
            var center = LocalBounds.center.ToNumerics();

            foreach (var pv in _parts.Values)
            {
                if (pv.Transform == null) continue;
                Vector3 offsetModel = Vector3.zero;
                if (_explode > 0.001f && pv.State != PartState.Future)
                    offsetModel += ExplodeLayout.Offset(pv.CenterInModel.ToNumerics(), center, pv.ExplodePath, _explode, Radius).ToUnity();
                if (pv.ActivePath != null)
                    offsetModel += InsertAnimation.OffsetAt(pv.ActivePath, _animTime).ToUnity();

                // Смещение задано в системе ModelView; деталь может лежать глубже в иерархии glTF.
                var parent = pv.Transform.parent;
                var offsetLocal = parent == transform || parent == null
                    ? offsetModel
                    : parent.InverseTransformVector(transform.TransformVector(offsetModel));
                pv.Transform.localPosition = pv.BaseLocalPosition + offsetLocal;

                if (pv.State == PartState.Current || pv.State == PartState.CurrentFastener)
                {
                    var c = pv.State == PartState.Current ? StarkMaterials.CurrentColor : StarkMaterials.FastenerColor;
                    StarkMaterials.SetColor(_block, new Color(c.r * pulse, c.g * pulse, c.b * pulse, 1f));
                    foreach (var r in pv.Renderers) r.SetPropertyBlock(_block);
                }
            }
        }

        /// <summary>Центр группы деталей в системе ModelView (для подписей «×4»).</summary>
        public bool TryGetCenter(IEnumerable<string> partIds, out Vector3 center, out Vector3 top)
        {
            center = top = Vector3.zero;
            int n = 0;
            float maxY = float.NegativeInfinity;
            foreach (var id in partIds)
            {
                if (!_parts.TryGetValue(id, out var pv)) continue;
                center += pv.CenterInModel;
                maxY = Mathf.Max(maxY, pv.CenterInModel.y);
                n++;
            }
            if (n == 0) return false;
            center /= n;
            top = new Vector3(center.x, maxY, center.z);
            return true;
        }

        static bool TryMeshBounds(Renderer r, out Bounds bounds)
        {
            bounds = default;
            Mesh mesh = null;
            if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh;
            else if (r.TryGetComponent<MeshFilter>(out var mf)) mesh = mf.sharedMesh;
            if (mesh == null) return false;
            bounds = mesh.bounds;
            return true;
        }

        static IEnumerable<Vector3> Corners(Bounds b)
        {
            var min = b.min;
            var max = b.max;
            for (int i = 0; i < 8; i++)
                yield return new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
        }
    }
}
