using System;
using System.Linq;

namespace NzbDrone.Core.Books
{
    internal static class AuthorRetrySchedule
    {
        public static readonly TimeSpan MaxPendingImportDelay = TimeSpan.FromHours(6);

        private static readonly (int FromAttempt, TimeSpan Delay)[] PendingImportBackoff =
        {
            (4, TimeSpan.FromMinutes(5)),
            (13, TimeSpan.FromMinutes(15)),
            (17, TimeSpan.FromHours(1)),
            (21, MaxPendingImportDelay)
        };

        public static bool ShouldWarn(int attemptCount) => attemptCount == 1 || attemptCount == 4 || attemptCount % 10 == 0;

        public static DateTime NextAttempt(int attemptCount)
        {
            // Three short retries, then a bounded five-minute delay.
            // Jitter keeps a recovered server from receiving a burst.
            var seconds = attemptCount <= 3 ? 60 : 300;
            return DateTime.UtcNow.AddSeconds(seconds * Jitter());
        }

        // Pending imports never give up on their own, so a request whose work never appears
        // must back off rather than refresh the whole author every few minutes.
        public static DateTime NextPendingImportAttempt(int attemptCount)
        {
            var step = PendingImportBackoff.LastOrDefault(candidate => attemptCount >= candidate.FromAttempt);
            if (step.FromAttempt == 0)
            {
                return NextAttempt(attemptCount);
            }

            return DateTime.UtcNow.AddSeconds(step.Delay.TotalSeconds * Jitter());
        }

        private static double Jitter() => 0.8 + Random.Shared.NextDouble() * 0.4;
    }
}
