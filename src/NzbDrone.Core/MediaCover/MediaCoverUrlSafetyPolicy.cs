using System;
using System.Net;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaCover
{
    internal static class MediaCoverUrlSafetyPolicy
    {
        internal static bool TryGetSafeImageUrl(string url, IConfigService configService, out string safeUrl, out string reason)
        {
            safeUrl = null;
            reason = null;

            if (string.IsNullOrWhiteSpace(url))
            {
                reason = "empty_url";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                reason = "invalid_url";
                return false;
            }

            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                reason = "invalid_scheme";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                reason = "userinfo_not_allowed";
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host))
            {
                reason = "missing_host";
                return false;
            }

            if (!IsTrustedCoverHost(uri.Host, configService) && IsSyntacticallyPrivateOrLocalHost(uri.Host, out var privateReason))
            {
                reason = privateReason;
                return false;
            }

            safeUrl = uri.ToString();
            return true;
        }

        internal static bool IsAllowedConnectTarget(string host, IPAddress address, IConfigService configService)
        {
            return IsTrustedCoverHost(host, configService) || !PrivateNetworkTargetPolicy.IsPrivateOrLocalAddress(address);
        }

        private static bool IsTrustedCoverHost(string host, IConfigService configService)
        {
            var metadataHost = GetMetadataServerHost(configService);
            if (string.IsNullOrWhiteSpace(metadataHost) || string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            if (host.Trim('[', ']').Equals(metadataHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // If the user configured the metadata server on loopback, allow loopback cover URLs as well.
            return IsLoopbackHost(metadataHost) && IsLoopbackHost(host);
        }

        private static string GetMetadataServerHost(IConfigService configService)
        {
            try
            {
                var url = configService?.MetadataServerUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }

                return new Uri(url).Host;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            host = host.Trim().Trim('[', ']');

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        }

        private static bool IsSyntacticallyPrivateOrLocalHost(string host, out string reason)
        {
            reason = null;
            host = host.Trim().Trim('[', ']').TrimEnd('.');

            if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            {
                reason = "cloud_metadata_host";
                return true;
            }

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(host, out var ip) && PrivateNetworkTargetPolicy.IsPrivateOrLocalAddress(ip)))
            {
                reason = "private_or_local_address";
                return true;
            }

            return false;
        }
    }
}
