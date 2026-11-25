using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Common;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Common.TPL;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaCover;
using NzbDrone.Host;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class CoverHttpClientFixture
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        [Test]
        public void general_client_still_reaches_loopback()
        {
            using var server = new LocalHttpServer(_ => Ok("cover"));
            var cacheManager = new CacheManager();
            var dispatcher = new ManagedHttpDispatcher(ProxySettings(null),
                new ManagedSocketsHttpHandlerFactory(new ManagedWebProxyFactory(cacheManager), Certificates(), Logger),
                UserAgent(), cacheManager, Logger);
            var client = new NzbDrone.Common.Http.HttpClient(Array.Empty<IHttpRequestInterceptor>(), cacheManager,
                new RateLimitService(cacheManager, Logger), dispatcher, Logger);

            var response = client.Get(Request($"http://127.0.0.1:{server.Port}/image"));

            Assert.That(response.Content, Is.EqualTo("cover"));
        }

        [TestCase("http://127.0.0.1:{0}/image")]
        [TestCase("http://localhost:{0}/image")]
        [TestCase("http://2130706433:{0}/image")]
        [TestCase("http://[::1]:{0}/image")]
        [TestCase("http://[::ffff:7f00:1]:{0}/image")]
        public void rejects_private_and_loopback_targets_before_connecting(string url)
        {
            using var server = new LocalHttpServer(_ => Ok("cover"));
            var client = CreateClient();

            var ex = Assert.Throws<WebException>(() => client.Get(Request(string.Format(url, server.Port))));

            Assert.That(ex.Status, Is.EqualTo(WebExceptionStatus.ProtocolError));
            Assert.That(ex.Message, Does.Contain("disallowed address"));
            Assert.That(server.RequestLines, Is.Empty);
        }

        [Test]
        public void rejects_redirect_to_private_address_at_connect_time()
        {
            using var server = new LocalHttpServer(_ => Redirect("http://10.255.255.1:9/image"));
            var client = CreateClient(metadataServer: $"http://127.0.0.1:{server.Port}");
            var request = Request($"http://127.0.0.1:{server.Port}/image");
            request.AllowAutoRedirect = true;

            var ex = Assert.Throws<WebException>(() => client.Get(request));

            Assert.That(ex.Status, Is.EqualTo(WebExceptionStatus.ProtocolError));
            Assert.That(ex.Message, Does.Contain("disallowed address"));
            Assert.That(server.RequestLines, Has.Count.EqualTo(1));
        }

        [Test]
        public void follows_redirects_itself_and_stops_after_the_limit()
        {
            LocalHttpServer server = null;
            using (server = new LocalHttpServer(_ => Redirect($"http://127.0.0.1:{server.Port}/again")))
            {
                var client = CreateClient(metadataServer: $"http://127.0.0.1:{server.Port}");
                var request = Request($"http://127.0.0.1:{server.Port}/image");
                request.AllowAutoRedirect = true;

                var ex = Assert.Throws<WebException>(() => client.Get(request));

                Assert.That(ex.Message, Does.Contain("Too many redirects"));
                Assert.That(server.RequestLines, Has.Count.EqualTo(6));
            }
        }

        [Test]
        public void configured_metadata_host_on_loopback_is_still_reachable()
        {
            using var server = new LocalHttpServer(_ => Ok("cover"));
            var client = CreateClient(metadataServer: $"http://127.0.0.1:{server.Port}");

            var response = client.Get(Request($"http://127.0.0.1:{server.Port}/image"));

            Assert.That(response.Content, Is.EqualTo("cover"));
        }

        [Test]
        public void dns_failure_is_an_ordinary_retryable_network_error()
        {
            var client = CreateClient();

            var ex = Assert.Throws<WebException>(() => client.Get(Request("http://cover.invalid/image")));

            Assert.That(ex.Status, Is.EqualTo(WebExceptionStatus.NameResolutionFailure));
        }

        [Test]
        public void proxied_request_connects_to_a_local_proxy_and_lets_it_resolve_the_target()
        {
            using var proxy = new LocalHttpServer(_ => Ok("proxied"));
            var client = CreateClient(proxy: new HttpProxySettings(ProxyType.Http, "127.0.0.1", proxy.Port, string.Empty, false));

            var response = client.Get(Request("http://cover.invalid/image"));

            Assert.That(response.Content, Is.EqualTo("proxied"));
            Assert.That(proxy.RequestLines.Single(), Does.StartWith("GET http://cover.invalid/image"));
        }

        [Test]
        public void proxied_request_to_a_name_that_resolves_locally_to_loopback_is_rejected()
        {
            using var proxy = new LocalHttpServer(_ => Ok("proxied"));
            var client = CreateClient(proxy: new HttpProxySettings(ProxyType.Http, "127.0.0.1", proxy.Port, string.Empty, false));

            var ex = Assert.Throws<WebException>(() => client.Get(Request("http://localhost:9/image")));

            Assert.That(ex.Message, Does.Contain("disallowed address"));
            Assert.That(proxy.RequestLines, Is.Empty);
        }

        [Test]
        public void https_request_through_a_local_http_proxy_opens_a_tunnel_to_the_proxy()
        {
            using var proxy = new LocalHttpServer(_ => "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            var client = CreateClient(proxy: new HttpProxySettings(ProxyType.Http, "127.0.0.1", proxy.Port, string.Empty, false));

            var ex = Assert.Throws<WebException>(() => client.Get(Request("https://cover.invalid/image")));

            Assert.That(ex.Message, Does.Not.Contain("disallowed address"));
            Assert.That(proxy.RequestLines.Single(), Does.StartWith("CONNECT cover.invalid:443"));
        }

        [Test]
        public void proxy_host_written_in_a_different_form_is_still_recognised_as_the_proxy()
        {
            using var proxy = new LocalHttpServer(_ => Ok("proxied"));
            var client = CreateClient(proxy: new HttpProxySettings(ProxyType.Http, "127.000.000.001", proxy.Port, string.Empty, false));

            var response = client.Get(Request("http://cover.invalid/image"));

            Assert.That(response.Content, Is.EqualTo("proxied"));
        }

        [Test]
        [NonParallelizable]
        public void system_proxy_on_a_private_address_is_reachable()
        {
            using var proxy = new LocalHttpServer(_ => Ok("proxied"));
            WithSystemProxy(proxy.Port, () =>
            {
                var response = CreateClient().Get(Request("http://cover.invalid/image"));

                Assert.That(response.Content, Is.EqualTo("proxied"));
            });
        }

        [Test]
        [NonParallelizable]
        public void request_through_the_system_proxy_still_checks_the_target()
        {
            using var proxy = new LocalHttpServer(_ => Ok("proxied"));
            WithSystemProxy(proxy.Port, () =>
            {
                var ex = Assert.Throws<WebException>(() => CreateClient().Get(Request("http://10.1.2.3:9/image")));

                Assert.That(ex.Message, Does.Contain("disallowed address"));
                Assert.That(proxy.RequestLines, Is.Empty);
            });
        }

        [Test]
        public void cover_client_does_not_replace_the_general_http_registrations()
        {
            using var container = new Container(rules => rules.WithNzbDroneRules());
            container.AutoAddServices(Bootstrap.ASSEMBLIES).RegisterHttpClients();

            Type ImplementationOf<T>() => container.GetServiceRegistrations()
                .Where(registration => registration.ServiceType == typeof(T))
                .Select(registration => registration.ImplementationType)
                .Last();

            Assert.That(ImplementationOf<IHttpClient>(), Is.EqualTo(typeof(NzbDrone.Common.Http.HttpClient)));
            Assert.That(ImplementationOf<ICoverHttpClient>(), Is.EqualTo(typeof(CoverHttpClient)));
            Assert.That(ImplementationOf<ICreateManagedSocketsHttpHandler>(), Is.EqualTo(typeof(ManagedSocketsHttpHandlerFactory)));
        }

        private static CoverHttpClient CreateClient(string metadataServer = null, HttpProxySettings proxy = null)
        {
            var cacheManager = new CacheManager();
            var config = InterfaceProxy.Proxy<IConfigService>((method, _) =>
                method.Name == "get_MetadataServerUrl" ? metadataServer : throw new AssertionException(method.Name));

            return new CoverHttpClient(Array.Empty<IHttpRequestInterceptor>(),
                cacheManager,
                new RateLimitService(cacheManager, Logger),
                ProxySettings(proxy),
                new ManagedWebProxyFactory(cacheManager),
                Certificates(),
                UserAgent(),
                config,
                Logger);
        }

        private static void WithSystemProxy(int port, Action test)
        {
            var original = System.Net.Http.HttpClient.DefaultProxy;
            System.Net.Http.HttpClient.DefaultProxy = new WebProxy($"http://127.0.0.1:{port}");
            try
            {
                test();
            }
            finally
            {
                System.Net.Http.HttpClient.DefaultProxy = original;
            }
        }

        private static IHttpProxySettingsProvider ProxySettings(HttpProxySettings proxy) =>
            InterfaceProxy.Proxy<IHttpProxySettingsProvider>((_, _) => proxy);

        private static ICertificateValidationService Certificates() =>
            InterfaceProxy.Proxy<ICertificateValidationService>((_, _) => false);

        private static IUserAgentBuilder UserAgent() =>
            InterfaceProxy.Proxy<IUserAgentBuilder>((_, _) => "Chaptarr.Test");

        private static HttpRequest Request(string url) => new HttpRequest(url)
        {
            AllowAutoRedirect = false,
            RequestTimeout = TimeSpan.FromSeconds(10)
        };

        private static string Ok(string body) =>
            $"HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";

        private static string Redirect(string location) =>
            $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

        private sealed class LocalHttpServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Func<string, string> _respond;

            public LocalHttpServer(Func<string, string> respond)
            {
                _respond = respond;
                _listener.Start();
                _ = AcceptAsync();
            }

            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

            public ConcurrentQueue<string> RequestLines { get; } = new ConcurrentQueue<string>();

            public void Dispose()
            {
                _stop.Cancel();
                _listener.Stop();
            }

            private async Task AcceptAsync()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    }
                    catch
                    {
                        return;
                    }

                    _ = Task.Run(() => ServeAsync(client));
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var requestLine = await reader.ReadLineAsync();
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                    {
                    }

                    RequestLines.Enqueue(requestLine);
                    var response = Encoding.ASCII.GetBytes(_respond(requestLine));
                    await stream.WriteAsync(response);
                    await stream.FlushAsync();
                }
            }
        }
    }
}
