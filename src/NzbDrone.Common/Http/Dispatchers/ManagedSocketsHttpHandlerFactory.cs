using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http.Proxy;

namespace NzbDrone.Common.Http.Dispatchers
{
    public interface ICreateManagedSocketsHttpHandler
    {
        SocketsHttpHandler CreateHandler(HttpProxySettings proxySettings,
            bool allowAutoRedirect,
            bool useCookies,
            ICredentials credentials,
            bool preAuthenticate,
            int maxConnectionsPerServer);
    }

    public class ManagedSocketsHttpHandlerFactory : ICreateManagedSocketsHttpHandler
    {
        private const int ConnectionEstablishTimeoutMs = 2000;
        private static bool _useIPv6 = Socket.OSSupportsIPv6;
        private static bool _hasResolvedIPv6Availability;

        private readonly ICreateManagedWebProxy _createManagedWebProxy;
        private readonly ICertificateValidationService _certificateValidationService;
        private readonly Logger _logger;

        public ManagedSocketsHttpHandlerFactory(ICreateManagedWebProxy createManagedWebProxy,
            ICertificateValidationService certificateValidationService,
            Logger logger)
        {
            _createManagedWebProxy = createManagedWebProxy;
            _certificateValidationService = certificateValidationService;
            _logger = logger;
        }

        public Func<string, IPAddress, bool> AllowConnectTarget { get; init; }

