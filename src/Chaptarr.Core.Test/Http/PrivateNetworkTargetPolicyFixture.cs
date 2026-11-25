using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Http
{
    [TestFixture]
    public class PrivateNetworkTargetPolicyFixture
    {
        [TestCase("0.0.0.0")]
        [TestCase("10.1.2.3")]
        [TestCase("100.64.0.1")]
        [TestCase("127.0.0.1")]
        [TestCase("127.255.255.254")]
        [TestCase("169.254.169.254")]
        [TestCase("172.16.0.1")]
        [TestCase("172.31.255.255")]
        [TestCase("192.168.0.1")]
        [TestCase("224.0.0.1")]
        [TestCase("255.255.255.255")]
        [TestCase("::")]
        [TestCase("::1")]
        [TestCase("fe80::1")]
        [TestCase("fec0::1")]
        [TestCase("fc00::1")]
        [TestCase("fd12:3456::1")]
        [TestCase("ff02::1")]
        [TestCase("::ffff:127.0.0.1")]
        [TestCase("::ffff:10.0.0.1")]
        [TestCase("::127.0.0.1")]
        [TestCase("64:ff9b::7f00:1")]
        [TestCase("64:ff9b::c0a8:101")]
        [TestCase("64:ff9b:1::1")]
        public void private_and_local_addresses_are_rejected(string address)
        {
            Assert.That(PrivateNetworkTargetPolicy.IsPrivateOrLocalAddress(IPAddress.Parse(address)), Is.True);
        }

        [TestCase("1.1.1.1")]
        [TestCase("8.8.8.8")]
        [TestCase("172.32.0.1")]
        [TestCase("100.128.0.1")]
        [TestCase("2606:4700:4700::1111")]
        [TestCase("::ffff:8.8.8.8")]
        [TestCase("64:ff9b::808:808")]
        public void public_addresses_are_allowed(string address)
        {
            Assert.That(PrivateNetworkTargetPolicy.IsPrivateOrLocalAddress(IPAddress.Parse(address)), Is.False);
        }
    }
}
