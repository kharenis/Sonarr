using System.Linq;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Tv.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    [CheckOn(typeof(SeriesDeletedEvent))]
    [CheckOn(typeof(SeriesMovedEvent))]
    [CheckOn(typeof(EpisodeImportedEvent), CheckOnCondition.FailedOnly)]
    [CheckOn(typeof(EpisodeImportFailedEvent), CheckOnCondition.SuccessfulOnly)]
    public class RootFolderCheck : HealthCheckBase
    {
        private readonly ISeriesService _seriesService;
        private readonly IDiskProvider _diskProvider;
        private readonly IRootFolderService _rootFolderService;

        public RootFolderCheck(ISeriesService seriesService, IDiskProvider diskProvider, IRootFolderService rootFolderService, ILocalizationService localizationService)
            : base(localizationService)
        {
            _seriesService = seriesService;
            _diskProvider = diskProvider;
            _rootFolderService = rootFolderService;
        }

        public override HealthCheck Check()
        {
            var rootFolders = _seriesService.GetAllSeriesPaths()
                .Select(s => _rootFolderService.GetBestRootFolderPath(s.Value))
                .Distinct();

            var missingRootFolders = rootFolders.Where(s => !_diskProvider.FolderExists(s))
                .ToList();

            if (missingRootFolders.Any())
            {
                if (missingRootFolders.Count == 1)
                {
                    return new HealthCheck(GetType(),
                        HealthCheckResult.Error,
                        string.Format(_localizationService.GetLocalizedString("RootFolderMissingHealthCheckMessage"), missingRootFolders.First()),
                        "#missing-root-folder");
                }

                return new HealthCheck(GetType(),
                    HealthCheckResult.Error,
                    string.Format(_localizationService.GetLocalizedString("RootFolderMultipleMissingHealthCheckMessage"), string.Join(" | ", missingRootFolders)),
                    "#missing-root-folder");
            }

            return new HealthCheck(GetType());
        }
    }
}
