using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Stark.Core.Gestures;
using Stark.Core.Layout;

namespace Stark.Core.Tests
{
    public class GestureRecognizerTests
    {
        const float Dt = 1f / 60f;
        static readonly Vector3 Right = Vector3.UnitX;

        GestureRecognizer _rec;
        List<GestureEvent> _events;
        float _time;

        [SetUp]
        public void SetUp()
        {
            _rec = new GestureRecognizer();
            _events = new List<GestureEvent>();
            _rec.Gesture += e => _events.Add(e);
            _time = 0;
        }

        static HandFrame Hand(Vector3 at, bool pinched) => new HandFrame
        {
            Tracked = true,
            ThumbTip = at,
            IndexTip = at + new Vector3(0, pinched ? 0.01f : 0.06f, 0),
            Palm = at - new Vector3(0, 0, 0.05f),
        };

        void Frames(int n, System.Func<int, HandFrame> left, System.Func<int, HandFrame> right)
        {
            for (int i = 0; i < n; i++)
            {
                _rec.Update(left(i), right(i), _time, Right);
                _time += Dt;
            }
        }

        void RightOnly(int n, System.Func<int, HandFrame> right) => Frames(n, _ => HandFrame.NotTracked, right);

        List<GestureKind> Kinds() => _events.ConvertAll(e => e.Kind);

        [Test]
        public void ShortStillPinchIsTap()
        {
            var p = new Vector3(0, 1, 0.4f);
            RightOnly(3, _ => Hand(p, false));
            RightOnly(10, _ => Hand(p, true));   // ~0.17 с
            RightOnly(3, _ => Hand(p, false));
            CollectionAssert.AreEqual(new[] { GestureKind.Tap }, Kinds());
            Assert.AreEqual(Stark.Core.Gestures.Hand.Right, _events[0].Hand);
        }

        [Test]
        public void FastHorizontalPinchSwipeIsFlick()
        {
            var p = new Vector3(0, 1, 0.4f);
            RightOnly(2, _ => Hand(p, false));
            // 15 см влево за 0.25 с
            RightOnly(15, i => Hand(p + new Vector3(-0.01f * i, 0, 0), true));
            RightOnly(2, _ => Hand(p + new Vector3(-0.15f, 0, 0), false));
            CollectionAssert.AreEqual(new[] { GestureKind.Flick }, Kinds());
            Assert.AreEqual(-1, _events[0].Direction);
        }

        [Test]
        public void VerticalSwipeIsNotFlick()
        {
            var p = new Vector3(0, 1, 0.4f);
            RightOnly(15, i => Hand(p + new Vector3(0.006f * i, -0.01f * i, 0), true));
            RightOnly(2, _ => Hand(p, false));
            Assert.IsFalse(Kinds().Contains(GestureKind.Flick));
        }

        [Test]
        public void HeldPinchBecomesDragWithAccumulatedDelta()
        {
            var p = new Vector3(0, 1, 0.4f);
            // 1 с медленно вправо на 10 см
            RightOnly(60, i => Hand(p + new Vector3(0.1f * i / 59f, 0, 0), true));
            RightOnly(2, _ => Hand(p + new Vector3(0.1f, 0, 0), false));
            var kinds = Kinds();
            Assert.AreEqual(GestureKind.Drag, kinds[0]);
            Assert.AreEqual(GestureKind.DragEnd, kinds[kinds.Count - 1]);
            Assert.IsFalse(kinds.Contains(GestureKind.Flick));
            Assert.IsFalse(kinds.Contains(GestureKind.Tap));
            float sum = 0;
            foreach (var e in _events) if (e.Kind == GestureKind.Drag) sum += e.Delta.X;
            Assert.AreEqual(0.1f, sum, 1e-4f, "сумма Drag = полное смещение руки");
        }

        [Test]
        public void TwoHandPinchScalesAndSuppressesSingleHandGestures()
        {
            var c = new Vector3(0, 1, 0.4f);
            // Руки разводятся с 20 до 40 см
            Frames(20,
                i => Hand(c - new Vector3(0.1f + 0.1f * i / 19f, 0, 0), true),
                i => Hand(c + new Vector3(0.1f + 0.1f * i / 19f, 0, 0), true));
            Frames(2, _ => Hand(c - new Vector3(0.2f, 0, 0), false), _ => Hand(c + new Vector3(0.2f, 0, 0), false));

            float scale = 1;
            foreach (var e in _events) if (e.Kind == GestureKind.TwoHandScale) scale *= e.Ratio;
            Assert.AreEqual(2f, scale, 1e-3f);
            var kinds = Kinds();
            Assert.IsTrue(kinds.Contains(GestureKind.TwoHandEnd));
            Assert.IsFalse(kinds.Contains(GestureKind.Flick), "разведение рук не листает шаги");
            Assert.IsFalse(kinds.Contains(GestureKind.Tap));
        }

        [Test]
        public void LostTrackingCancelsPinchWithoutTap()
        {
            var p = new Vector3(0, 1, 0.4f);
            RightOnly(5, _ => Hand(p, true));
            RightOnly(10, _ => HandFrame.NotTracked);
            RightOnly(3, _ => Hand(p, false));
            Assert.AreEqual(0, _events.Count);
        }

