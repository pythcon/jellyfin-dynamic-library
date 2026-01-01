using Jellyfin.Plugin.DynamicLibrary.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary;

public class DynamicLibraryPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<DynamicLibraryPlugin> _logger;

    public DynamicLibraryPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<DynamicLibraryPlugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = logger;
        _logger.LogInformation("Dynamic Library plugin loaded");
    }

    public static DynamicLibraryPlugin? Instance { get; private set; }

    public override string Name => "Dynamic Library";

    public override Guid Id => Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

    public override string Description => "Infinite library powered by TVDB and TMDB with Embedarr integration.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = "Dynamic Library",
            EmbeddedResourcePath = $"{prefix}.Configuration.configPage.html",
        };
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        _logger.LogInformation("Dynamic Library configuration updated");
    }
}
