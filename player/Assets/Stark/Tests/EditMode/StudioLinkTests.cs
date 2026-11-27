using System;
using System.Text;
using NUnit.Framework;
using Stark.Core.Link;

namespace Stark.Core.Tests
{
    public class StudioLinkTests
    {
        const string Token = "Abc_def-123456789XYZ_qw";

        [Test]
        public void ParsesDeepLinkFromStudioQr()
        {
            // Ровно такую ссылку строит stark_studio.link.LinkHub.deep_link
            Assert.IsTrue(StudioLink.TryParse("stark://link?u=http%3A%2F%2F192.168.1.10%3A8041&t=" + Token, out var p));
            Assert.AreEqual("http://192.168.1.10:8041", p.BaseUrl);
            Assert.AreEqual(Token, p.Token);
            Assert.AreEqual("http://192.168.1.10:8041/api/v1/current", p.CurrentUrl);
            Assert.AreEqual("http://192.168.1.10:8041/api/v1/current.stark", p.PackageUrl);
            Assert.AreEqual("192.168.1.10:8041", p.Host);
        }

        [Test]
        public void ParsesPairPageUrlForManualEntry()
        {
            Assert.IsTrue(StudioLink.TryParse("http://10.0.0.5:8041/s/" + Token, out var p));
            Assert.AreEqual("http://10.0.0.5:8041", p.BaseUrl);
            Assert.AreEqual(Token, p.Token);
        }

        [TestCase("")]
        [TestCase("hello")]
        [TestCase("stark://link?u=http%3A%2F%2F1.2.3.4%3A8041")]                       // нет токена
        [TestCase("stark://link?t=" + Token)]                                          // нет адреса
        [TestCase("stark://other?u=http%3A%2F%2F1.2.3.4%3A8041&t=" + Token)]           // не та команда
        [TestCase("stark://link?u=file%3A%2F%2F%2Fsdcard&t=" + Token)]                 // не http
        [TestCase("stark://link?u=http%3A%2F%2F1.2.3.4%3A8041%2Fevil&t=" + Token)]     // путь в адресе
        [TestCase("stark://link?u=http%3A%2F%2F1.2.3.4%3A8041&t=short")]               // слабый токен
        [TestCase("stark://link?u=http%3A%2F%2F1.2.3.4%3A8041&t=bad%20token%20with%20spaces")]
        [TestCase("https://example.com/other/" + Token)]
        public void RejectsBadLinks(string link)
        {
            Assert.IsFalse(StudioLink.TryParse(link, out var p));
            Assert.IsNull(p);
        }

        [Test]
        public void ParsesCurrentPackageAnswer()
        {
            var sha = StudioLink.Sha256Hex(Encoding.UTF8.GetBytes("x"));
            var json = "{\"app\":\"stark-studio\",\"protocol\":1,\"package\":{\"id\":\"gearbox-1a2b3c\",\"name\":\"Редуктор\"," +
                       "\"version\":3,\"sha256\":\"" + sha.ToUpperInvariant() + "\",\"size\":31000,\"steps\":8,\"sentAt\":\"2026-11-27T10:00:00\"}}";
            var r = StudioLink.ParseCurrent(json);
            Assert.AreEqual("gearbox-1a2b3c", r.Id);
            Assert.AreEqual("Редуктор", r.Name);
            Assert.AreEqual(3, r.Version);
            Assert.AreEqual(sha, r.Sha256); // нормализуется в нижний регистр
            Assert.AreEqual(31000, r.Size);
            Assert.AreEqual(8, r.Steps);
            Assert.AreEqual("gearbox-1a2b3c.stark", StudioLink.FileNameFor(r));
        }

        [Test]
        public void NoPackageYetIsNull()
        {
            Assert.IsNull(StudioLink.ParseCurrent("{\"app\":\"stark-studio\",\"protocol\":1,\"package\":null}"));
        }

        [Test]
        public void RejectsNewerProtocolAndBrokenAnswers()
        {
            Assert.Throws<FormatException>(() => StudioLink.ParseCurrent("{\"protocol\":2,\"package\":null}"));
            Assert.Throws<FormatException>(() => StudioLink.ParseCurrent("[]"));
            Assert.Throws<FormatException>(() => StudioLink.ParseCurrent("{\"package\":{\"id\":\"a\",\"sha256\":\"zz\",\"size\":10}}"));
            var sha = new string('a', 64);
            Assert.Throws<FormatException>(() => StudioLink.ParseCurrent("{\"package\":{\"id\":\"a\",\"sha256\":\"" + sha + "\",\"size\":0}}"));
        }

        [Test]
        public void DetectsNewVersionAndVerifiesDownload()
        {
            var data = Encoding.UTF8.GetBytes("package-bytes");
            var remote = new RemotePackage { Id = "p", Sha256 = StudioLink.Sha256Hex(data), Size = data.Length };
            Assert.IsTrue(StudioLink.IsNew(remote, null));
            Assert.IsFalse(StudioLink.IsNew(remote, remote.Sha256.ToUpperInvariant()));
            Assert.IsFalse(StudioLink.IsNew(null, "x"));
            Assert.DoesNotThrow(() => StudioLink.Verify(remote, data));
            Assert.Throws<FormatException>(() => StudioLink.Verify(remote, Encoding.UTF8.GetBytes("package-byteZ")));
            Assert.Throws<FormatException>(() => StudioLink.Verify(remote, new byte[0]));
        }

        [Test]
        public void SafeFileNames()
        {
            Assert.AreEqual("a-b-c.stark", StudioLink.FileNameFor(new RemotePackage { Id = "../a/b\\c" }));
            Assert.AreEqual("редуктор-1a2b.stark", StudioLink.FileNameFor(new RemotePackage { Id = "редуктор-1a2b" }));
            Assert.AreEqual("studio.stark", StudioLink.FileNameFor(new RemotePackage { Id = "///" }));
        }

        [Test]
        public void PollBacksOffOnErrors()
        {
            Assert.AreEqual(3f, StudioLink.PollDelaySeconds(0));
            Assert.AreEqual(6f, StudioLink.PollDelaySeconds(1));
            Assert.AreEqual(30f, StudioLink.PollDelaySeconds(10));
        }
    }
}
