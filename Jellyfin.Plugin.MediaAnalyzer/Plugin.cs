using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaAnalyzer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaAnalyzer;

/// <summary>
/// TV Show Intro Skip plugin. Uses audio analysis to find common sequences of audio shared between episodes.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly object _serializationLock = new();
    private IXmlSerializer _xmlSerializer;
    private ILibraryManager _libraryManager;
    private IItemRepository _itemRepository;
    private IMediaSegmentsManager _mediaSegmentsManager;
    private ILogger<Plugin> _logger;
    private string _pluginCachePath;
    private string _pluginDbPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="serverConfiguration">Server configuration manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository.</param>
    /// <param name="mediaSegmentsManager">Segments manager.</param>
    /// <param name="logger">Logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IServerConfigurationManager serverConfiguration,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        IMediaSegmentsManager mediaSegmentsManager,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        _xmlSerializer = xmlSerializer;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _mediaSegmentsManager = mediaSegmentsManager;
        _logger = logger;

        FFmpegPath = serverConfiguration.GetEncodingOptions().EncoderAppPathDisplay;
        Blacklist = new List<BlacklistSegment>();

        _pluginCachePath = Path.Join(applicationPaths.CachePath, "JFPMediaAnalyzer");
        _pluginDbPath = Path.Join(applicationPaths.PluginConfigurationsPath, "mediaanalyzer.db");

        FingerprintCachePath = Path.Join(_pluginCachePath, "chromaprints");

        // Create the base & cache directories (if needed).
        if (!Directory.Exists(FingerprintCachePath))
        {
            Directory.CreateDirectory(FingerprintCachePath);
        }

        // Create and migrate db
        using (var context = new MediaAnalyzerDbContext(this._pluginDbPath))
        {
            context.ApplyMigrations();
        }

        ConfigurationChanged += OnConfigurationChanged;
    }

    /// <summary>
    /// Gets or sets a value indicating whether analysis is running.
    /// </summary>
    public bool AnalysisRunning { get; set; } = false;

    /// <summary>
    /// Gets the most recent media item queue.
    /// </summary>
    public Dictionary<Guid, List<QueuedMedia>> QueuedMediaItems { get; } = new();

    /// <summary>
    /// Gets or sets the total number of episodes in the queue.
    /// </summary>
    public int TotalQueued { get; set; }

    /// <summary>
    /// Gets the directory to cache fingerprints in.
    /// </summary>
    public string FingerprintCachePath { get; private set; }

    /// <summary>
    /// Gets the full path to FFmpeg.
    /// </summary>
    public string FFmpegPath { get; private set; }

    /// <inheritdoc />
    public override string Name => "Media Analyzer";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("80885677-DACB-461B-AC97-EE7E971288AA");

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets list for Blacklisted elements. Just available during scans.
    /// </summary>
    public ICollection<BlacklistSegment> Blacklist { get; private set; }

    /// <summary>
    /// Delete segments from db.
    /// </summary>
    /// <param name="type">Type of Media segment.</param>
    /// <returns>Task.</returns>
    public async Task DeleteSegementsWithType(MediaSegmentType type)
    {
        await _mediaSegmentsManager.DeleteSegmentsAsync(creatorId: Id, type: type, typeIndex: 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete segments by id.
    /// </summary>
    /// <param name="itemId">Type of Media segment.</param>
    /// <returns>Task.</returns>
    public async Task DeleteSegementsById(Guid itemId)
    {
        _logger.LogDebug("Delete Segments for itemId: {Item}", itemId);

        await _mediaSegmentsManager.DeleteSegmentsAsync(creatorId: Id, itemId: itemId).ConfigureAwait(false);
        DeleteBlacklist(itemId);
    }

    /// <summary>
    /// Get segments from db by mode and id.
    /// </summary>
    /// <param name="itemId">Item Id.</param>
    /// <param name="mode">Mode of analysis.</param>
    /// <returns>Dictionary of guid,segments.</returns>
    public Dictionary<Guid, Segment> GetMediaSegmentsById(Guid itemId, AnalysisMode mode)
    {
        var type = mode == AnalysisMode.Introduction ? MediaSegmentType.Intro : MediaSegmentType.Outro;
        var segments = _mediaSegmentsManager.GetAllMediaSegments(itemId: itemId, type: type);
        var intros = new Dictionary<Guid, Segment>();

        foreach (var item in segments)
        {
            intros.Add(item.ItemId, new Segment()
            {
                ItemId = item.ItemId,
                Start = item.Start,
                End = item.End,
            });
        }

        return intros;
    }

    /// <summary>
    /// Create/Update segments in db.
    /// </summary>
    /// <param name="segments">List if segments.</param>
    /// <param name="mode">Mode of analysis.</param>
    /// <returns>Task.</returns>
    public async Task SaveSegmentsAsync(Dictionary<Guid, Segment> segments, AnalysisMode mode)
    {
        var allSegments = new List<MediaSegment>();
        var type = mode == AnalysisMode.Introduction ? MediaSegmentType.Intro : MediaSegmentType.Outro;
        var episodeAction = mode == AnalysisMode.Introduction ? this.Configuration.SeriesIntroAction : this.Configuration.SeriesOutroAction;
        var movieAction = this.Configuration.MoviesOutroAction;

        foreach (var (key, value) in segments)
        {
            var seg = new MediaSegment()
            {
                Start = Math.Round(value.Start, 2, MidpointRounding.AwayFromZero),
                End = Math.Round(value.End, 2, MidpointRounding.AwayFromZero),
                ItemId = value.ItemId,
                CreatorId = this.Id,
                Type = type,
                Action = value.IsEpisode ? episodeAction : movieAction,
            };

            allSegments.Add(seg);
        }

        await _mediaSegmentsManager.CreateMediaSegmentsAsync(allSegments).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            },
            new PluginPageInfo
            {
                Name = "visualizer.js",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.visualizer.js"
            }
        };
    }

    /// <summary>
    /// Gets the commit used to build the plugin.
    /// </summary>
    /// <returns>Commit.</returns>
    public string GetCommit()
    {
        var commit = string.Empty;

        var path = GetType().Namespace + ".Configuration.version.txt";
        using var stream = GetType().Assembly.GetManifestResourceStream(path);
        if (stream is null)
        {
            _logger.LogWarning("Unable to read embedded version information");
            return commit;
        }

        using var reader = new StreamReader(stream);
        commit = reader.ReadToEnd().TrimEnd();

        if (commit == "unknown")
        {
            _logger.LogTrace("Embedded version information was not valid, ignoring");
            return string.Empty;
        }

        _logger.LogInformation("Unstable plugin version built from commit {Commit}", commit);
        return commit;
    }

    internal BaseItem GetItem(Guid id)
    {
        return _libraryManager.GetItemById(id);
    }

    /// <summary>
    /// Gets the full path for an item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>Full path to item.</returns>
    internal string GetItemPath(Guid id)
    {
        return GetItem(id).Path;
    }

    /// <summary>
    /// Gets all chapters for this item.
    /// </summary>
    /// <param name="id">Item id.</param>
    /// <returns>List of chapters.</returns>
    internal List<ChapterInfo> GetChapters(Guid id)
    {
        return _itemRepository.GetChapters(GetItem(id));
    }

    /// <summary>
    /// Update Timestamps. Sync wrapper around SaveSegmentsAsync.
    /// </summary>
    /// <param name="newTimestamps">New timestamps from analysis.</param>
    /// <param name="mode">analysis mode.</param>
    internal void UpdateTimestamps(Dictionary<Guid, Segment> newTimestamps, AnalysisMode mode)
    {
        var task = Task.Run(async () => { await Plugin.Instance!.SaveSegmentsAsync(newTimestamps, mode).ConfigureAwait(false); });
        task.Wait();
    }

    /// <summary>
    /// Update blacklist cache.
    /// </summary>
    /// <param name="media">Media to blacklist.</param>
    /// <param name="mode">analysis mode.</param>
    public void UpdateBlacklist(ICollection<QueuedMedia> media, AnalysisMode mode)
    {
        lock (_serializationLock)
        {
            var newBlackList = new List<BlacklistSegment>();

            // transform to blacklist
            foreach (var seg in media)
            {
                var segName = seg.IsEpisode ? string.Format(CultureInfo.InvariantCulture, "{0} S{1}: {2}", seg.SeriesName, seg.SeasonNumber, seg.Name) : string.Format(CultureInfo.InvariantCulture, "{0}", seg.Name);
                var type = mode == AnalysisMode.Introduction ? MediaSegmentType.Intro : MediaSegmentType.Outro;
                var s = new BlacklistSegment
                {
                    ItemId = seg.ItemId,
                    Type = type,
                    Name = segName,
                };

                _logger.LogInformation("No '{Type}' segment found for '{Name}', blacklist for future analysis", type, segName);

                newBlackList.Add(s);
            }

            // merge with old list
            foreach (var seg in newBlackList)
            {
                if (!this.Blacklist.Contains(seg))
                {
                    this.Blacklist.Add(seg);
                }
            }
        }
    }

    /// <summary>
    /// Save blacklisted segments to db.
    /// </summary>
    public void SaveBlacklist()
    {
        // update db
        using (var context = new MediaAnalyzerDbContext(this._pluginDbPath))
        {
            var storedBlacklist = context.BlacklistSegment.ToList();
            foreach (var seg in this.Blacklist)
            {
                if (!storedBlacklist.Contains(seg))
                {
                    context.BlacklistSegment.Add(seg);
                }
            }

            context.SaveChanges();
        }
    }

    /// <summary>
    /// Delete Blacklisted database entries.
    /// </summary>
    /// <param name="id">Optional just id.</param>
    public void DeleteBlacklist(Guid? id)
    {
        using (var context = new MediaAnalyzerDbContext(this._pluginDbPath))
        {
            var delete = id is not null ? context.BlacklistSegment.Where(s => s.ItemId == id).ToList() : context.BlacklistSegment.ToList();
            context.RemoveRange(delete);
            context.SaveChanges();
        }
    }

    /// <summary>
    /// Get previous blacklisted segments and store them in memory.
    /// </summary>
    public void GetBlacklistFromDb()
    {
        if (this.Configuration.EnableBlacklist)
        {
            using (var context = new MediaAnalyzerDbContext(this._pluginDbPath))
            {
                this.Blacklist = context.BlacklistSegment.ToList();
            }
        }
        else
        {
            this.Blacklist.Clear();
        }
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        if (this.Configuration.ResetBlacklist == true)
        {
            this.DeleteBlacklist(null);
            this.Configuration.ResetBlacklist = false;
            this.SaveConfiguration(this.Configuration);
        }
    }

    /// <summary>
    /// Called just before the plugin is uninstalled from the server.
    /// </summary>
    public override void OnUninstalling()
    {
        // Blocking thread, other solution?
        var task = Task.Run(async () => { await _mediaSegmentsManager.DeleteSegmentsAsync(creatorId: this.Id).ConfigureAwait(false); });
        task.Wait();

        // Delete cache data
        if (Directory.Exists(_pluginCachePath))
        {
            Directory.Delete(_pluginCachePath, true);
        }
    }
}
