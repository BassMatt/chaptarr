using System.Net;
using System.Net.Sockets;

namespace NzbDrone.Common.Http
{
    public static class PrivateNetworkTargetPolicy
    {
        public static bool IsPrivateOrLocalAddress(IPAddress ip)
        {
            if (ip == null)
            {
                return true;
            }

            if (ip.IsIPv4MappedToIPv6)
            {
                ip = ip.MapToIPv4();
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return IsPrivateOrLocalIPv4(ip.GetAddressBytes());
            }

            if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return true;
            }

            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return true;
            }

            var bytes = ip.GetAddressBytes();

            // Unique local addresses (fc00::/7)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            // NAT64 (64:ff9b::/96) and IPv4-compatible (::/96) addresses reach the embedded IPv4 address.
            if (HasPrefix(bytes, 0x00, 0x64, 0xFF, 0x9B, 0, 0, 0, 0, 0, 0, 0, 0) || HasPrefix(bytes, new byte[12]))
            {
                return IsPrivateOrLocalIPv4(new[] { bytes[12], bytes[13], bytes[14], bytes[15] });
            }

            // Local-use NAT64 (64:ff9b:1::/48)
            return HasPrefix(bytes, 0x00, 0x64, 0xFF, 0x9B, 0x00, 0x01);
        }

        private static bool IsPrivateOrLocalIPv4(byte[] bytes)
        {
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   bytes[0] >= 224;
        }

        private static bool HasPrefix(byte[] bytes, params byte[] prefix)
        {
            for (var i = 0; i < prefix.Length; i++)
            {
                if (bytes[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
