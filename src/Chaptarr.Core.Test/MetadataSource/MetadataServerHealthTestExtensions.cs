using System;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.MetadataSource
{
    public static class MetadataServerHealthTestExtensions
    {
        public static void TripCircuit(this IMetadataServerHealthService service, string sourceName, Exception exception)
        {
            for (var i = 0; i < MetadataServerHealthService.FailureThreshold; i++)
            {
                service.ReportFailure(sourceName, exception);
            }
        }
    }
}
