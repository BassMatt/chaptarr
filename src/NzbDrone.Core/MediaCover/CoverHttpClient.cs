using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaCover
{
    /// <summary>
    /// HTTP client for remote cover images. Direct connections to private or local addresses are refused;
    /// requests through a proxy get a best-effort check, because the proxy resolves the name.
    /// </summary>
    public interface ICoverHttpClient : IHttpClient
    {
    }

    public class CoverHttpClient : NzbDrone.Common.Http.HttpClient, ICoverHttpClient
    {
        private const int MaxRedirects = 5;
        private static readonly TimeSpan ProxiedLookupTimeout = TimeSpan.FromSeconds(5);

        private readonly IHttpProxySettingsProvider _proxySettingsProvider;
        private readonly IConfigService _configService;

        public CoverHttpClient(IEnumerable<IHttpRequestInterceptor> requestInterceptors,
            ICacheManager cacheManager,
            IRateLimitService rateLimitService,
            IHttpProxySettingsProvider proxySettingsProvider,
            ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            IUserAgentBuilder userAgentBuilder,
            IConfigService configService,
            Logger logger)
            : base(requestInterceptors,
                cacheManager,
                rateLimitService,
                CreateDispatcher(proxySettingsProvider, createManagedWebProxy, certificateValidationService, userAgentBuilder, configService),
                logger)
        {
            _proxySettingsProvider = proxySettingsProvider;
            _configService = configService;
        }

        public override async Task<HttpResponse> ExecuteAsync(HttpRequest request)
        {
            var followRedirects = request.AllowAutoRedirect;
            request.AllowAutoRedirect = false;

            for (var redirects = 0; ; redirects++)
            {
                await EnsureProxiedTargetAllowedAsync(request.Url).ConfigureAwait(false);

                var response = await SendAsync(request).ConfigureAwait(false);

                if (!followRedirects || !response.HasHttpRedirect)
                {
                    return response;
                }

                if (redirects == MaxRedirects)
                {
                    throw new WebException($"Too many redirects while downloading image from '{request.GetLogUrl()}'.", WebExceptionStatus.ProtocolError);
                }

                var location = response.Headers.GetSingleValue("Location");
                if (location.IsNullOrWhiteSpace())
                {
                    throw new WebException($"Redirect response from {request.GetLogUrl()} is missing a Location header.", WebExceptionStatus.ProtocolError);
                }

                request.Url += new HttpUri(location);
            }
        }

        private async Task<HttpResponse> SendAsync(HttpRequest request)
        {
            try
            {
                return await base.ExecuteAsync(request).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (ex.InnerException is WebException rejected)
                {
                    throw new WebException(rejected.Message, ex, rejected.Status, null);
                }

                var status = IsNameResolutionFailure(ex) ? WebExceptionStatus.NameResolutionFailure : WebExceptionStatus.ConnectFailure;
                throw new WebException(ex.Message, ex, status, null);
            }
        }

        private static bool IsNameResolutionFailure(HttpRequestException ex)
        {
            return ex.HttpRequestError == HttpRequestError.NameResolutionError ||
                   ex.InnerException is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData };
        }

        private async Task EnsureProxiedTargetAllowedAsync(HttpUri url)
        {
            if (!MayUseProxy(url))
            {
                return;
            }

            // The proxy resolves the name and its connection is not checked against the target, so this
            // best-effort local lookup is the only private-network check available for proxied requests.
            var host = url.Host?.Trim('[', ']');
            IPAddress[] addresses;
            try
            {
                using var timeout = new CancellationTokenSource(ProxiedLookupTimeout);
                addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
            {
                return;
            }

            if (addresses.Any(address => !MediaCoverUrlSafetyPolicy.IsAllowedConnectTarget(host, address, _configService)))
            {
                throw new WebException($"Request target '{url.Host}' resolved to a disallowed address.", WebExceptionStatus.ProtocolError);
            }
        }

        private bool MayUseProxy(HttpUri url)
        {
            var proxySettings = _proxySettingsProvider.GetProxySettings(url);
            if (proxySettings != null)
            {
                return !proxySettings.IsDirectConnection;
            }

            var uri = (Uri)url;
            var systemProxy = System.Net.Http.HttpClient.DefaultProxy;
            return systemProxy != null && !systemProxy.IsBypassed(uri) && systemProxy.GetProxy(uri) != null;
        }

        private static ManagedHttpDispatcher CreateDispatcher(IHttpProxySettingsProvider proxySettingsProvider,
            ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            IUserAgentBuilder userAgentBuilder,
            IConfigService configService)
        {
            var socketsHttpHandlerFactory = new ManagedSocketsHttpHandlerFactory(createManagedWebProxy,
                certificateValidationService,
                LogManager.GetLogger(nameof(ManagedSocketsHttpHandlerFactory)))
            {
                AllowConnectTarget = (host, address) => MediaCoverUrlSafetyPolicy.IsAllowedConnectTarget(host, address, configService)
            };

            // The dispatcher caches its clients in the cache manager under a fixed name, so a shared
            // cache manager would hand this dispatcher the general, unfiltered clients.
            return new ManagedHttpDispatcher(proxySettingsProvider,
                socketsHttpHandlerFactory,
                userAgentBuilder,
                new CacheManager(),
                LogManager.GetLogger("CoverHttpDispatcher"));
        }
    }
}
