using System.Collections.Generic;
using UnityEngine;
using NVector3 = System.Numerics.Vector3;

namespace Stark.Presentation
{
    /// <summary>
    /// Переходы между System.Numerics (Core) и UnityEngine, и между системами координат.
    /// glTF правая система, Unity левая: glTFast при импорте инвертирует X. Пути установки
    /// в steps.json заданы в системе glTF, поэтому X у них тоже инвертируем.
    /// </summary>
    public static class UnityConvert
    {
        public static Vector3 ToUnity(this NVector3 v) => new Vector3(v.X, v.Y, v.Z);
        public static NVector3 ToNumerics(this Vector3 v) => new NVector3(v.x, v.y, v.z);

        public static Vector3 GltfToUnity(NVector3 v) => new Vector3(-v.X, v.Y, v.Z);
        public static NVector3 UnityToGltf(Vector3 v) => new NVector3(-v.x, v.y, v.z);

        /// <summary>Путь из пакета (glTF) → путь в системе Unity, для Core-функций анимации.</summary>
        public static List<NVector3> PathToUnity(IReadOnlyList<NVector3> gltfPath)
        {
            if (gltfPath == null) return null;
            var result = new List<NVector3>(gltfPath.Count);
            foreach (var p in gltfPath) result.Add(new NVector3(-p.X, p.Y, p.Z));
            return result;
        }
    }
}
