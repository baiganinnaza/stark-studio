using System;
using System.Collections.Generic;
using System.Numerics;

namespace Stark.Core.Layout
{
    /// <summary>
    /// Анимация установки: деталь-«призрак» летит по insertPath от path[0] к [0,0,0],
    /// держится на месте, затем цикл повторяется.
    /// </summary>
    public static class InsertAnimation
    {
        public const float FlyDuration = 1.4f;
        public const float HoldDuration = 0.6f;
        public const float Period = FlyDuration + HoldDuration;

        /// <summary>Смещение детали в момент time (с). path в метрах, последняя точка [0,0,0].</summary>
        public static Vector3 OffsetAt(IReadOnlyList<Vector3> path, float time)
        {
            if (path == null || path.Count == 0) return Vector3.Zero;
            if (path.Count == 1) return path[0];
            float phase = time % Period;
            if (phase < 0) phase += Period;
            float t = phase >= FlyDuration ? 1f : SmoothStep(phase / FlyDuration);
            return Sample(path, t);
        }

        /// <summary>Точка на ломаной по доле длины t ∈ [0,1].</summary>
        public static Vector3 Sample(IReadOnlyList<Vector3> path, float t)
        {
            if (path == null || path.Count == 0) return Vector3.Zero;
            if (path.Count == 1 || t <= 0) return path[0];
            if (t >= 1) return path[path.Count - 1];
            float total = 0;
            for (int i = 1; i < path.Count; i++) total += Vector3.Distance(path[i - 1], path[i]);
            if (total < 1e-6f) return path[path.Count - 1];
            float target = t * total;
            for (int i = 1; i < path.Count; i++)
            {
                float seg = Vector3.Distance(path[i - 1], path[i]);
                if (target <= seg && seg > 0) return Vector3.Lerp(path[i - 1], path[i], target / seg);
                target -= seg;
            }
            return path[path.Count - 1];
        }

        static float SmoothStep(float x) => x * x * (3 - 2 * x);
    }

    /// <summary>
    /// Разлёт (explode). Деталь смещается по направлению извлечения (обратно пути установки),
    /// если оно известно, иначе по лучу от центра сборки. amount ∈ [0,1], radius = размер сборки.
    /// </summary>
    public static class ExplodeLayout
    {
        public static Vector3 Offset(Vector3 partCenter, Vector3 assemblyCenter, IReadOnlyList<Vector3> insertPath, float amount, float radius)
        {
            if (amount <= 0 || radius <= 0) return Vector3.Zero;
            Vector3 dir;
            if (insertPath != null && insertPath.Count > 0 && insertPath[0].LengthSquared() > 1e-10f)
            {
                dir = Vector3.Normalize(insertPath[0]);
            }
            else
            {
                var radial = partCenter - assemblyCenter;
                // Деталь в центре (вал, ось): поднимаем вверх, а не оставляем на месте.
                dir = radial.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(radial);
            }
            // Дальше от центра → дальше разлетается, чтобы детали не наезжали друг на друга.
            float spread = 0.6f + Math.Min(1.5f, (partCenter - assemblyCenter).Length() / radius);
            return dir * (amount * radius * spread);
        }
    }

    /// <summary>Математика ручных манипуляций с моделью на столе.</summary>
    public static class Manipulation
    {
        /// <summary>Крупные изделия по умолчанию показываем уменьшенными до FitSize (поле зрения ~52°).</summary>
        public const float FitSize = 0.35f;
        public const float FitThreshold = 0.45f;
        public const float MinScale = 0.05f;
        public const float MaxScale = 1.5f;
        /// <summary>Градусов поворота на метр горизонтального движения руки.</summary>
        public const float DegreesPerMeter = 450f;

        /// <summary>Начальный масштаб: 1:1 для мелких изделий, уменьшение до FitSize для крупных.</summary>
        public static float DefaultScale(float maxDimension)
        {
            if (maxDimension <= 0 || float.IsNaN(maxDimension)) return 1f;
            return maxDimension > FitThreshold ? Clamp(FitSize / maxDimension) : 1f;
        }

        public static float Clamp(float scale) => Math.Max(MinScale, Math.Min(MaxScale, scale));

        /// <summary>
        /// Изменение поворота вокруг вертикали (градусы) от смещения руки.
        /// Вращаем только вокруг вертикали, чтобы модель не «падала» со стола.
        /// </summary>
        public static float YawDelta(Vector3 handDelta, Vector3 userRight)
        {
            var r = new Vector3(userRight.X, 0, userRight.Z);
            if (r.LengthSquared() < 1e-8f) return 0;
            r = Vector3.Normalize(r);
            // Рука вправо → ближняя к пользователю сторона модели уходит вправо, как будто
            // крутишь поворотный столик. В Unity (левая система, Y вверх) это отрицательный yaw.
            return -Vector3.Dot(handDelta, r) * DegreesPerMeter;
        }
    }
}
