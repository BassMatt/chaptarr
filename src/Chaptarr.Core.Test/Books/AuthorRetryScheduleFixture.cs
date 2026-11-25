using System;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class AuthorRetryScheduleFixture
    {
        [TestCase(0, 60)]
        [TestCase(1, 60)]
        [TestCase(3, 60)]
        [TestCase(4, 300)]
        [TestCase(12, 300)]
        [TestCase(13, 900)]
        [TestCase(16, 900)]
        [TestCase(17, 3600)]
        [TestCase(20, 3600)]
        [TestCase(21, 21600)]
        [TestCase(9916, 21600)]
        public void pending_import_retries_back_off_to_a_six_hour_check(int attemptCount, int expectedSeconds)
        {
            var before = DateTime.UtcNow;

            var next = AuthorRetrySchedule.NextPendingImportAttempt(attemptCount);

            Assert.That(next, Is.InRange(before.AddSeconds(expectedSeconds * 0.8), DateTime.UtcNow.AddSeconds(expectedSeconds * 1.2)));
        }

        [Test]
        public void pending_import_delay_never_exceeds_the_cap()
        {
            var before = DateTime.UtcNow;

            var next = AuthorRetrySchedule.NextPendingImportAttempt(int.MaxValue);

            Assert.That(next - before, Is.LessThanOrEqualTo(TimeSpan.FromTicks((long)(AuthorRetrySchedule.MaxPendingImportDelay.Ticks * 1.2)) + TimeSpan.FromSeconds(1)));
        }

        [TestCase(4)]
        [TestCase(100)]
        public void shared_author_retry_schedule_keeps_its_five_minute_ceiling(int attemptCount)
        {
            var before = DateTime.UtcNow;

            var next = AuthorRetrySchedule.NextAttempt(attemptCount);

            Assert.That(next, Is.InRange(before.AddSeconds(240), DateTime.UtcNow.AddSeconds(360)));
        }
    }
}
