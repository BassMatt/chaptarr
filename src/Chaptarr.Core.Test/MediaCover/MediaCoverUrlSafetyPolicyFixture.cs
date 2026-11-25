using System.Diagnostics;
using System.Net;
using Chaptarr.Core.Test.Common;
using NUnit.Framework;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class MediaCoverUrlSafetyPolicyFixture
    {
        [Test]
        public void public_hostname_is_accepted_without_a_dns_lookup()
        {
            var watch = Stopwatch.StartNew();

            var safe = MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl("https://cover.invalid/image.jpg", null, out var url, out var reason);

            Assert.That(safe, Is.True, "a name that can never resolve must not be treated as unsafe");
            Assert.That(url, Is.EqualTo("https://cover.invalid/image.jpg"));
            Assert.That(reason, Is.Null);
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(500));
        }

        [TestCase("http://127.0.0.1/cover.jpg", "private_or_local_address")]
        [TestCase("http://2130706433/cover.jpg", "private_or_local_address")]
        [TestCase("http://0x7f000001/cover.jpg", "private_or_local_address")]
        [TestCase("http://10.0.0.1/cover.jpg", "private_or_local_address")]
        [TestCase("http://192.168.1.1/cover.jpg", "private_or_local_address")]
        [TestCase("http://169.254.169.254/cover.jpg", "private_or_local_address")]
        [TestCase("http://[::1]/cover.jpg", "private_or_local_address")]
        [TestCase("http://[::ffff:127.0.0.1]/cover.jpg", "private_or_local_address")]
        [TestCase("http://[::ffff:10.0.0.1]/cover.jpg", "private_or_local_address")]
        [TestCase("http://[64:ff9b::a00:1]/cover.jpg", "private_or_local_address")]
        [TestCase("http://[fe80::1]/cover.jpg", "private_or_local_address")]
        [TestCase("http://[fd00::1]/cover.jpg", "private_or_local_address")]
        [TestCase("http://localhost/cover.jpg", "private_or_local_address")]
        [TestCase("http://LOCALHOST./cover.jpg", "private_or_local_address")]
        [TestCase("http://images.localhost/cover.jpg", "private_or_local_address")]
        [TestCase("http://metadata.google.internal/cover.jpg", "cloud_metadata_host")]
        [TestCase("http://metadata.google.internal./cover.jpg", "cloud_metadata_host")]
        [TestCase("http://user:pass@cover.example/cover.jpg", "userinfo_not_allowed")]
        [TestCase("ftp://cover.example/cover.jpg", "invalid_scheme")]
        [TestCase("file:///etc/passwd", "invalid_scheme")]
        [TestCase("not a url", "invalid_url")]
        [TestCase("", "empty_url")]
        public void rejects_urls_that_are_unsafe_without_resolving_them(string url, string expectedReason)
        {
            Assert.That(MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(url, null, out var safeUrl, out var reason), Is.False);
            Assert.That(safeUrl, Is.Null);
            Assert.That(reason, Is.EqualTo(expectedReason));
        }

        [TestCase("http://192.168.1.20/cover.jpg", "http://192.168.1.20:8080")]
        [TestCase("http://[fd00::5]/cover.jpg", "http://[fd00::5]:8080")]
        [TestCase("http://localhost/cover.jpg", "http://127.0.0.1:8080")]
        [TestCase("http://127.0.0.1/cover.jpg", "http://localhost:8080")]
        public void configured_metadata_host_stays_trusted(string url, string metadataServer)
        {
            Assert.That(MediaCoverUrlSafetyPolicy.TryGetSafeImageUrl(url, Config(metadataServer), out _, out var reason), Is.True, reason);
        }

        [TestCase("cover.example", "10.0.0.5", null, false)]
        [TestCase("cover.example", "127.0.0.1", null, false)]
        [TestCase("cover.example", "::ffff:192.168.0.1", null, false)]
        [TestCase("cover.example", "1.1.1.1", null, true)]
        [TestCase("localhost", "127.0.0.1", null, false)]
        [TestCase("192.168.1.20", "192.168.1.20", "http://192.168.1.20:8080", true)]
        [TestCase("fd00::5", "fd00::5", "http://[fd00::5]:8080", true)]
        [TestCase("cover.example", "192.168.1.20", "http://192.168.1.20:8080", false)]
        public void connect_targets_follow_the_private_network_rule(string host, string address, string metadataServer, bool allowed)
        {
            Assert.That(MediaCoverUrlSafetyPolicy.IsAllowedConnectTarget(host, IPAddress.Parse(address), Config(metadataServer)), Is.EqualTo(allowed));
        }

        private static IConfigService Config(string metadataServer)
        {
            return InterfaceProxy.Proxy<IConfigService>((method, _) =>
                method.Name == "get_MetadataServerUrl" ? metadataServer : throw new AssertionException(method.Name));
        }
    }
}