        [Test]
        public void HysteresisIgnoresJitterAroundThreshold()
        {
            var p = new Vector3(0, 1, 0.4f);
            HandFrame At(float gap) => new HandFrame { Tracked = true, ThumbTip = p, IndexTip = p + new Vector3(0, gap, 0) };
            RightOnly(1, _ => At(0.015f)); // щипок
            // дрожание 2.5–3.2 см: выше порога включения, но ниже порога выключения
            RightOnly(6, i => At(i % 2 == 0 ? 0.025f : 0.032f));
            Assert.IsTrue(_rec.IsPinching(Stark.Core.Gestures.Hand.Right));
            RightOnly(1, _ => At(0.05f));
            CollectionAssert.AreEqual(new[] { GestureKind.Tap }, Kinds());
        }
    }

    public class PokeDetectorTests
    {
        [Test]
        public void PressOnceWhenEnteringFromAbove()
        {
            var b = new PokeDetector(new Vector3(0.03f, 0.01f, 0.03f));
            float t = 0;
            Assert.IsFalse(b.Update(new Vector3(0, 0.04f, 0), true, t += 0.02f)); // над кнопкой
            Assert.IsTrue(b.Update(new Vector3(0, 0.005f, 0), true, t += 0.02f)); // нажали
            Assert.IsFalse(b.Update(new Vector3(0, 0.0f, 0), true, t += 0.02f));  // держим: без повтора
            Assert.IsFalse(b.Update(new Vector3(0, 0.04f, 0), true, t += 0.02f)); // вышли вверх
            Assert.IsFalse(b.Update(new Vector3(0, 0.005f, 0), true, t += 0.02f), "пауза Cooldown");
            b.Update(new Vector3(0, 0.04f, 0), true, t += 1f);
            Assert.IsTrue(b.Update(new Vector3(0, 0.005f, 0), true, t += 0.02f));
        }

        [Test]
        public void SideEntryDoesNotPress()
        {
            var b = new PokeDetector(new Vector3(0.03f, 0.01f, 0.03f));
            Assert.IsFalse(b.Update(new Vector3(0.1f, 0, 0), true, 0));
            Assert.IsFalse(b.Update(new Vector3(0.0f, 0, 0), true, 0.02f), "рука скользнула сбоку, например с деталью");
        }
    }

    public class LayoutTests
    {
        [Test]
        public void InsertAnimationStartsAtPathStartAndLandsAtZero()
        {
            var path = new List<Vector3> { new Vector3(0, 0.1f, 0), Vector3.Zero };
            Assert.AreEqual(path[0], InsertAnimation.OffsetAt(path, 0));
            Assert.AreEqual(Vector3.Zero, InsertAnimation.OffsetAt(path, InsertAnimation.FlyDuration + 0.1f));
            var mid = InsertAnimation.OffsetAt(path, InsertAnimation.FlyDuration / 2);
            Assert.AreEqual(0.05f, mid.Y, 1e-4f);
            Assert.AreEqual(path[0], InsertAnimation.OffsetAt(path, InsertAnimation.Period), "цикл");
        }

        [Test]
        public void SampleFollowsPolylineByLength()
        {
            var path = new List<Vector3> { new Vector3(0.3f, 0, 0), new Vector3(0.1f, 0, 0), Vector3.Zero };
            Assert.AreEqual(0.15f, InsertAnimation.Sample(path, 0.5f).X, 1e-5f);
        }

        [Test]
        public void ExplodeUsesInsertDirectionOrRadial()
        {
            var up = new List<Vector3> { new Vector3(0, 0.04f, 0), Vector3.Zero };
            var o = ExplodeLayout.Offset(new Vector3(0.05f, 0, 0), Vector3.Zero, up, 1f, 0.1f);
            Assert.AreEqual(0, o.X, 1e-6f);
            Assert.Greater(o.Y, 0);
            var r = ExplodeLayout.Offset(new Vector3(0.05f, 0, 0), Vector3.Zero, null, 1f, 0.1f);
            Assert.Greater(r.X, 0);
            Assert.AreEqual(Vector3.Zero, ExplodeLayout.Offset(new Vector3(1, 0, 0), Vector3.Zero, null, 0f, 0.1f));
            var center = ExplodeLayout.Offset(Vector3.Zero, Vector3.Zero, null, 1f, 0.1f);
            Assert.Greater(center.Y, 0, "деталь в центре поднимается вверх");
        }

        [Test]
        public void DefaultScaleShrinksOnlyLargeProducts()
        {
            Assert.AreEqual(1f, Manipulation.DefaultScale(0.2f));
            Assert.AreEqual(0.35f, Manipulation.DefaultScale(1.0f), 1e-5f);
            Assert.AreEqual(Manipulation.MinScale, Manipulation.DefaultScale(100f));
        }

        [Test]
        public void YawFollowsHandAlongUserRight()
        {
            Assert.Less(Manipulation.YawDelta(new Vector3(0.1f, 0, 0), Vector3.UnitX), 0);
            Assert.Greater(Manipulation.YawDelta(new Vector3(-0.1f, 0, 0), Vector3.UnitX), 0);
            Assert.AreEqual(0, Manipulation.YawDelta(new Vector3(0, 0.1f, 0), Vector3.UnitX), 1e-6f, "вертикальное движение не вращает");
        }
    }
}
