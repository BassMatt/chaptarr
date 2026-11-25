using System;
using System.Net;
using System.Net.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.MetadataSource
{
    [TestFixture]
    public class MetadataServerHealthServiceFixture
    {
        private const string Source = "https://api2.chaptarr.com";

        [Test]
        public void scheduling_check_shares_request_eligibility_without_stealing_recovery()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
            service.TripCircuit(Source, new WebException("unavailable"));
            Assert.That(service.CanBeginRequest(Source, out var wait), Is.False);
            Assert.That(wait.TotalSeconds, Is.InRange(55, 60));
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
            Assert.That(status.ProbeInProgress, Is.False);
            Assert.That(service.CanAttemptWithoutProbe(Source, out _), Is.False, "optional work still cannot probe");
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            Assert.That(service.CanBeginRequest(Source, out _), Is.False);
            Assert.That(service.TryBeginRequest(Source, out _), Is.False);
            status.ProbeStartedAt = DateTime.UtcNow.AddMinutes(-3);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True, "an abandoned probe can recover");
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            service.ReportSuccess(Source);
            Assert.That(service.CanBeginRequest(Source, out _), Is.True);
        }

        [Test]
        public void should_open_short_cooldown_then_allow_single_probe()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);

            service.TripCircuit(Source, new WebException("edge returned 525"));

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));

            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            Assert.That(service.TryBeginRequest(Source, out _), Is.False);
        }

        [Test]
        public void optional_request_check_should_not_claim_half_open_probe()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            Assert.That(service.CanAttemptWithoutProbe(Source, out _), Is.True);

            service.TripCircuit(Source, new WebException("edge returned 525"));
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(service.CanAttemptWithoutProbe(Source, out _), Is.False);
            Assert.That(status.ProbeInProgress, Is.False);
            Assert.That(service.TryBeginRequest(Source, out _), Is.True,
                "the optional request must leave the recovery probe available for load-bearing metadata work");
        }

        [Test]
        public void should_escalate_failed_probe_then_reset_after_success()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.TripCircuit(Source, new WebException("edge returned 525"));
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);

            service.ReportFailure(Source, new WebException("probe failed"));

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(175, 180));

            service.ReportSuccess(Source);

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(0));
            Assert.That(status.EscalationLevel, Is.EqualTo(0));
        }

        [Test]
        public void manual_reset_should_clear_open_circuit_and_allow_immediate_requests()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.TripCircuit(Source, new WebException("edge returned 525"));
            Assert.That(service.TryBeginRequest(Source, out _), Is.False);

            service.Reset(Source);

            var status = service.GetStatus(Source);
            Assert.That(status.IsHealthy, Is.True);
            Assert.That(status.IsRateLimited, Is.False);
            Assert.That(status.ConsecutiveFailures, Is.Zero);
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);

            service.ReportFailure(Source, new WebException("still down"));
            Assert.That(service.TryBeginRequest(Source, out _), Is.True, "a reset must clear the earlier failure streak");

            service.TripCircuit(Source, new WebException("edge returned 525"));
            Assert.That(service.TryBeginRequest(Source, out _), Is.False, "repeated post-reset failures must reopen the circuit");
        }

        [Test]
        public void should_stay_closed_below_failure_threshold()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            for (var i = 1; i < MetadataServerHealthService.FailureThreshold; i++)
            {
                service.ReportFailure(Source, new WebException("Resource temporarily unavailable"));
            }

            var status = service.GetStatus(Source);
            Assert.That(status.IsHealthy, Is.True);
            Assert.That(status.RateLimitedUntil, Is.Null);
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(MetadataServerHealthService.FailureThreshold - 1));
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
            Assert.That(service.CanAttemptWithoutProbe(Source, out _), Is.True);
        }

        [Test]
        public void success_should_reset_failure_streak_before_threshold()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            for (var i = 1; i < MetadataServerHealthService.FailureThreshold; i++)
            {
                service.ReportFailure(Source, new WebException("Resource temporarily unavailable"));
            }

            service.ReportSuccess(Source);
            service.ReportFailure(Source, new WebException("Resource temporarily unavailable"));

            var status = service.GetStatus(Source);
            Assert.That(status.IsHealthy, Is.True);
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(1));
        }

        [Test]
        public void failures_outside_window_should_not_accumulate()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());
            var status = service.GetStatus(Source);

            for (var i = 0; i < MetadataServerHealthService.FailureThreshold * 2; i++)
            {
                service.ReportFailure(Source, new WebException("Resource temporarily unavailable"));
                status.LastFailure = DateTime.UtcNow - MetadataServerHealthService.FailureWindow - TimeSpan.FromSeconds(1);
            }

            Assert.That(status.IsHealthy, Is.True);
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(1));
            Assert.That(service.TryBeginRequest(Source, out _), Is.True);
        }

        [Test]
        public void in_flight_failures_after_circuit_opens_should_not_escalate_backoff()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.TripCircuit(Source, new WebException("edge returned 525"));
            var status = service.GetStatus(Source);
            var openUntil = status.RateLimitedUntil;

            service.ReportFailure(Source, new WebException("in-flight request failed"));
            service.ReportFailure(Source, new WebException("in-flight request failed"));

            Assert.That(status.EscalationLevel, Is.EqualTo(1));
            Assert.That(status.RateLimitedUntil, Is.EqualTo(openUntil));
            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));
        }

        [Test]
        public void late_failure_after_cooldown_expires_should_not_escalate_without_a_probe()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.TripCircuit(Source, new WebException("edge returned 525"));
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            service.ReportFailure(Source, new WebException("old request timed out"));

            Assert.That(status.EscalationLevel, Is.EqualTo(1));
            Assert.That(service.TryBeginRequest(Source, out _), Is.True, "the recovery probe must still be available");

            service.ReportFailure(Source, new WebException("probe failed"));

            Assert.That(status.EscalationLevel, Is.EqualTo(2));
            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(175, 180));
        }

        [Test]
        public void transport_failure_should_not_shorten_rate_limit_window()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source, TimeSpan.FromMinutes(10));
            service.ReportFailure(Source, new WebException("in-flight request failed"));

            var status = service.GetStatus(Source);
            Assert.That(status.IsRateLimited, Is.True);
            Assert.That(status.LastErrorMessage, Is.EqualTo("Rate limited"));
            Assert.That(status.ConsecutiveFailures, Is.Zero);
            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalMinutes, Is.InRange(9.9, 10));
        }

        [Test]
        public void failed_probe_after_rate_limit_should_reopen_circuit()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source);
            var status = service.GetStatus(Source);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(service.TryBeginRequest(Source, out _), Is.True);

            service.ReportFailure(Source, new WebException("probe failed"));

            Assert.That(status.IsHealthy, Is.False);
            Assert.That(status.IsRateLimited, Is.False);
            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));
        }

        [Test]
        public void should_use_short_cooldown_for_rate_limit_without_retry_after()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source);

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter.TotalSeconds, Is.InRange(55, 60));
        }

        [Test]
        public void should_clamp_rate_limit_window_when_caller_supplies_large_retry_after()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());

            service.ReportRateLimited(Source, TimeSpan.FromHours(24));

            Assert.That(service.TryBeginRequest(Source, out var retryAfter), Is.False);
            Assert.That(retryAfter, Is.LessThanOrEqualTo(MetadataServerHealthService.MaxRateLimitRetryAfter));
            Assert.That(retryAfter.TotalSeconds, Is.InRange(895, 900));
        }

        [Test]
        public void should_clamp_large_numeric_retry_after()
        {
            var retryAfter = MetadataServerHealthGate.GetRetryAfter(RateLimitedResponse("86400"));

            Assert.That(retryAfter, Is.EqualTo(MetadataServerHealthService.MaxRateLimitRetryAfter));
        }

        [Test]
        public void should_clamp_large_date_retry_after()
        {
            var header = DateTime.UtcNow.AddHours(6).ToString("R");
            var retryAfter = MetadataServerHealthGate.GetRetryAfter(RateLimitedResponse(header));

            Assert.That(retryAfter, Is.EqualTo(MetadataServerHealthService.MaxRateLimitRetryAfter));
        }

        [Test]
        public void should_open_circuit_for_modern_http_transport_failures()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), service, LogManager.GetCurrentClassLogger());

            for (var i = 0; i < MetadataServerHealthService.FailureThreshold; i++)
            {
                gate.ReportException(new HttpRequestException("connection refused"));
            }

            Assert.That(service.GetStatus(gate.SourceName).IsHealthy, Is.False);
            Assert.That(gate.TryBeginRequest(out _), Is.False);
        }

        [Test]
        public void cached_response_should_release_probe_without_marking_server_healthy()
        {
            var service = new MetadataServerHealthService(LogManager.GetCurrentClassLogger());
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), service, LogManager.GetCurrentClassLogger());
            service.TripCircuit(gate.SourceName, new WebException("edge returned 525"));
            var status = service.GetStatus(gate.SourceName);
            status.RateLimitedUntil = DateTime.UtcNow.AddSeconds(-1);

            Assert.That(gate.TryBeginRequest(out _), Is.True);

            var headers = new HttpHeader();
            headers.Add("X-Cache-Status", "HIT");
            gate.ReportResponse(new HttpResponse(new HttpRequest(Source), headers, string.Empty, HttpStatusCode.OK));

            Assert.That(status.IsHealthy, Is.False);
            Assert.That(status.ConsecutiveFailures, Is.EqualTo(MetadataServerHealthService.FailureThreshold));
            Assert.That(gate.TryBeginRequest(out _), Is.True);
        }

        [Test]
        public void should_floor_negative_numeric_retry_after_at_zero()
        {
            var retryAfter = MetadataServerHealthGate.GetRetryAfter(RateLimitedResponse("-1"));

            Assert.That(retryAfter, Is.EqualTo(TimeSpan.Zero));
        }

        private static HttpResponse RateLimitedResponse(string retryAfter)
        {
            var headers = new HttpHeader();
            headers.Add("Retry-After", retryAfter);

            return new HttpResponse(new HttpRequest(Source), headers, string.Empty, HttpStatusCode.TooManyRequests);
        }
    }
}