        public SocketsHttpHandler CreateHandler(HttpProxySettings proxySettings,
            bool allowAutoRedirect,
            bool useCookies,
            ICredentials credentials,
            bool preAuthenticate,
            int maxConnectionsPerServer)
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
                UseProxy = proxySettings?.IsDirectConnection != true,
                UseCookies = useCookies,
                AllowAutoRedirect = allowAutoRedirect,
                Credentials = credentials,
                PreAuthenticate = preAuthenticate,
                MaxConnectionsPerServer = maxConnectionsPerServer,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = _certificateValidationService.ShouldByPassValidationError
                }
            };

            if (proxySettings != null && !proxySettings.IsDirectConnection)
            {
                handler.Proxy = _createManagedWebProxy.GetWebProxy(proxySettings);
            }

            handler.ConnectCallback = (context, cancellationToken) => OnConnectAsync(context, handler, cancellationToken);

            return handler;
        }

        private bool HasRoutableIPv4Address()
        {
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

                return networkInterfaces.Any(ni =>
                    ni.OperationalStatus == OperationalStatus.Up &&
                    ni.GetIPProperties().UnicastAddresses.Any(ip =>
                        ip.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ip.Address)));
            }
            catch (Exception e)
            {
                _logger.Debug(e, "Caught exception while GetAllNetworkInterfaces assuming IPv4 connectivity: {0}", e.Message);
                return true;
            }
        }

        private async ValueTask<Stream> OnConnectAsync(SocketsHttpConnectionContext context, SocketsHttpHandler handler, CancellationToken cancellationToken)
        {
            var allowTarget = AllowConnectTarget != null && !IsProxyConnection(context, handler) ? AllowConnectTarget : null;

            if (_useIPv6)
            {
                var targetRejected = false;

                try
                {
                    if (!_hasResolvedIPv6Availability)
                    {
                        using var quickFailCts = new CancellationTokenSource(ConnectionEstablishTimeoutMs);
                        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, quickFailCts.Token);
                        return await AttemptConnection(AddressFamily.InterNetworkV6, context, allowTarget, linkedTokenSource.Token);
                    }

                    return await AttemptConnection(AddressFamily.InterNetworkV6, context, allowTarget, cancellationToken);
                }
                catch (WebException)
                {
                    // A rejected target says nothing about whether IPv6 works.
                    targetRejected = true;
                    throw;
                }
                catch
                {
                    var routableIPv4 = HasRoutableIPv4Address();
                    _logger.Info("IPv4 is available: {0}, IPv6 will be {1}", routableIPv4, routableIPv4 ? "disabled" : "left enabled");
                    _useIPv6 = !routableIPv4;
                }
                finally
                {
                    if (!targetRejected)
                    {
                        _hasResolvedIPv6Availability = true;
                    }
                }
            }

            return await AttemptConnection(AddressFamily.InterNetwork, context, allowTarget, cancellationToken);
        }

        private static bool IsProxyConnection(SocketsHttpConnectionContext context, SocketsHttpHandler handler)
        {
            if (!handler.UseProxy)
            {
                return false;
            }

            var request = context.InitialRequestMessage;
            if (request?.Method == HttpMethod.Connect)
            {
                return true;
            }

            var target = request?.RequestUri;
            var proxy = handler.Proxy ?? System.Net.Http.HttpClient.DefaultProxy;
            if (target == null || proxy == null || proxy.IsBypassed(target))
            {
                return false;
            }

            var proxyUri = proxy.GetProxy(target);
            return proxyUri != null &&
                   proxyUri.Port == context.DnsEndPoint.Port &&
                   IsSameHost(proxyUri.IdnHost, context.DnsEndPoint.Host);
        }

        private static bool IsSameHost(string left, string right)
        {
            return string.Equals(left?.Trim('[', ']'), right?.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
        }

        private static async ValueTask<Stream> AttemptConnection(AddressFamily addressFamily,
            SocketsHttpConnectionContext context,
            Func<string, IPAddress, bool> allowTarget,
            CancellationToken cancellationToken)
        {
            EndPoint endpoint = context.DnsEndPoint;

            if (addressFamily == AddressFamily.InterNetwork &&
                TryGetPlexDirectIpAddress(context.DnsEndPoint.Host, out var plexDirectIp))
            {
                endpoint = new IPEndPoint(plexDirectIp, context.DnsEndPoint.Port);
            }

            if (endpoint is IPEndPoint ipEndPoint)
            {
                EnsureAllowedTarget(ipEndPoint.Address, context.DnsEndPoint.Host, allowTarget);
                return await ConnectSocket(addressFamily, ipEndPoint, cancellationToken).ConfigureAwait(false);
            }

            if (endpoint is not DnsEndPoint dnsEndPoint)
            {
                return await ConnectSocket(addressFamily, endpoint, cancellationToken).ConfigureAwait(false);
            }

            // Resolve the hostname ourselves so we can validate the resolved IPs (DNS rebinding defense-in-depth).
            IPAddress[] resolvedAddresses;
            try
            {
                resolvedAddresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            }
            catch when (allowTarget == null)
            {
                // Fall back to letting the socket resolve if DNS resolution fails here.
                return await ConnectSocket(addressFamily, dnsEndPoint, cancellationToken).ConfigureAwait(false);
            }

            if (allowTarget != null)
            {
                foreach (var address in resolvedAddresses)
                {
                    EnsureAllowedTarget(address, dnsEndPoint.Host, allowTarget);
                }
            }

            var candidates = resolvedAddresses.Where(a => a.AddressFamily == addressFamily).ToArray();
            if (candidates.Length == 0)
            {
                if (allowTarget != null)
                {
                    throw new SocketException((int)SocketError.NoData);
                }

                return await ConnectSocket(addressFamily, dnsEndPoint, cancellationToken).ConfigureAwait(false);
            }

            Exception lastException = null;
            foreach (var address in candidates)
            {
                EnsureAllowedTarget(address, dnsEndPoint.Host, allowTarget);

                try
                {
                    return await ConnectSocket(addressFamily, new IPEndPoint(address, dnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            throw lastException ?? new SocketException((int)SocketError.HostUnreachable);
        }

        private static async ValueTask<Stream> ConnectSocket(AddressFamily addressFamily, EndPoint endpoint, CancellationToken cancellationToken)
        {
            var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static void EnsureAllowedTarget(IPAddress ip, string host, Func<string, IPAddress, bool> allowTarget)
        {
            if (CloudMetadataTargetPolicy.IsBlocked(ip) || (allowTarget != null && !allowTarget(host, ip)))
            {
                throw new WebException($"Request target '{host}' resolved to a disallowed address.", WebExceptionStatus.ProtocolError);
            }
        }

        private static bool TryGetPlexDirectIpAddress(string host, out IPAddress ipAddress)
        {
            ipAddress = null;

            if (host.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (!host.EndsWith(".plex.direct", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var firstDot = host.IndexOf('.');
            if (firstDot <= 0)
            {
                return false;
            }

            var ipCandidate = host.Substring(0, firstDot).Replace('-', '.');

            if (!IPAddress.TryParse(ipCandidate, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            ipAddress = parsed;
            return true;
        }
    }
}
