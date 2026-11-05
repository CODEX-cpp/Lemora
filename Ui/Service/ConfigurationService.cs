using Lemora.Model;
using Lemora.Service.DataSource;
using Lemora.Service.DataSource.Model;
using Lemora.Utils;
using Lemora.Utils.Tracing;
using Lemora.View;
using Newtonsoft.Json;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media;
using VariableKeywordMatcher.Provider.DirectMatch;
using SetSelfStartingHelper = Lemora.Utils.SetSelfStartingHelper;

namespace Lemora.Service
{
    public class EngagementSettings
    {
        public DateTime InstallTime = DateTime.Today;
        public bool DoNotShowAgain = false;
        public string DoNotShowAgainVersionString = "";
        public DateTime LastRequestRatingsTime = DateTime.MinValue;
        [Newtonsoft.Json.JsonIgnore]
        public VersionHelper.Version DoNotShowAgainVersion => VersionHelper.Version.FromString(DoNotShowAgainVersionString);

        public int ConnectCount = 0;
    }
    public class GeneralConfig
    {
        #region General
        public string CurrentLanguageCode = "en-us";
        public EnumServerViewStatus ServerViewStatus = EnumServerViewStatus.List;
        public enum EnumCloseButtonBehavior
        {
            Exit,
            Minimize,
        };
        public int CloseButtonBehavior = (int)EnumCloseButtonBehavior.Minimize;
        /// <summary>"Mantieni sbloccata": minuti per cui, dopo lo sblocco, un nuovo avvio non chiede impronta o password (0 = sempre)</summary>
        public int VaultKeepUnlockedMinutes = 0;
        public bool ConfirmBeforeClosingSession = false;
        /// <summary>con "riduci nel vassoio": le sessioni aperte (VNC, RDP, SSH...) restano attive mentre l'app e' in background; se falso si chiudono tutte</summary>
        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool KeepSessionsInBackground = true;
        /// <summary>nelle schede VNC Alt+Tab va al PC remoto (come le altre scorciatoie) invece di aprire il selettore di finestre di questo PC</summary>
        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool SendAltTabToVnc = true;
        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool ShowSessionIconInSessionWindow = true;

        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool TabHeaderShowIconButton = true;
        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool TabHeaderShowCloseButton = true;
        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool TabHeaderShowReConnectButton = false;
        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool ShowRecentlySessionInTray = false;
        public bool ShowNoteFieldInListView = true;

