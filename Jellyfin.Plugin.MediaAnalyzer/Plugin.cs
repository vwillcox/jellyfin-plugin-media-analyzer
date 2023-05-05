using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
    private readonly object _introsLock = new();
    private IXmlSerializer _xmlSerializer;
    private ILibraryManager _libraryManager;
    private IItemRepository _itemRepository;
    private IMediaSegmentsManager _mediaSegmentsManager;
    private ILogger<Plugin> _logger;

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

        var introsDirectory = Path.Join(applicationPaths.PluginConfigurationsPath, "intros");
        FingerprintCachePath = Path.Join(introsDirectory, "cache");

        // Create the base & cache directories (if needed).
        if (!Directory.Exists(FingerprintCachePath))
        {
            Directory.CreateDirectory(FingerprintCachePath);
        }

        ConfigurationChanged += OnConfigurationChanged;

        // get all stored segments
        RegenerateCache();
    }

    /// <summary>
    /// Fired after configuration has been saved so the auto skip timer can be stopped or started.
    /// </summary>
    public event EventHandler? AutoSkipChanged;

    /// <summary>
    /// Gets the results of fingerprinting all episodes.
    /// </summary>
    public Dictionary<Guid, Intro> Intros { get; private set; } = new();

    /// <summary>
    /// Gets all discovered ending credits.
    /// </summary>
    public Dictionary<Guid, Intro> Credits { get; private set; } = new();

    /// <summary>
    /// Gets the most recent media item queue.
    /// </summary>
    public Dictionary<Guid, List<QueuedEpisode>> QueuedMediaItems { get; } = new();

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
    public override string Name => "TV Show Intro Detector";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("80885677-DACB-461B-AC97-EE7E971288AA");

    /// <summary>
    /// Gets the plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Delete segments from.
    /// </summary>
    /// <param name="type">Type of Media segment.</param>
    /// <returns>Task.</returns>
    public async Task DeleteSegementsWithType(MediaSegmentType type)
    {
        await _mediaSegmentsManager.DeleteSegmentsAsync(creatorId: Id, type: type, typeIndex: 0).ConfigureAwait(false);
    }

    /// <summary>
    /// Regenerate cached segments.
    /// </summary>
    public void RegenerateCache()
    {
        var segments = _mediaSegmentsManager.GetAllMediaSegments();
        var intro = segments.FindAll(s => s.Type == MediaSegmentType.Intro);
        var outro = segments.FindAll(s => s.Type == MediaSegmentType.Outro);
        var intros = new Dictionary<Guid, Intro>();

        foreach (var item in intro)
        {
            intros.Add(item.ItemId, new Intro()
            {
                EpisodeId = item.ItemId,
                IntroStart = item.Start,
                IntroEnd = item.End,
                FromDB = true
            });
        }

        Intros = intros;
        intros.Clear();

        foreach (var item in outro)
        {
            intros.Add(item.ItemId, new Intro()
            {
                EpisodeId = item.ItemId,
                IntroStart = item.Start,
                IntroEnd = item.End,
                FromDB = true
            });
        }

        Credits = intros;
    }

    /// <summary>
    /// Push missing segments to db.
    /// </summary>
    /// <returns>Task.</returns>
    public async Task SaveSegments()
    {
        var allSegments = new List<MediaSegment>();
        foreach (var (key, value) in Intros)
        {
            _logger.LogInformation(
                "save intro id? {0} = {1}",
                value.EpisodeId,
                !value.FromDB);

            if (!value.FromDB)
            {
                var seg = new MediaSegment()
                {
                    Start = value.IntroStart,
                    End = value.IntroEnd,
                    ItemId = value.EpisodeId,
                    CreatorId = Id,
                    Type = MediaSegmentType.Intro,
                    Action = Plugin.Instance?.Configuration.SeriesIntroAction ?? MediaSegmentAction.Auto
                };

                allSegments.Add(seg);
                value.FromDB = true;
            }
        }

        foreach (var (key, value) in Credits)
        {
            _logger.LogInformation(
                "save outro id? {0} = {1}",
                value.EpisodeId,
                !value.FromDB);

            if (!value.FromDB)
            {
                var seg = new MediaSegment()
                {
                    Start = value.IntroStart,
                    End = value.IntroEnd,
                    ItemId = value.EpisodeId,
                    CreatorId = Id,
                    Type = MediaSegmentType.Outro,
                    Action = Plugin.Instance?.Configuration.SeriesOutroAction ?? MediaSegmentAction.Auto
                };

                allSegments.Add(seg);
                value.FromDB = true;
            }
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

    internal void UpdateTimestamps(Dictionary<Guid, Intro> newTimestamps, AnalysisMode mode)
    {
        lock (_introsLock)
        {
            foreach (var intro in newTimestamps)
            {
                if (mode == AnalysisMode.Introduction)
                {
                    Plugin.Instance!.Intros[intro.Key] = intro.Value;
                }
                else if (mode == AnalysisMode.Credits)
                {
                    Plugin.Instance!.Credits[intro.Key] = intro.Value;
                }
            }

            var task = Plugin.Instance!.SaveSegments();
            task.RunSynchronously();
        }
    }

    /// <summary>
    /// Remove a intro segment from db. Used by plugin webconfig.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>Task.</returns>
    public async Task RemoveSegment(Guid itemId)
    {
        await _mediaSegmentsManager.DeleteSegmentsAsync(itemId: itemId, type: MediaSegmentType.Intro, typeIndex: 0).ConfigureAwait(false);
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e)
    {
        AutoSkipChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called just before the plugin is uninstalled from the server.
    /// </summary>
    public override void OnUninstalling()
    {
        // Blocking thread, other solution?
        var task = _mediaSegmentsManager.DeleteSegmentsAsync(creatorId: Id);
        var result = task.Result;
    }
}
