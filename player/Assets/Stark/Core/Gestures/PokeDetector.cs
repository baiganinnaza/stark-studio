using System.Numerics;

namespace Stark.Core.Gestures
{
    /// <summary>
    /// «Поки»-кнопка: нажатие кончиком указательного пальца. Кнопка = коробка в своей локальной
    /// системе (центр 0, полуразмеры HalfExtents, верхняя грань +Y). Слой Unity переводит кончик
    /// пальца в локальные координаты кнопки и передаёт сюда.
    /// Срабатывание: палец вошёл в объём сверху (был выше коробки, затем внутри). Повтор только
    /// после выхода пальца и паузы Cooldown, чтобы дрожание руки не листало шаги дважды.
    /// </summary>
    public sealed class PokeDetector
    {
        public Vector3 HalfExtents;
        public float Cooldown = 0.6f;
        /// <summary>Насколько выше верхней грани палец считается «над кнопкой» (м).</summary>
        public float HoverHeight = 0.05f;

        bool _armed;      // палец был над кнопкой и готов нажать
        bool _inside;
        float _lastPress = float.NegativeInfinity;

        public PokeDetector(Vector3 halfExtents) { HalfExtents = halfExtents; }

        public bool IsInside => _inside;
        public bool IsHovered { get; private set; }

        /// <summary>Возвращает true в кадр нажатия. tracked=false сбрасывает состояние.</summary>
        public bool Update(Vector3 fingertipLocal, bool tracked, float time)
        {
            if (!tracked)
            {
                _armed = _inside = IsHovered = false;
                return false;
            }
            var p = fingertipLocal;
            var h = HalfExtents;
            bool withinXZ = System.Math.Abs(p.X) <= h.X && System.Math.Abs(p.Z) <= h.Z;
            bool inside = withinXZ && System.Math.Abs(p.Y) <= h.Y;
            bool above = withinXZ && p.Y > h.Y && p.Y <= h.Y + HoverHeight;
            IsHovered = above || inside;

            bool pressed = false;
            if (inside && !_inside && _armed && time - _lastPress >= Cooldown)
            {
                pressed = true;
                _lastPress = time;
                _armed = false;
            }
            if (above) _armed = true;
            else if (!inside) _armed = false;
            _inside = inside;
            return pressed;
        }
    }
}