        /// <summary>livello del log scelto nelle Impostazioni: 0 Base, 1 Medio, 2 Approfondito (vedi Lemora.Utils.Diag.ApplyLogDetail); -1 = non ancora scelto, si ricava dai due campi vecchi qui sotto</summary>
        [DefaultValue(-1)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int LogDetail = -1;
        /// <summary>vecchio livello (Debug..Disattivato): letto solo per ricavare LogDetail dai profili di prima</summary>
        public int LogLevel = (int)SimpleLogHelper.EnumLogLevel.Warning;
        /// <summary>vecchia modalita' diagnostica: letta solo per ricavare LogDetail dai profili di prima (accesa = Approfondito)</summary>
        public bool DiagnosticMode = false;
        #endregion

        // Misc
        //[DefaultValue(true)]
        //[JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        //public bool TabAutoFocusContent= true;

        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool CopyPortWhenCopyAddress = false;


        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool TabWindowCloseButtonOnLeft = false;


        [DefaultValue(false)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool TabWindowSetFocusToLocalDesktopOnMouseLeaveRdpWindow = false;

        /// <summary>
        /// DateTime format for file transmit host
        /// 0 = yyyy-MM-dd HH:mm:ss (24H)
        /// 1 = yyyy-MM-dd hh:mm:ss tt (12H)
        /// 2 = HH:mm:ss yyyy-MM-dd (Time first, 24H)
        /// 3 = hh:mm:ss tt yyyy-MM-dd (Time first, 12H)
        /// 4 = MM/dd/yyyy HH:mm:ss (24H)
        /// 5 = MM/dd/yyyy hh:mm:ss tt (12H)
        /// </summary>
        [DefaultValue(0)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int FileTransmitDateTimeFormat = 0;
    }

    public class LauncherConfig
    {
        public bool LauncherEnabled = false;

#if DEBUG
        public HotkeyModifierKeys HotKeyModifiers = HotkeyModifierKeys.Shift;
#else
        public HotkeyModifierKeys HotKeyModifiers = HotkeyModifierKeys.Alt;
#endif

        public Key HotKeyKey = Key.M;

        public bool ShowNoteFieldInLauncher = true;
        public bool ShowCredentials = true;
        public bool IsMatchingCredentials = true;
        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool AllowSaveInfoInQuickConnect = true;
    }

    public class KeywordMatchConfig
    {
        /// <summary>
        /// name of the matchers
        /// </summary>
        public List<string> EnabledMatchers = new List<string>();
    }

    //public class DataSourcesConfig
    //{
    //    public SqliteSource LocalDataSource { get; set; } = new SqliteSource()
    //    {
    //        DataSourceName = DataSourceService.LOCAL_DATA_SOURCE_NAME,
    //        Path = "./" + Assert.APP_NAME + ".db"
    //    };
    //    public List<DataSourceBase> AdditionalDataSource { get; set; } = new List<DataSourceBase>();
    //}

    public class ThemeConfig
    {
        // default: tema "Fluent Dark" (ui-map\Restyle grafico app\STYLE-BLUEPRINT.md)
        public string ThemeName = "Grafite";

        public string PrimaryMidColor = "#262626";
        public string PrimaryLightColor = "#33383C";
        public string PrimaryDarkColor = "#111111";
        public string PrimaryTextColor = "#F0F0F0";

        public string AccentMidColor = "#FF60CDFF";
        public string AccentLightColor = "#FF8FDDFF";
        public string AccentDarkColor = "#FF3AA9DD";
        public string AccentTextColor = "#FF00283B";

        public string BackgroundColor = "#1E1E1E";
        public string BackgroundTextColor = "#F0F0F0";

        public string FontFamily = "Nirmala Text";
        public int FontSize = 16;   // "XL" nelle impostazioni

        #region GetColor
        public System.Windows.Media.Color GetPrimaryMidColor => ColorAndBrushHelper.HexColorToMediaColor(PrimaryMidColor);
        public System.Windows.Media.Color GetPrimaryLightColor => ColorAndBrushHelper.HexColorToMediaColor(PrimaryLightColor);
        public System.Windows.Media.Color GetPrimaryDarkColor => ColorAndBrushHelper.HexColorToMediaColor(PrimaryDarkColor);
        public System.Windows.Media.Color GetPrimaryTextColor => ColorAndBrushHelper.HexColorToMediaColor(PrimaryTextColor);

        public System.Windows.Media.Color GetAccentMidColor => ColorAndBrushHelper.HexColorToMediaColor(AccentMidColor);
        public System.Windows.Media.Color GetAccentLightColor => ColorAndBrushHelper.HexColorToMediaColor(AccentLightColor);
        public System.Windows.Media.Color GetAccentDarkColor => ColorAndBrushHelper.HexColorToMediaColor(AccentDarkColor);
        public System.Windows.Media.Color GetAccentTextColor => ColorAndBrushHelper.HexColorToMediaColor(AccentTextColor);

        public System.Windows.Media.Color GetBackgroundColor => ColorAndBrushHelper.HexColorToMediaColor(BackgroundColor);
        public System.Windows.Media.Color GetBackgroundTextColor => ColorAndBrushHelper.HexColorToMediaColor(BackgroundTextColor);
        #endregion
    }

    /// <summary>Trasferimento file nelle sessioni VNC (TightVNC)</summary>
    public class FileTransferConfig
    {
        /// <summary>limite per singolo file, in MB; 0 = nessun limite</summary>
        [DefaultValue(2048)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int MaxFileSizeMB = 2048;
        /// <summary>limite per un singolo trasferimento (somma dei file), in MB; 0 = nessun limite</summary>
        [DefaultValue(10240)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public int MaxJobSizeMB = 10240;
        /// <summary>cosa fare se il file esiste gia': 0 chiedi, 1 sostituisci, 2 salta, 3 tieni entrambi (vedi FileTransfer.ConflictPolicy)</summary>
        public int ConflictPolicy = (int)FileTransfer.ConflictPolicy.Ask;
        /// <summary>i file in corso hanno un nome provvisorio e si rinominano a fine copia</summary>
        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool UseTemporaryNames = true;
        /// <summary>chiedere conferma prima di cancellare file sul PC remoto</summary>
        [DefaultValue(true)]
        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Populate)]
        public bool ConfirmRemoteDelete = true;
        /// <summary>ultima cartella locale usata come destinazione degli scaricamenti (vuoto = Download)</summary>
        public string DefaultLocalFolder = "";
        /// <summary>cartella remota di destinazione dei caricamenti, ricordata per server (chiave: id della connessione o indirizzo:porta)</summary>
        public Dictionary<string, string> RemoteDefaultDirs = new Dictionary<string, string>();

        /// <summary>cartella di destinazione sul PC remoto proposta di base (es. C:\Trasferimenti); vuota = Desktop dell'utente</summary>
        public string DefaultRemoteDir = "";
    }

    public class Configuration
    {
        public FileTransferConfig FileTransfer { get; set; } = new FileTransferConfig();
        public GeneralConfig General { get; set; } = new GeneralConfig();
        public LauncherConfig Launcher { get; set; } = new LauncherConfig();
        public KeywordMatchConfig KeywordMatch { get; set; } = new KeywordMatchConfig();
        public int DatabaseCheckPeriod { get; set; } = 10;
        public int DatabaseReconnectPeriod { get; set; } = 60 * 2;

        private string _sqliteDatabasePath = "./" + Assert.APP_NAME + ".db";
        public string SqliteDatabasePath
        {
            get => _sqliteDatabasePath;
            set => _sqliteDatabasePath = value.Replace(Environment.CurrentDirectory, ".");
        }

        public ThemeConfig Theme { get; set; } = new ThemeConfig();
        public EngagementSettings Engagement { get; set; } = new EngagementSettings();
        public List<string> PinnedTags { get; set; } = new List<string>();
        public static Configuration? Load(string path)
        {
            var tmp = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(path));
            tmp?.RegulateTheme();
            return tmp;
        }

        private void RegulateTheme()
        {
            if (string.IsNullOrEmpty(Theme.ThemeName))
                Theme.ThemeName = "Grafite";

            if (Theme.FontSize < 10)
                Theme.FontSize = 10;
            if (Theme.FontSize > 20)
                Theme.FontSize = 20;

            var ff = Theme.FontFamily;
            var f = Fonts.SystemFontFamilies.FirstOrDefault(x => string.Equals(x.Source, ff, StringComparison.CurrentCultureIgnoreCase)) ??
                    Fonts.SystemFontFamilies.FirstOrDefault(x => x.Source.EndsWith("YaHei", StringComparison.OrdinalIgnoreCase)) ??
                    Fonts.SystemFontFamilies.First();
            Theme.FontFamily = f.Source;
        }
    }

    public class ConfigurationService
    {
        private readonly KeywordMatchService _keywordMatchService;
        public readonly List<MatchProviderInfo> AvailableMatcherProviders;
        private readonly Configuration _cfg = new Configuration();

        public GeneralConfig General => _cfg.General;
        public FileTransferConfig FileTransfer => _cfg.FileTransfer;
        public LauncherConfig Launcher => _cfg.Launcher;
        public KeywordMatchConfig KeywordMatch => _cfg.KeywordMatch;
        public SqliteSource LocalDataSource { get; } = new SqliteSource("Local");

        public int DatabaseCheckPeriod
        {
            get => _cfg.DatabaseCheckPeriod >= 0 ? (_cfg.DatabaseCheckPeriod > 99 ? 99 : _cfg.DatabaseCheckPeriod) : 0;
            set => _cfg.DatabaseCheckPeriod = value >= 0 ? (value > 99 ? 99 : value) : 0;
        }
        public int DatabaseReconnectPeriod
        {
            get => _cfg.DatabaseReconnectPeriod >= 0 ? (_cfg.DatabaseReconnectPeriod > 60 * 60 ? 60 * 60 : _cfg.DatabaseReconnectPeriod) : 0;
            set => _cfg.DatabaseReconnectPeriod = value >= 0 ? (value > 60 * 60 ? 60 * 60 : value) : 0;
        }

        public ThemeConfig Theme => _cfg.Theme;
        public EngagementSettings Engagement => _cfg.Engagement;
        /// <summary>
        /// Tags that show on the tab bar of the main window
        /// </summary>
        [Obsolete("this property become useless since 20230524, use LocalityService.TagDict instead")]
        public List<string> PinnedTags
        {
            set => _cfg.PinnedTags = value;
            get => _cfg.PinnedTags;
        }


        public List<DataSourceBase> AdditionalDataSource { get; set; } = new List<DataSourceBase>();



        public ConfigurationService(KeywordMatchService keywordMatchService, Configuration? cfg = null, List<DataSourceBase>? additionalDataSource = null)
        {
            if (cfg != null)
                _cfg = cfg;
            if (additionalDataSource != null)
                AdditionalDataSource = additionalDataSource;
            _keywordMatchService = keywordMatchService;
            AvailableMatcherProviders = KeywordMatchService.GetMatchProviderInfos() ?? new List<MatchProviderInfo>();

            LocalDataSource.Path = _cfg.SqliteDatabasePath;
            LocalDataSource.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(SqliteSource.Path))
                {
                    _cfg.SqliteDatabasePath = LocalDataSource.Path;
                }
            };

            // init matcher
            if (KeywordMatch.EnabledMatchers.Count > 0)
            {
                foreach (var matcherProvider in AvailableMatcherProviders)
                {
                    matcherProvider.Enabled = false;
                }

                foreach (var enabledName in KeywordMatch.EnabledMatchers)
                {
                    var first = AvailableMatcherProviders.FirstOrDefault(x => x.Name == enabledName);
                    if (first != null)
                        first.Enabled = true;
                }
            }
            AvailableMatcherProviders.First(x => x.Name == DirectMatchProvider.GetName()).Enabled = true;
            AvailableMatcherProviders.First(x => x.Name == DirectMatchProvider.GetName()).IsEditable = false;
            KeywordMatch.EnabledMatchers = AvailableMatcherProviders.Where(x => x.Enabled).Select(x => x.Name).ToList();
            _keywordMatchService.Init(KeywordMatch.EnabledMatchers.ToArray());

            // register matcher change event
            foreach (var info in AvailableMatcherProviders)
            {
                info.PropertyChanged += OnMatchProviderChangedHandler;
            }

            // 


            AdditionalDataSource = DataSourceService.AdditionalSourcesLoadFromProfile(AppPathHelper.Instance.ProfileAdditionalDataSourceJsonPath);
            Save();
        }

