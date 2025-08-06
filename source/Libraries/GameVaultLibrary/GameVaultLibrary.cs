using GameVaultLibrary.Contollers;
using GameVaultLibrary.Helper;
using GameVaultLibrary.Models;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using static GameVaultLibrary.GameVaultLibraryClient;

namespace GameVaultLibrary
{
    public class GameVaultLibrary : LibraryPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private GameVaultLibrarySettingsViewModel settings { get; set; }

        public override Guid Id { get; } = Guid.Parse("fab8be77-18ab-4e6c-ad3d-89097b492d74");

        // Change to something more appropriate
        public override string Name { get; } = "GameVault";

        public override string LibraryIcon { get; } = GameVaultLibraryClient.IconPath;

        // Implementing Client adds ability to open it via special menu in playnite.
        public override LibraryClient Client => GameVaultClient;

        public GameVaultLibraryClient GameVaultClient { get; } = new GameVaultLibraryClient();

        public GameVaultLibrary(IPlayniteAPI api) : base(api)
        {
            settings = new GameVaultLibrarySettingsViewModel(this);

            Properties = new LibraryPluginProperties
            {
                HasSettings = true,
                CanShutdownClient = true
            };

            try
            {
                var extensionYaml = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "extension.yaml");

                if (System.IO.File.Exists(extensionYaml))
                {
                    var content = System.IO.File.ReadAllText(extensionYaml);

                    if (!string.IsNullOrEmpty(content))
                    {
                        var versionMatch = System.Text.RegularExpressions.Regex.Match(content, @"version\D+(\d[\.\d]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                        if (versionMatch.Success)
                        {
                            var version = versionMatch.Groups[1].Value;

                            if (settings.Settings.Version != version)
                            {
                                settings.Settings.Version = version;
                                settings.EndEdit(); // save it
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to get version");
            }
        }

        public override ISettings GetSettings(bool firstRunSettings) => settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new GameVaultLibrarySettingsView(settings);

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args) => GetGamesAsync(args).GetAwaiter().GetResult();

        public async Task<IEnumerable<GameMetadata>> GetGamesAsync(LibraryGetGamesArgs args)
        {
            List<GameMetadata> gameMetadatas = new List<GameMetadata>();

            bool isRunning = await GameVaultClient.EnsureRunning(TimeSpan.FromSeconds(10), args.CancelToken);
            try
            {
                CancellationTokenSource cancelToken = new CancellationTokenSource();
                var result = await PipelineHelper.SendPipeMessage($"gamevault://query?query=getallgames", cancelToken.Token, expectsResult: true);



                if (string.IsNullOrEmpty(result))
                {
                    API.Instance.Dialogs.ShowMessage("No games found. Check the GameVault Client", "GameVault Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return null;
                }
                string decodedGames = Encoding.UTF8.GetString(Convert.FromBase64String(result));
                var serverGames = Playnite.SDK.Data.Serialization.FromJson<Paginated<GameVaultServerGame>>(decodedGames);

                if (serverGames?.data?.Any() != true)
                {
                    API.Instance.Dialogs.ShowMessage("No games found", "GameVault Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return null;
                }

                foreach (var serverGame in serverGames.data)
                {
                    var gameMetadata = new GameMetadata()
                    {
                        GameId = serverGame.id.ToString(),
                        Name = serverGame.title,
                        Version = serverGame.version,
                        InstallSize = serverGame.size,
                    };

                    gameMetadata.Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") };

                    gameMetadatas.Add(gameMetadata);
                }
                //}
            }
            catch (Exception ex)
            {
                API.Instance.Dialogs.ShowMessage($"Failed to import games with error:\n{ex.Message}", "GameVault Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return null;
            }


            // If the client is not running, we can't get the installed status of games
            if (isRunning)
            {
                try
                {
                    foreach (var gameMetadata in gameMetadatas)
                    {
                        var installed = await GameVaultClient.TryQueryIsInstalled(gameMetadata.GameId, args.CancelToken, ensureRunning: false);

                        if (installed.Success)
                            gameMetadata.IsInstalled = installed.Result;
                    }
                }
                catch (Exception ex)
                {
                    API.Instance.Dialogs.ShowMessage($"Error checking install status: {ex}", "GameVault Import Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                }
            }

            return gameMetadatas;
        }

        public override void OnGameSelected(OnGameSelectedEventArgs args)
        {
            if (args.NewValue == null)
                return;

            var gamevaultGames = args.NewValue.FindAll(x => x.PluginId == Id);

            if (gamevaultGames.Count == 0)
                return;

            _ = Task.Run(async () => await RefreshGameInformationAsync(gamevaultGames));
        }

        private CancellationTokenSource refreshGamesCts = null;

        private async Task RefreshGameInformationAsync(List<Game> gamevaultGames)
        {
            refreshGamesCts?.Cancel();
            refreshGamesCts = new CancellationTokenSource();

            if (!await GameVaultClient.EnsureRunning(TimeSpan.FromSeconds(10), refreshGamesCts.Token))
                return;

            using (API.Instance.Database.BufferedUpdate())
            {
                foreach (var game in gamevaultGames)
                {
                    // get installed status
                    var installed = await GameVaultClient.TryQueryIsInstalled(game.GameId, refreshGamesCts.Token, ensureRunning: false);
                    var changed = false;

                    if (installed.Success)
                    {
                        if (installed.Result != game.IsInstalled)
                        {
                            game.IsInstalled = installed.Result;
                            changed = true;
                        }

                        if (installed.Result)
                        {
                            var installPath = await GameVaultClient.TryQueryInstallDirectory(game.GameId, refreshGamesCts.Token, ensureRunning: false);

                            if (installPath.Success && game.InstallDirectory != installPath.Result)
                            {
                                game.InstallDirectory = installPath.Result;
                                changed = true;
                            }
                        }
                    }

                    if (changed)
                        API.Instance.Database.Games.Update(game);
                }
            }
        }

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
                yield break;

            yield return new GameVaultInstallController(args.Game, this.GameVaultClient);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id)
                yield break;

            yield return new GameVaultUninstallController(args.Game, this.GameVaultClient);
        }

        public override IEnumerable<PlayController> GetPlayActions(GetPlayActionsArgs args)
        {
            if (args.Game.PluginId != Id)
                yield break;

            yield return new GameVaultPlayController(args.Game, this.GameVaultClient);
        }
    }
}