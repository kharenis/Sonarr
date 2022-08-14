using System;
using System.Collections.Generic;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Tv;
using NzbDrone.Core.Validation;
using Sonarr.Http;
using HttpStatusCode = System.Net.HttpStatusCode;

namespace Sonarr.Api.V3.Indexers
{
    [V3ApiController]
    public class ReleaseController : ReleaseControllerBase
    {
        private readonly IFetchAndParseRss _rssFetcherAndParser;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IMakeDownloadDecision _downloadDecisionMaker;
        private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
        private readonly IDownloadService _downloadService;
        private readonly ISeriesService _seriesService;
        private readonly IEpisodeService _episodeService;
        private readonly IParsingService _parsingService;
        private readonly Logger _logger;

        private readonly ICached<RemoteEpisode> _remoteEpisodeCache;

        public ReleaseController(IFetchAndParseRss rssFetcherAndParser,
                             ISearchForReleases releaseSearchService,
                             IMakeDownloadDecision downloadDecisionMaker,
                             IPrioritizeDownloadDecision prioritizeDownloadDecision,
                             IDownloadService downloadService,
                             ISeriesService seriesService,
                             IEpisodeService episodeService,
                             IParsingService parsingService,
                             ICacheManager cacheManager,
                             IQualityProfileService qualityProfileService,
                             Logger logger)
            : base(qualityProfileService)
        {
            _rssFetcherAndParser = rssFetcherAndParser;
            _releaseSearchService = releaseSearchService;
            _downloadDecisionMaker = downloadDecisionMaker;
            _prioritizeDownloadDecision = prioritizeDownloadDecision;
            _downloadService = downloadService;
            _seriesService = seriesService;
            _episodeService = episodeService;
            _parsingService = parsingService;
            _logger = logger;

            PostValidator.RuleFor(s => s.IndexerId).ValidId();
            PostValidator.RuleFor(s => s.Guid).NotEmpty();

            _remoteEpisodeCache = cacheManager.GetCache<RemoteEpisode>(GetType(), "remoteEpisodes");
        }

        [HttpPost]
        [Consumes("application/json")]
        public object DownloadRelease(ReleaseResource release)
        {
            var remoteEpisode = _remoteEpisodeCache.Find(GetCacheKey(release));

            if (remoteEpisode == null)
            {
                _logger.Debug("Couldn't find requested release in cache, cache timeout probably expired.");

                throw new NzbDroneClientException(HttpStatusCode.NotFound, "Couldn't find requested release in cache, try searching again");
            }

            try
            {
                if (remoteEpisode.Series == null)
                {
                    if (release.EpisodeId.HasValue)
                    {
                        var episode = _episodeService.GetEpisode(release.EpisodeId.Value);

                        remoteEpisode.Series = _seriesService.GetSeries(episode.SeriesId);
                        remoteEpisode.Episodes = new List<Episode> { episode };
                    }
                    else if (release.SeriesId.HasValue)
                    {
                        var series = _seriesService.GetSeries(release.SeriesId.Value);
                        var episodes = _parsingService.GetEpisodes(remoteEpisode.ParsedEpisodeInfo, series, true);

                        if (episodes.Empty())
                        {
                            throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to parse episodes in the release");
                        }

                        remoteEpisode.Series = series;
                        remoteEpisode.Episodes = episodes;
                    }
                    else
                    {
                        throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to find matching series and episodes");
                    }
                }
                else if (remoteEpisode.Episodes.Empty())
                {
                    var episodes = _parsingService.GetEpisodes(remoteEpisode.ParsedEpisodeInfo, remoteEpisode.Series, true);

                    if (episodes.Empty() && release.EpisodeId.HasValue)
                    {
                        var episode = _episodeService.GetEpisode(release.EpisodeId.Value);

                        episodes = new List<Episode> { episode };
                    }

                    remoteEpisode.Episodes = episodes;
                }

                if (remoteEpisode.Episodes.Empty())
                {
                    throw new NzbDroneClientException(HttpStatusCode.NotFound, "Unable to parse episodes in the release");
                }

                _downloadService.DownloadReport(remoteEpisode);
            }
            catch (ReleaseDownloadException ex)
            {
                _logger.Error(ex, ex.Message);
                throw new NzbDroneClientException(HttpStatusCode.Conflict, "Getting release from indexer failed");
            }

            return release;
        }

        [HttpGet]
        [Produces("application/json")]
        public List<ReleaseResource> GetReleases(int? seriesId, int? episodeId, int? seasonNumber)
        {
            if (episodeId.HasValue)
            {
                return GetEpisodeReleases(episodeId.Value);
            }

            if (seriesId.HasValue && seasonNumber.HasValue)
            {
                return GetSeasonReleases(seriesId.Value, seasonNumber.Value);
            }

            return GetRss();
        }

        private List<ReleaseResource> GetEpisodeReleases(int episodeId)
        {
            try
            {
                var decisions = _releaseSearchService.EpisodeSearch(episodeId, true, true);
                var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

                return MapDecisions(prioritizedDecisions);
            }
            catch (SearchFailedException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Episode search failed: " + ex.Message);
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, ex.Message);
            }
        }

        private List<ReleaseResource> GetSeasonReleases(int seriesId, int seasonNumber)
        {
            try
            {
                var decisions = _releaseSearchService.SeasonSearch(seriesId, seasonNumber, false, false, true, true);
                var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

                return MapDecisions(prioritizedDecisions);
            }
            catch (SearchFailedException ex)
            {
                throw new NzbDroneClientException(HttpStatusCode.BadRequest, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Season search failed: " + ex.Message);
                throw new NzbDroneClientException(HttpStatusCode.InternalServerError, ex.Message);
            }
        }

        private List<ReleaseResource> GetRss()
        {
            var reports = _rssFetcherAndParser.Fetch();
            var decisions = _downloadDecisionMaker.GetRssDecision(reports);
            var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(decisions);

            return MapDecisions(prioritizedDecisions);
        }

        protected override ReleaseResource MapDecision(DownloadDecision decision, int initialWeight)
        {
            var resource = base.MapDecision(decision, initialWeight);
            _remoteEpisodeCache.Set(GetCacheKey(resource), decision.RemoteEpisode, TimeSpan.FromMinutes(30));

            return resource;
        }

        private string GetCacheKey(ReleaseResource resource)
        {
            return string.Concat(resource.IndexerId, "_", resource.Guid);
        }
    }
}