        private void OnMatchProviderChangedHandler(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MatchProviderInfo.Enabled))
            {
                KeywordMatch.EnabledMatchers = AvailableMatcherProviders.Where(x => x.Enabled).Select(x => x.Name).ToList();
                Save();
                _keywordMatchService.Init(KeywordMatch.EnabledMatchers.ToArray());
            }
        }

        public bool CanSave = true;

        public void Save()
        {
            AdditionalDataSource = AdditionalDataSource.Distinct().ToList();
            if (!CanSave) return;
            lock (this)
            {
                if (!CanSave) return;
                CanSave = false;
                // Early return/throw below used to leave CanSave == false for the whole session.
                try
                {
                    var fi = new FileInfo(AppPathHelper.Instance.ProfileJsonPath);

                    try
                    {
                        if (fi?.Directory?.Exists == false)
                            fi.Directory.Create();
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Error(e);
                        return;
                    }

                    RetryHelper.Try(() =>
                    {
                        File.WriteAllText(AppPathHelper.Instance.ProfileJsonPath, JsonConvert.SerializeObject(this._cfg, Formatting.Indented), Encoding.UTF8);
                    }, actionOnError: exception => UnifyTracing.Error(exception));

                    DataSourceService.AdditionalSourcesSaveToProfile(AppPathHelper.Instance.ProfileAdditionalDataSourceJsonPath, AdditionalDataSource);
                }
                finally
                {
                    CanSave = true;
                }
            }
        }


        public static void SetSelfStart(bool isInstall)
        {
            // istanze di mappatura (LEMORA_HEADLESS=1): non toccare l'avvio automatico dell'utente
            if (Environment.GetEnvironmentVariable("LEMORA_HEADLESS") == "1") return;
            // prove con la finestra visibile (es. il primo avvio): stessa protezione, i collegamenti dell'utente non si toccano
            if (Environment.GetEnvironmentVariable("LEMORA_NO_SHORTCUTS") == "1") return;
            SetSelfStartingHelper.SetSelfStart(isInstall, Assert.APP_NAME);
        }

        public static ConfigurationService LoadFromAppPath(KeywordMatchService keywordMatchService)
        {
            var cfg = new Configuration();

            if (File.Exists(AppPathHelper.Instance.ProfileJsonPath))
            {
                var tmp = Configuration.Load(AppPathHelper.Instance.ProfileJsonPath);
                if (tmp != null)
                    cfg = tmp;
            }

            var ads = DataSourceService.AdditionalSourcesLoadFromProfile(AppPathHelper.Instance.ProfileAdditionalDataSourceJsonPath);

            return new ConfigurationService(keywordMatchService, cfg, ads);
        }
    }
}
