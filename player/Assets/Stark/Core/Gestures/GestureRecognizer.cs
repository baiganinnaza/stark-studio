using System;
using System.Numerics;

namespace Stark.Core.Gestures
{
    public enum Hand { Left = 0, Right = 1 }

    /// <summary>
    /// Снимок одной руки за кадр в мировых координатах (метры).
    /// Адаптер устройства заполняет его из XR Hands (суставы OpenXR), симулятор из мыши.
    /// </summary>
    public struct HandFrame
    {
        public bool Tracked;
        public Vector3 ThumbTip;
        public Vector3 IndexTip;
        public Vector3 Palm;

        public Vector3 PinchPoint => (ThumbTip + IndexTip) * 0.5f;
        public float PinchDistance => Vector3.Distance(ThumbTip, IndexTip);

        public static HandFrame NotTracked => new HandFrame { Tracked = false };
    }

    public enum GestureKind
    {
        /// <summary>Короткий щипок без движения (поставить модель, выбрать).</summary>
        Tap,
        /// <summary>Быстрый щипок со смахиванием в сторону: листать шаги. Direction = +1 вправо, -1 влево.</summary>
        Flick,
        /// <summary>Щипок с удержанием и движением: вращение модели. Delta = смещение руки за кадр.</summary>
        Drag,
        DragEnd,
        /// <summary>Щипок двумя руками: масштаб. Ratio = изменение расстояния между руками за кадр.</summary>
        TwoHandScale,
        TwoHandEnd,
    }

    public struct GestureEvent
    {
        public GestureKind Kind;
        public Hand Hand;
        public Vector3 Position;
        public Vector3 Delta;
        public int Direction;
        public float Ratio;

        public override string ToString() => $"{Kind} {Hand} dir={Direction} ratio={Ratio:0.000}";
    }

    /// <summary>Пороги распознавания. Значения по умолчанию [О]: проверить на Air 2 Ultra на этапе 0.</summary>
    public sealed class GestureSettings
    {
        /// <summary>Щипок начинается, когда большой и указательный ближе этого (м).</summary>
        public float PinchOnDistance = 0.020f;
        /// <summary>Щипок кончается, когда пальцы дальше этого (м). Гистерезис против дрожания.</summary>
        public float PinchOffDistance = 0.035f;
        /// <summary>Tap: щипок короче (с) и смещение меньше (м).</summary>
        public float TapMaxDuration = 0.35f;
        public float TapMaxMove = 0.025f;
        /// <summary>Flick: быстрее (с), горизонтально дальше (м), и горизонталь в N раз больше вертикали.</summary>
        public float FlickMaxDuration = 0.45f;
        public float FlickMinDistance = 0.08f;
        public float FlickMinRatio = 2f;
        /// <summary>
        /// Drag (вращение) начинается после удержания щипка (с). Не раньше окна Flick, иначе быстрое
        /// смахивание успело бы повернуть модель. Смещение за это время не теряется: оно приходит
        /// первым событием Drag.
        /// </summary>
        public float DragMinHold = 0.45f;
        /// <summary>Кадров подряд без трекинга, после которых щипок отменяется.</summary>
        public int LostFramesToCancel = 4;
    }

    /// <summary>
    /// Распознаёт жесты двух рук из потока HandFrame. Чистая логика, без Unity:
    /// одинаково работает с XR Hands на очках, с симулятором в редакторе и в юнит-тестах.
    /// </summary>
    public sealed class GestureRecognizer
    {
        public GestureSettings Settings { get; }
        public event Action<GestureEvent> Gesture;

        readonly HandTracker[] _hands = { new HandTracker(Hand.Left), new HandTracker(Hand.Right) };
        bool _twoHand;
        float _twoHandLastDistance;

        public GestureRecognizer(GestureSettings settings = null)
        {
            Settings = settings ?? new GestureSettings();
        }

        public bool IsPinching(Hand hand) => _hands[(int)hand].Pinching;
        public bool InTwoHandGesture => _twoHand;

