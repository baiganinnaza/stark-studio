using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Stark.Core.Json;
using Stark.Core.Package;
using Stark.Core.Session;
using Stark.Core.Steps;

namespace Stark.Core.Tests
{
    public class MiniJsonTests
    {
        [Test]
        public void ParsesNestedStructuresAndUnicode()
        {
            var o = (System.Collections.Generic.Dictionary<string, object>)MiniJson.Parse(
                "{\"a\": [1, -2.5e1, true, null], \"t\": \"Шаг \\u00e9\\n\", \"o\": {}}");
            var a = (System.Collections.Generic.List<object>)o["a"];
            Assert.AreEqual(1.0, a[0]);
            Assert.AreEqual(-25.0, a[1]);
            Assert.AreEqual(true, a[2]);
            Assert.IsNull(a[3]);
            Assert.AreEqual("Шаг é\n", o["t"]);
        }

        [Test]
        public void RejectsBrokenJson()
        {
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{\"a\": [1, 2}"));
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{} x"));
        }

        [Test]
        public void RoundTrip()
        {
            string json = MiniJson.Serialize(new System.Collections.Generic.Dictionary<string, object>
            {
                { "s", "кавычка \" и \\" }, { "n", 1.5 }, { "i", 3 }, { "l", new object[] { true, null } },
            });
            var back = (System.Collections.Generic.Dictionary<string, object>)MiniJson.Parse(json);
            Assert.AreEqual("кавычка \" и \\", back["s"]);
            Assert.AreEqual(1.5, back["n"]);
            Assert.AreEqual(3.0, back["i"]);
        }
    }

    public class PackageLoaderTests
    {
        [Test]
        public void LoadsGearboxSample()
        {
            var pkg = StarkPackageLoader.LoadFile(TestData.SamplePath);
            Assert.AreEqual(1, pkg.Manifest.FormatVersion);
            Assert.AreEqual("Редукторный модуль", pkg.Manifest.ProductName);
            Assert.AreEqual(17, pkg.Parts.Count);
            Assert.AreEqual(8, pkg.Steps.Count);
            Assert.IsTrue(pkg.ModelGlb.Length > 1000);
            Assert.AreEqual("Установите: Основание", pkg.HintFor(pkg.Steps[0], "ru"));

            // Каждая деталь в ровно одном шаге
            var all = pkg.Steps.SelectMany(s => s.AllPartIds).ToList();
            Assert.AreEqual(17, all.Count);
            Assert.AreEqual(17, all.Distinct().Count());

            // Шаг с крышкой: 4 болта M6, у каждого есть путь установки
            var cover = pkg.Steps.First(s => s.Parts.Contains("P006"));
            Assert.AreEqual(4, cover.FastenerCount);
            foreach (var id in cover.Fasteners[0].PartIds)
                Assert.IsNotNull(cover.PathFor(id), id);
            var path = cover.PathFor("P006");
            Assert.AreEqual(Vector3.Zero, path[path.Count - 1]);
        }

        [Test]
        public void HintFallsBackToRussianThenTitle()
        {
            var pkg = StarkPackageLoader.Load(TestData.MakePackage(TestData.Manifest, TestData.TwoSteps, "{\"H1\": \"Поставьте основание\"}"));
            Assert.AreEqual("Поставьте основание", pkg.HintFor(pkg.Steps[0], "kk"));
            Assert.AreEqual("Крышка", pkg.HintFor(pkg.Steps[1], "ru"));
        }

        [Test]
        public void FastenerPathSharedBetweenInstances()
        {
            var pkg = StarkPackageLoader.Load(TestData.MakePackage(TestData.Manifest, TestData.TwoSteps));
            var s = pkg.Steps[1];
            Assert.AreEqual(new Vector3(0, 0.05f, 0), s.PathFor("F1")[0]);
            Assert.AreEqual(new Vector3(0, 0.05f, 0), s.PathFor("F2")[0], "F2 берёт путь соседнего болта");
            Assert.AreEqual(new Vector3(0, 0.04f, 0), s.PathFor("B")[0], "без partPaths используется insertPath");
            Assert.AreEqual(2.5, s.Fasteners[0].TorqueNm);
            Assert.AreEqual("Ключ 8", s.Tool);
        }

        [Test]
        public void RejectsWrongFormatVersion()
        {
            var manifest = TestData.Manifest.Replace("\"formatVersion\":1", "\"formatVersion\":2");
            var e = Assert.Throws<StarkFormatException>(() => StarkPackageLoader.Load(TestData.MakePackage(manifest, TestData.TwoSteps)));
            StringAssert.Contains("Версия формата 2", e.Message);
        }

        [Test]
        public void RejectsMissingPiecesAndBadReferences()
        {
            Assert.Throws<StarkFormatException>(() => StarkPackageLoader.Load(new byte[] { 1, 2, 3 }));
            Assert.Throws<StarkFormatException>(() => StarkPackageLoader.Load(TestData.MakePackage(TestData.Manifest, TestData.TwoSteps, withGlb: false)));
            Assert.Throws<StarkFormatException>(() => StarkPackageLoader.Load(TestData.MakePackage(null, TestData.TwoSteps)));
            var unknown = TestData.TwoSteps.Replace("\"parts\": [\"B\"]", "\"parts\": [\"ZZZ\"]");
            Assert.Throws<StarkFormatException>(() => StarkPackageLoader.Load(TestData.MakePackage(TestData.Manifest, unknown)));
            var dup = TestData.TwoSteps.Replace("\"parts\": [\"B\"]", "\"parts\": [\"A\"]");
            Assert.Throws<StarkFormatException>(() => StarkPackageLoader.Load(TestData.MakePackage(TestData.Manifest, dup)));
        }
    }

    public class StepSequencerTests
    {
        StarkPackage Pkg() => StarkPackageLoader.Load(TestData.MakePackage(TestData.Manifest, TestData.TwoSteps));

        [Test]
        public void StatesFollowCurrentStep()
        {
            var seq = new StepSequencer(Pkg());
            Assert.AreEqual(0, seq.CurrentIndex);
            Assert.AreEqual(PartState.Current, seq.StateOf("A"));
            Assert.AreEqual(PartState.Future, seq.StateOf("B"));
            Assert.AreEqual(PartState.Future, seq.StateOf("F1"));

            Assert.IsTrue(seq.Next());
            Assert.AreEqual(PartState.Done, seq.StateOf("A"));
            Assert.AreEqual(PartState.Current, seq.StateOf("B"));
            Assert.AreEqual(PartState.CurrentFastener, seq.StateOf("F2"));

            Assert.IsTrue(seq.Next());
            Assert.IsTrue(seq.IsFinished);
            Assert.AreEqual(PartState.Done, seq.StateOf("F1"));
            Assert.IsFalse(seq.Next(), "после конца дальше некуда");
            Assert.AreEqual(1f, seq.Progress);
        }

        [Test]
        public void PrevAndEvents()
        {
            var seq = new StepSequencer(Pkg());
            int events = 0;
            StepChange last = default;
            seq.StepChanged += c => { events++; last = c; };
            Assert.IsFalse(seq.Prev(), "до первого шага назад нельзя");
            seq.Next();
            seq.Prev();
            Assert.AreEqual(2, events);
            Assert.AreEqual(StepChangeReason.Prev, last.Reason);
            Assert.AreEqual(0, last.To);
            Assert.AreEqual(PartState.Done, seq.StateOf("unknown-part"), "деталь без шага показываем как готовую");
        }
    }

    public class SessionLogTests
    {
        [Test]
        public void CountsTimePerStepAndBacktracks()
        {
            double now = 100;
            var log = new SessionLog("gearbox", () => now, new System.DateTime(2026, 11, 26, 10, 0, 0, System.DateTimeKind.Utc));
            log.EnterStep(0, StepChangeReason.Jump);
            now += 30;
            log.EnterStep(1, StepChangeReason.Next);
            now += 10;
            log.EnterStep(0, StepChangeReason.Prev);
            now += 5;
            log.EnterStep(1, StepChangeReason.Next);
            now += 20;
            log.Finish();

            Assert.AreEqual(35, log.SecondsOnStep(0), 1e-9);
            Assert.AreEqual(30, log.SecondsOnStep(1), 1e-9);
            Assert.AreEqual(1, log.BackCount);

            var json = (System.Collections.Generic.Dictionary<string, object>)MiniJson.Parse(log.ToJson());
            Assert.AreEqual("gearbox", json["productId"]);
            Assert.AreEqual(65.0, json["totalSeconds"]);
            Assert.AreEqual(true, json["finished"]);
            Assert.AreEqual("2026-11-26T10:00:00Z", json["startedAt"]);
        }
    }
}