        /// <summary>
        /// Вызывать каждый кадр. right = направление «вправо» для пользователя (camera.right),
        /// по нему определяется сторона смахивания.
        /// </summary>
        public void Update(HandFrame left, HandFrame right, float time, Vector3 userRight)
        {
            var r = Vector3.Normalize(new Vector3(userRight.X, 0, userRight.Z));
            if (float.IsNaN(r.X)) r = Vector3.UnitX;

            _hands[0].Step(left, time, Settings);
            _hands[1].Step(right, time, Settings);

            var L = _hands[0];
            var R = _hands[1];

            // Две руки: как только оба щипка активны, одноручные жесты этих щипков гасятся.
            if (L.Pinching && R.Pinching)
            {
                float d = Vector3.Distance(L.Position, R.Position);
                if (!_twoHand)
                {
                    _twoHand = true;
                    _twoHandLastDistance = d;
                    if (L.Dragging) Emit(new GestureEvent { Kind = GestureKind.DragEnd, Hand = Hand.Left, Position = L.Position });
                    if (R.Dragging) Emit(new GestureEvent { Kind = GestureKind.DragEnd, Hand = Hand.Right, Position = R.Position });
                    L.Consumed = R.Consumed = true;
                }
                else if (_twoHandLastDistance > 1e-4f && d > 1e-4f)
                {
                    float ratio = d / _twoHandLastDistance;
                    _twoHandLastDistance = d;
                    if (Math.Abs(ratio - 1f) > 1e-4f)
                        Emit(new GestureEvent { Kind = GestureKind.TwoHandScale, Position = (L.Position + R.Position) * 0.5f, Ratio = ratio });
                }
            }
            else if (_twoHand)
            {
                _twoHand = false;
                Emit(new GestureEvent { Kind = GestureKind.TwoHandEnd });
            }

            foreach (var h in _hands) Classify(h, time, r);
        }

        void Classify(HandTracker h, float time, Vector3 right)
        {
            if (h.JustReleased)
            {
                if (h.Consumed) return;
                float dur = time - h.StartTime;
                var move = h.Position - h.StartPosition;
                float horiz = Vector3.Dot(move, right);
                float vert = Math.Abs(move.Y);
                if (h.Dragging)
                {
                    Emit(new GestureEvent { Kind = GestureKind.DragEnd, Hand = h.Hand, Position = h.Position });
                }
                else if (dur <= Settings.FlickMaxDuration && Math.Abs(horiz) >= Settings.FlickMinDistance &&
                         Math.Abs(horiz) >= Settings.FlickMinRatio * vert)
                {
                    Emit(new GestureEvent { Kind = GestureKind.Flick, Hand = h.Hand, Position = h.StartPosition, Direction = Math.Sign(horiz), Delta = move });
                }
                else if (dur <= Settings.TapMaxDuration && move.Length() <= Settings.TapMaxMove)
                {
                    Emit(new GestureEvent { Kind = GestureKind.Tap, Hand = h.Hand, Position = h.StartPosition });
                }
                return;
            }

            if (h.Cancelled)
            {
                if (h.Dragging) Emit(new GestureEvent { Kind = GestureKind.DragEnd, Hand = h.Hand, Position = h.Position });
                return;
            }

            if (!h.Pinching || h.Consumed || _twoHand) return;

            if (!h.Dragging)
            {
                float held = time - h.StartTime;
                if (held >= Math.Max(Settings.DragMinHold, Settings.FlickMaxDuration))
                {
                    h.Dragging = true;
                    // Первое событие несёт всё накопленное смещение, чтобы вращение не «отставало».
                    Emit(new GestureEvent { Kind = GestureKind.Drag, Hand = h.Hand, Position = h.Position, Delta = h.Position - h.StartPosition });
                }
            }
            else
            {
                var delta = h.Position - h.PrevPosition;
                if (delta.LengthSquared() > 0)
                    Emit(new GestureEvent { Kind = GestureKind.Drag, Hand = h.Hand, Position = h.Position, Delta = delta });
            }
        }

        void Emit(GestureEvent e) => Gesture?.Invoke(e);

        sealed class HandTracker
        {
            public readonly Hand Hand;
            public bool Pinching;
            public bool JustReleased;
            public bool Cancelled;
            public bool Dragging;
            public bool Consumed;
            public float StartTime;
            public Vector3 StartPosition;
            public Vector3 Position;
            public Vector3 PrevPosition;
            int _lostFrames;

            public HandTracker(Hand hand) { Hand = hand; }

            public void Step(HandFrame f, float time, GestureSettings s)
            {
                // Флаги прошлого кадра сбрасываем, и вместе с ними состояние завершённого щипка.
                if (JustReleased || Cancelled)
                {
                    Dragging = false;
                    Consumed = false;
                }
                JustReleased = false;
                Cancelled = false;
                PrevPosition = Position;

                if (!f.Tracked)
                {
                    if (Pinching && ++_lostFrames >= s.LostFramesToCancel)
                    {
                        Pinching = false;
                        Cancelled = true;
                    }
                    return;
                }
                _lostFrames = 0;
                float d = f.PinchDistance;
                Position = f.PinchPoint;

                if (!Pinching)
                {
                    PrevPosition = Position;
                    if (d < s.PinchOnDistance)
                    {
                        Pinching = true;
                        Dragging = false;
                        Consumed = false;
                        StartTime = time;
                        StartPosition = Position;
                    }
                }
                else if (d > s.PinchOffDistance)
                {
                    Pinching = false;
                    JustReleased = true;
                }
            }
        }
    }
}
