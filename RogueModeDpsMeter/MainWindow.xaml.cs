using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;

namespace RogueModeDpsMeter;

public partial class MainWindow : Window
{
    // Combat Tracker v0.9.0 Beta: combat-only public release branch.
    private const string TrackerVersion = "v0.9.0 Beta";
    private const string PalworldProcessName = "Palworld-Win64-Shipping";
    private const string TelemetryFileName = "RogueModeTelemetry.txt";
    private const int MinimumCompatibleLuaReleaseCandidate = 7;
    private const int SupportedTelemetryFormatVersion = 1;
    private const double TelemetryStallWarningSeconds = 12.0;
    private const int DiagnosticTelemetryFirstBytes = 64 * 1024;
    private const int DiagnosticTelemetryTailBytes = 2 * 1024 * 1024;
    private const int DiagnosticLogFirstBytes = 64 * 1024;
    private const int DiagnosticLogTailBytes = 4 * 1024 * 1024;
    private const int DiagnosticCrashTailBytes = 1024 * 1024;
    private const int DiagnosticRecentEncounterCount = 10;

    private enum DiagnosticBundleMode
    {
        PublicSupport,
        PrivateDeveloper
    }

    private sealed class DiagnosticPrivacyReport
    {
        public Dictionary<string, string> SensitiveNames { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int UserPathRedactions { get; set; }
        public int LocalPathRedactions { get; set; }
        public int PlayerNameRedactions { get; set; }
        public int OwnerNameRedactions { get; set; }
        public int DisplayNameRedactions { get; set; }
        public int ActorInstanceRedactions { get; set; }
        public int MemoryAddressRedactions { get; set; }
        public int IdentifierRedactions { get; set; }

        public int TotalRedactions =>
            UserPathRedactions +
            LocalPathRedactions +
            PlayerNameRedactions +
            OwnerNameRedactions +
            DisplayNameRedactions +
            ActorInstanceRedactions +
            MemoryAddressRedactions +
            IdentifierRedactions;

        public void RememberName(string? value, string replacement)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string trimmed = value.Trim();
            if (trimmed.Length < 2 ||
                trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("unresolved", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("nil", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Player", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("Pal", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SensitiveNames.TryAdd(trimmed, replacement);
        }

        public string BuildReport(DiagnosticBundleMode mode)
        {
            StringBuilder text = new();
            text.AppendLine("RogueMode Combat Tracker · Diagnostic Privacy Report");
            text.AppendLine($"Bundle mode: {MainWindow.GetModeLabel(mode)}");
            text.AppendLine(
                $"Privacy redactions applied: {(mode == DiagnosticBundleMode.PublicSupport ? "Yes" : "No")}");
            text.AppendLine($"User/profile paths redacted: {UserPathRedactions:N0}");
            text.AppendLine($"Other local paths redacted: {LocalPathRedactions:N0}");
            text.AppendLine($"Player names redacted: {PlayerNameRedactions:N0}");
            text.AppendLine($"Owner names redacted: {OwnerNameRedactions:N0}");
            text.AppendLine($"Pal/display names redacted: {DisplayNameRedactions:N0}");
            text.AppendLine($"Actor instances redacted: {ActorInstanceRedactions:N0}");
            text.AppendLine($"Memory addresses redacted: {MemoryAddressRedactions:N0}");
            text.AppendLine($"GUID/session identifiers redacted: {IdentifierRedactions:N0}");
            text.AppendLine($"Total replacements: {TotalRedactions:N0}");
            return text.ToString().TrimEnd();
        }
    }

    // Exact death telemetry still ends encounters immediately. This timeout
    // is a fallback for multiplayer targets whose death event is not received.
    private const double EncounterInactivityTimeoutSeconds = 15.0;

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private readonly DispatcherTimer _maintenanceTimer;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _uiTimer;
    private readonly Stopwatch _applicationClock = Stopwatch.StartNew();

    private bool _interfaceDirty = true;

    private readonly Dictionary<string, CombatantEntry> _combatants =
        new(StringComparer.Ordinal);

    // Shared visible-name cache for players, owned Pals, remote/base Pals,
    // wild targets, tower bosses, and raid bosses.
    private readonly Dictionary<string, string> _knownCombatantNames =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, int> _knownActorNamePriorities =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, PalOwnerInfo> _palOwners =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, DamageSourceEntry> _damageSources =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, PalSkillRuntimeEntry> _palSkillStats =
        new(StringComparer.Ordinal);

    // Q action-begin records can arrive before the first damage record starts
    // an encounter. Keep a short rolling buffer so the opening cast can still
    // be counted when its first exact C correlation arrives.
    private readonly List<PendingPalSkillActivation> _recentPalSkillActivations =
        new();

    private readonly List<PendingDamageMetadataMatch> _pendingDamageMetadata =
        new();

    private readonly List<EncounterSnapshot> _encounterHistory = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private EncounterHistoryWindow? _historyWindow;
    private DateTimeOffset _encounterStartedAtUtc;
    private bool _encounterSnapshotSaved;

    private int _nextCombatantOrder;
    private int _nextDamageSourceOrder;
    private int _nextPalSkillOrder;

    private const string RmThemeName = "RM";
    private const string LegacySciFiThemeName = "SciFi";
    private const string ClassicThemeName = "Classic";
    private const string PotatoeThemeName = "Potatoe";
    private const string BelleNoireThemeName = "BelleNoire";
    private const string SolenneThemeName = "Solenne";
    private const string JormuntideIgnisThemeName = "JormuntideIgnis";
    private const string SekhmetThemeName = "Sekhmet";
    private const string PalworldThemeName = "Palworld";

    private OverlayToggleWindow? _overlayToggleWindow;
    private bool _overlayLocked;
    private string _currentTheme = RmThemeName;

    private Process? _palworldProcess;
    private string _telemetryFilePath = string.Empty;
    private string _expectedTelemetryFilePath = string.Empty;
    private long _telemetryPosition;
    private string _pendingTelemetryText = string.Empty;
    private TelemetryConnectionState _telemetryConnectionState =
        TelemetryConnectionState.WaitingForPalworld;
    private string _luaVersion = "Not reported";
    private int? _luaReleaseCandidate;
    private int? _telemetryFormatVersion;
    private string _telemetryProfile = "Unknown";
    private string _diagnosticsMode = "Unknown";
    private bool _versionHandshakeSeen;
    private DateTime _lastTelemetryWriteUtc = DateTime.MinValue;
    private string _lastTelemetryError = string.Empty;
    private string _lastDiagnosticZipPath = string.Empty;

    private bool _closing;
    private bool _connected;
    private bool _encounterActive;
    private bool _encounterPaused;
    private bool _encounterComplete;
    private bool _targetConfirmedDead;

    private DateTime _nextAttachAttemptUtc = DateTime.MinValue;

    private string? _activeTargetName;
    private string _targetPlaceholder = "Waiting for Palworld";

    private long _playerDamage;
    private long _palDamage;
    private long _totalDamage;

    private string _playerDisplayName = "Player";
    private string? _activePalActorName;
    private string _palDisplayName = "No active Pal";
    private bool _activePalStateKnown;

    // Emitted directly by Lua through PalUtility.GetPlayerCharacter. Local
    // PAL attackers can therefore be grouped correctly without waiting for
    // CharacterParameterComponent.Trainer replication.
    private string? _localPlayerActorId;
    private string? _localPlayerDisplayName;

    private double _encounterStartSeconds;
    private double _lastTargetActivitySeconds;
    private double _finalizedDurationSeconds;
    private double _displayedCombinedDps;
    private double _displayedPlayerDps;
    private double _displayedPalDps;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(
        IntPtr windowHandle,
        int index
    );

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(
        IntPtr windowHandle,
        int index
    );

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(
        IntPtr windowHandle,
        int index,
        IntPtr newValue
    );

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(
        IntPtr windowHandle,
        int index,
        int newValue
    );

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );

    private static IntPtr GetWindowLongPointer(
        IntPtr windowHandle,
        int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));
    }

    private static void SetWindowLongPointer(
        IntPtr windowHandle,
        int index,
        IntPtr newValue)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(windowHandle, index, newValue);
        }
        else
        {
            SetWindowLong32(
                windowHandle,
                index,
                newValue.ToInt32()
            );
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        HeaderBrandImage.Source = BrandAssets.HeaderPicture;
        Dispatcher.UnhandledException += MainWindow_DispatcherUnhandledException;
        LoadSavedTheme();
        _encounterHistory.AddRange(EncounterHistoryStore.Load());
        UpdateHistoryButton();

        Topmost = true;

        Loaded += MainWindow_Loaded;
        LocationChanged += MainWindow_PositionChanged;
        SizeChanged += MainWindow_PositionChanged;
        StateChanged += MainWindow_StateChanged;

        _maintenanceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _maintenanceTimer.Tick += MaintenanceTimer_Tick;

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Keep reading telemetry quickly so no combat events are missed.
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _pollTimer.Tick += PollTimer_Tick;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Visible values and source ranking refresh once per second.
            Interval = TimeSpan.FromSeconds(1)
        };
        _uiTimer.Tick += UiTimer_Tick;

        _maintenanceTimer.Start();
        _uiTimer.Start();
        TryAttachTelemetry();
        RenderInterface();
    }

    private static string ThemePreferencePath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "RogueModeCombatTracker",
        "theme.txt"
    );

    private static string TrackerDataDirectory => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "RogueModeCombatTracker"
    );

    private static string DiagnosticsDirectory => Path.Combine(
        TrackerDataDirectory,
        "Diagnostics"
    );

    private void LoadSavedTheme()
    {
        string selectedTheme = RmThemeName;

        try
        {
            if (File.Exists(ThemePreferencePath))
            {
                string savedTheme = File.ReadAllText(
                    ThemePreferencePath
                ).Trim();

                if (savedTheme.Equals(
                        ClassicThemeName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = ClassicThemeName;
                }
                else if (savedTheme.Equals(
                             PotatoeThemeName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = PotatoeThemeName;
                }
                else if (savedTheme.Equals(
                             BelleNoireThemeName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = BelleNoireThemeName;
                }
                else if (savedTheme.Equals(
                             SolenneThemeName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = SolenneThemeName;
                }
                else if (savedTheme.Equals(
                             JormuntideIgnisThemeName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = JormuntideIgnisThemeName;
                }
                else if (savedTheme.Equals(
                             SekhmetThemeName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = SekhmetThemeName;
                }
                else if (savedTheme.Equals(
                             PalworldThemeName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = PalworldThemeName;
                }
                else if (savedTheme.Equals(
                             RmThemeName,
                             StringComparison.OrdinalIgnoreCase) ||
                         savedTheme.Equals(
                             LegacySciFiThemeName,
                             StringComparison.OrdinalIgnoreCase))
                {
                    selectedTheme = RmThemeName;
                }
            }
        }
        catch
        {
            // A theme preference failure should never block the tracker.
        }

        ApplyTheme(selectedTheme, savePreference: false);
    }

    private void ApplyTheme(
        string themeName,
        bool savePreference)
    {
        string normalizedTheme;

        if (themeName.Equals(
                ClassicThemeName,
                StringComparison.OrdinalIgnoreCase))
        {
            normalizedTheme = ClassicThemeName;
        }
        else if (themeName.Equals(
                     PotatoeThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            normalizedTheme = PotatoeThemeName;
        }
        else if (themeName.Equals(
                     BelleNoireThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            normalizedTheme = BelleNoireThemeName;
        }
        else if (themeName.Equals(
                     SolenneThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            normalizedTheme = SolenneThemeName;
        }
        else if (themeName.Equals(
                     JormuntideIgnisThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            normalizedTheme = JormuntideIgnisThemeName;
        }
        else if (themeName.Equals(
                     SekhmetThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            normalizedTheme = SekhmetThemeName;
        }
        else if (themeName.Equals(
                     PalworldThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            normalizedTheme = PalworldThemeName;
        }
        else
        {
            normalizedTheme = RmThemeName;
        }

        ResourceDictionary newWindowTheme;
        ResourceDictionary newApplicationTheme;

        try
        {
            // Load both dictionaries completely before removing the active
            // theme. A malformed or unavailable skin therefore cannot leave
            // the overlay without the brushes required by BaseStyles.xaml.
            newWindowTheme = new ResourceDictionary
            {
                Source = new Uri(
                    $"Themes/{normalizedTheme}.xaml",
                    UriKind.Relative
                )
            };

            newApplicationTheme = new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/Themes/{normalizedTheme}.xaml",
                    UriKind.Absolute
                )
            };
        }
        catch (Exception exception)
        {
            LogThemeSwitchFailure(normalizedTheme, exception);
            return;
        }

        try
        {
            var windowDictionaries = Resources.MergedDictionaries;
            List<ResourceDictionary> oldWindowThemes =
                windowDictionaries
                    .Where(IsThemeDictionary)
                    .ToList();

            foreach (ResourceDictionary dictionary in oldWindowThemes)
            {
                windowDictionaries.Remove(dictionary);
            }

            windowDictionaries.Insert(0, newWindowTheme);

            var applicationDictionaries =
                Application.Current.Resources.MergedDictionaries;

            List<ResourceDictionary> oldApplicationThemes =
                applicationDictionaries
                    .Where(IsThemeDictionary)
                    .ToList();

            foreach (ResourceDictionary dictionary in oldApplicationThemes)
            {
                applicationDictionaries.Remove(dictionary);
            }

            applicationDictionaries.Insert(
                0,
                newApplicationTheme
            );

            _currentTheme = normalizedTheme;
            ThemeButton.Content = normalizedTheme switch
            {
                PotatoeThemeName => "POTATOE",
                BelleNoireThemeName => "BELLE",
                SolenneThemeName => "SOL",
                JormuntideIgnisThemeName => "IGNIS",
                SekhmetThemeName => "SEKH",
                PalworldThemeName => "PAL",
                ClassicThemeName => "CLASSIC",
                _ => "RM"
            };

            ApplyThemeBackground(normalizedTheme);
            _overlayToggleWindow?.ApplyTheme();
            _historyWindow?.ApplyTheme(normalizedTheme);
        }
        catch (Exception exception)
        {
            LogThemeSwitchFailure(normalizedTheme, exception);
            return;
        }

        if (!savePreference)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(
                ThemePreferencePath
            );

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                ThemePreferencePath,
                normalizedTheme
            );
        }
        catch
        {
            // The selected skin remains active for the current session.
        }
    }

    private void ApplyThemeBackground(string normalizedTheme)
    {
        ImageSource? backgroundSource = null;

        if (normalizedTheme.Equals(
                RmThemeName,
                StringComparison.OrdinalIgnoreCase))
        {
            backgroundSource = ThemeAssets.RmBackground;
        }
        else if (normalizedTheme.Equals(
                     PotatoeThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            backgroundSource = ThemeAssets.PotatoeBackground;
        }
        else if (normalizedTheme.Equals(
                     BelleNoireThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            backgroundSource = ThemeAssets.BelleNoireBackground;
        }
        else if (normalizedTheme.Equals(
                     SolenneThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            backgroundSource = ThemeAssets.SolenneBackground;
        }
        else if (normalizedTheme.Equals(
                     JormuntideIgnisThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            backgroundSource = ThemeAssets.JormuntideIgnisBackground;
        }
        else if (normalizedTheme.Equals(
                     SekhmetThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            backgroundSource = ThemeAssets.SekhmetBackground;
        }
        else if (normalizedTheme.Equals(
                     PalworldThemeName,
                     StringComparison.OrdinalIgnoreCase))
        {
            backgroundSource = ThemeAssets.PalworldBackground;
        }

        if (backgroundSource is not null)
        {
            // Use an ImageBrush instead of an Image control. An Image control
            // reports the artwork's natural size during layout and, with
            // SizeToContent=Height, can stretch the tracker to image height.
            ImageBrush backgroundBrush = new(backgroundSource)
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };

            if (backgroundBrush.CanFreeze)
            {
                backgroundBrush.Freeze();
            }

            ThemeBackgroundLayer.Background = backgroundBrush;
        }
        else
        {
            ThemeBackgroundLayer.Background = Brushes.Transparent;
        }
    }

    private static void LogThemeSwitchFailure(
        string themeName,
        Exception exception)
    {
        try
        {
            string? directory = Path.GetDirectoryName(CrashLogPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                CrashLogPath,
                $"[{DateTime.Now:O}] Theme switch failed: {themeName}\n" +
                $"{exception}\n\n"
            );
        }
        catch
        {
            // Logging must never interrupt the overlay.
        }
    }

    private static bool IsThemeDictionary(
        ResourceDictionary dictionary)
    {
        string source = dictionary.Source?.OriginalString ?? string.Empty;

        return source.EndsWith(
                   $"/{RmThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{LegacySciFiThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{ClassicThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{PotatoeThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{BelleNoireThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{SolenneThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{JormuntideIgnisThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{SekhmetThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"/{PalworldThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{RmThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{LegacySciFiThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{ClassicThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{PotatoeThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{BelleNoireThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{SolenneThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{JormuntideIgnisThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{SekhmetThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase) ||
               source.EndsWith(
                   $"Themes/{PalworldThemeName}.xaml",
                   StringComparison.OrdinalIgnoreCase);
    }

    private void HistoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_historyWindow is not null)
        {
            if (_historyWindow.WindowState == WindowState.Minimized)
            {
                _historyWindow.WindowState = WindowState.Normal;
            }

            _historyWindow.Activate();
            return;
        }

        _historyWindow = new EncounterHistoryWindow(
            _encounterHistory,
            PersistEncounterHistory,
            _currentTheme,
            _sessionId
        );

        _historyWindow.Closed += (_, _) =>
        {
            _historyWindow = null;
        };

        _historyWindow.Show();
    }

    private void UpdateHistoryButton()
    {
        HistoryButton.Content = _encounterHistory.Count == 0
            ? "HIST"
            : $"HIST {_encounterHistory.Count}";

        HistoryButton.ToolTip = _encounterHistory.Count switch
        {
            0 => "Open encounter history",
            1 => "Open 1 saved encounter",
            _ => $"Open {_encounterHistory.Count} saved encounters"
        };
    }

    private void PersistEncounterHistory()
    {
        EncounterHistoryStore.NormalizeInPlace(_encounterHistory);
        EncounterHistoryStore.Save(_encounterHistory);
        UpdateHistoryButton();
    }

    private void ThemeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string nextTheme = _currentTheme switch
        {
            RmThemeName => PotatoeThemeName,
            PotatoeThemeName => BelleNoireThemeName,
            BelleNoireThemeName => SolenneThemeName,
            SolenneThemeName => JormuntideIgnisThemeName,
            JormuntideIgnisThemeName => SekhmetThemeName,
            SekhmetThemeName => PalworldThemeName,
            PalworldThemeName => ClassicThemeName,
            _ => RmThemeName
        };

        ApplyTheme(nextTheme, savePreference: true);

        // Row colors are generated from theme resources, so rebuild the
        // visible rows immediately after a skin change.
        _interfaceDirty = false;
        RenderInterface();
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "RogueModeCombatTracker",
        "crash.log"
    );

    private void MainWindow_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string? directory = Path.GetDirectoryName(CrashLogPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                CrashLogPath,
                $"[{DateTime.Now:O}]\n{e.Exception}\n\n"
            );
        }
        catch
        {
            // Crash logging must not replace the original exception.
        }

        // Preserve normal crash behavior so failures are never hidden.
        e.Handled = false;
    }

    private void MainWindow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_overlayToggleWindow is not null)
        {
            return;
        }

        _overlayToggleWindow = new OverlayToggleWindow(this);
        _overlayToggleWindow.ToggleRequested +=
            OverlayToggleWindow_ToggleRequested;
        _overlayToggleWindow.SetLocked(_overlayLocked);
        _overlayToggleWindow.ApplyTheme();
        PositionOverlayToggleWindow();
        _overlayToggleWindow.Show();
    }

    private void MainWindow_PositionChanged(
        object? sender,
        EventArgs e)
    {
        PositionOverlayToggleWindow();
    }

    private void MainWindow_StateChanged(
        object? sender,
        EventArgs e)
    {
        if (_overlayToggleWindow is null)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            _overlayToggleWindow.Hide();
        }
        else
        {
            PositionOverlayToggleWindow();

            if (!_overlayToggleWindow.IsVisible)
            {
                _overlayToggleWindow.Show();
            }
        }
    }

    private void OverlayToggleWindow_ToggleRequested(
        object? sender,
        EventArgs e)
    {
        SetOverlayLocked(!_overlayLocked);
    }

    private void SetOverlayLocked(bool locked)
    {
        _overlayLocked = locked;

        IntPtr windowHandle = new WindowInteropHelper(this).Handle;

        if (windowHandle != IntPtr.Zero)
        {
            long extendedStyle =
                GetWindowLongPointer(
                    windowHandle,
                    GwlExStyle
                ).ToInt64();

            if (locked)
            {
                extendedStyle |=
                    WsExTransparent |
                    WsExNoActivate;
            }
            else
            {
                extendedStyle &=
                    ~(WsExTransparent |
                      WsExNoActivate);
            }

            SetWindowLongPointer(
                windowHandle,
                GwlExStyle,
                new IntPtr(extendedStyle)
            );

            SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove |
                SwpNoSize |
                SwpNoZOrder |
                SwpNoActivate |
                SwpFrameChanged
            );
        }

        _overlayToggleWindow?.SetLocked(locked);

        if (!locked)
        {
            Activate();
        }
    }

    private void PositionOverlayToggleWindow()
    {
        if (_overlayToggleWindow is null ||
            WindowState == WindowState.Minimized)
        {
            return;
        }

        const double gap = 6;
        Rect workArea = SystemParameters.WorkArea;

        double preferredLeft = Left + ActualWidth + gap;
        double left = preferredLeft +
                      _overlayToggleWindow.Width <=
                      workArea.Right
            ? preferredLeft
            : Left - gap - _overlayToggleWindow.Width;

        left = Math.Max(
            workArea.Left,
            Math.Min(
                left,
                workArea.Right -
                _overlayToggleWindow.Width
            )
        );

        double top = Math.Max(
            workArea.Top,
            Math.Min(
                Top + 8,
                workArea.Bottom -
                _overlayToggleWindow.Height
            )
        );

        _overlayToggleWindow.Left = left;
        _overlayToggleWindow.Top = top;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        TryFinalizeInactiveEncounter();

        // Combat time and DPS must advance once per second even when no new
        // telemetry line arrived during that interval.
        RefreshTelemetryFileMetadata();

        if (_interfaceDirty || _encounterActive)
        {
            _interfaceDirty = false;
            RenderInterface();
        }
        else
        {
            RenderTelemetryHealth();
        }
    }

    private void MaintenanceTimer_Tick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (_connected)
        {
            if (HasPalworldExited())
            {
                HandleProcessExit();
            }

            return;
        }

        if (DateTime.UtcNow >= _nextAttachAttemptUtc)
        {
            TryAttachTelemetry();
        }
    }

    private void TryAttachTelemetry()
    {
        if (_closing || _connected)
        {
            return;
        }

        Process? palworld = Process
            .GetProcessesByName(PalworldProcessName)
            .FirstOrDefault();

        if (palworld is null)
        {
            _telemetryConnectionState =
                TelemetryConnectionState.WaitingForPalworld;
            _targetPlaceholder = "Waiting for Palworld";
            _lastTelemetryError = string.Empty;
            _nextAttachAttemptUtc = DateTime.UtcNow.AddSeconds(1);
            UpdateInterface();
            return;
        }

        string executablePath;

        try
        {
            executablePath = palworld.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            palworld.Dispose();
            _telemetryConnectionState =
                TelemetryConnectionState.AttachmentError;
            _targetPlaceholder = "Attachment error";
            _lastTelemetryError = "Unable to read the Palworld executable path.";
            _nextAttachAttemptUtc = DateTime.UtcNow.AddSeconds(3);
            UpdateInterface();
            return;
        }

        string? executableDirectory = Path.GetDirectoryName(executablePath);

        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            palworld.Dispose();
            _telemetryConnectionState =
                TelemetryConnectionState.AttachmentError;
            _targetPlaceholder = "Attachment error";
            _lastTelemetryError = "Palworld executable directory was unavailable.";
            _nextAttachAttemptUtc = DateTime.UtcNow.AddSeconds(3);
            UpdateInterface();
            return;
        }

        string telemetryPath = Path.Combine(
            executableDirectory,
            "ue4ss",
            TelemetryFileName
        );

        _expectedTelemetryFilePath = telemetryPath;

        if (!File.Exists(telemetryPath))
        {
            palworld.Dispose();
            _telemetryConnectionState =
                TelemetryConnectionState.WaitingForTelemetry;
            _targetPlaceholder = "Waiting for telemetry";
            _lastTelemetryError = string.Empty;
            _nextAttachAttemptUtc = DateTime.UtcNow.AddSeconds(1);
            UpdateInterface();
            return;
        }

        try
        {
            FileInfo telemetryFile = new(telemetryPath);

            ResetTelemetryHandshake();
            InspectExistingTelemetryHandshake(telemetryPath);

            _palworldProcess = palworld;
            _telemetryFilePath = telemetryPath;
            _lastTelemetryWriteUtc = telemetryFile.LastWriteTimeUtc;

            // Ignore combat entries from older sessions. The version record
            // is inspected separately before this position is established.
            _telemetryPosition = telemetryFile.Length;
            _pendingTelemetryText = string.Empty;

            _connected = true;
            _telemetryConnectionState = TelemetryConnectionState.Connected;
            _targetPlaceholder = "No target";
            _lastTelemetryError = string.Empty;
            _nextAttachAttemptUtc = DateTime.MinValue;

            _pollTimer.Start();
            UpdateInterface();
        }
        catch
        {
            palworld.Dispose();
            _telemetryConnectionState =
                TelemetryConnectionState.TelemetryError;
            _targetPlaceholder = "Telemetry error";
            _lastTelemetryError = "The telemetry file could not be opened.";
            _nextAttachAttemptUtc = DateTime.UtcNow.AddSeconds(3);
            UpdateInterface();
        }
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_connected)
        {
            return;
        }

        if (HasPalworldExited())
        {
            HandleProcessExit();
            return;
        }

        ReadNewTelemetryLines();
        UpdateInterface();
    }

    private void ReadNewTelemetryLines()
    {
        if (string.IsNullOrWhiteSpace(_telemetryFilePath))
        {
            HandleTelemetryFailure();
            return;
        }

        try
        {
            using FileStream stream = new(
                _telemetryFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );

            if (stream.Length < _telemetryPosition)
            {
                _telemetryPosition = 0;
                _pendingTelemetryText = string.Empty;
            }

            if (stream.Length == _telemetryPosition)
            {
                return;
            }

            stream.Seek(_telemetryPosition, SeekOrigin.Begin);

            byte[] buffer = new byte[64 * 1024];
            int bytesRead;

            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                _telemetryPosition += bytesRead;
                _lastTelemetryWriteUtc = File.GetLastWriteTimeUtc(
                    _telemetryFilePath
                );
                _pendingTelemetryText += Encoding.UTF8.GetString(
                    buffer,
                    0,
                    bytesRead
                );

                ProcessCompleteTelemetryLines();
            }
        }
        catch (FileNotFoundException)
        {
            HandleTelemetryFailure();
        }
        catch (IOException)
        {
            HandleTelemetryFailure();
        }
        catch (UnauthorizedAccessException)
        {
            HandleTelemetryFailure();
        }
    }

    private void ProcessCompleteTelemetryLines()
    {
        while (true)
        {
            int newlineIndex = _pendingTelemetryText.IndexOf('\n');

            if (newlineIndex < 0)
            {
                if (_pendingTelemetryText.Length > 256 * 1024)
                {
                    _pendingTelemetryText = string.Empty;
                }

                return;
            }

            string line = _pendingTelemetryText[..newlineIndex]
                .TrimEnd('\r');

            _pendingTelemetryText = _pendingTelemetryText[(newlineIndex + 1)..];

            if (!string.IsNullOrWhiteSpace(line))
            {
                ProcessTelemetryLine(line);
            }
        }
    }

    private void ProcessTelemetryLine(string line)
    {
        string[] fields = line.Split('|');

        if (fields.Length >= 4 &&
            fields[0].Equals("V", StringComparison.OrdinalIgnoreCase) &&
            TryParseTimestamp(fields[1], out _))
        {
            ApplyTelemetryVersionRecord(fields);
            return;
        }

        // Legacy format: timestamp|damage|attacker|defender
        if (fields.Length == 4 &&
            TryParseTimestamp(fields[0], out double legacyTimestamp) &&
            int.TryParse(
                fields[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int legacyDamage))
        {
            ProcessDamageEvent(
                legacyTimestamp,
                legacyDamage,
                fields[2],
                fields[3],
                null,
                null
            );
            return;
        }

        // Direct local-player identity:
        // L|timestamp|local player actor|display name
        if (fields.Length >= 4 &&
            (fields[0].Equals("L", StringComparison.OrdinalIgnoreCase) ||
             fields[0].Equals(
                 "LOCAL_PLAYER",
                 StringComparison.OrdinalIgnoreCase)) &&
            TryParseTimestamp(fields[1], out _))
        {
            ProcessLocalPlayerEvent(
                fields[2],
                fields[3]
            );
            return;
        }

        // Shared actor-name format:
        // N|timestamp|actor|display name|priority
        // Priority: 3 custom nickname, 2 official display name, 1 fallback.
        if (fields.Length >= 4 &&
            (fields[0].Equals("N", StringComparison.OrdinalIgnoreCase) ||
             fields[0].Equals("NAME", StringComparison.OrdinalIgnoreCase)) &&
            TryParseTimestamp(fields[1], out _))
        {
            int namePriority = 2;

            if (fields.Length >= 5 &&
                int.TryParse(
                    fields[4],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedPriority))
            {
                namePriority = Math.Clamp(parsedPriority, 1, 3);
            }

            ProcessActorNameEvent(
                fields[2],
                fields[3],
                namePriority
            );
            return;
        }

        // Pal action lifecycle:
        // Q|time|sequence|phase|actor|source type|source name|target|
        // action object|action class|simple name|action ID|waza ID|waza name
        if (fields.Length >= 14 &&
            fields[0].Equals("Q", StringComparison.OrdinalIgnoreCase) &&
            TryParseTimestamp(fields[1], out double actionTimestamp) &&
            int.TryParse(
                fields[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int actionSequence))
        {
            ProcessPalActionEvent(
                actionTimestamp,
                actionSequence,
                fields[3],
                fields[4],
                fields[5],
                fields[6],
                fields[7],
                fields[10],
                fields[12],
                fields[13]
            );
            return;
        }

        // Rich damage metadata:
        // M|time|sequence|hook|actual|raw|raw attacker|resolved attacker|
        // defender|source type|source name|element|weapon|body part|
        // base power|ignore equip|cannot kill
        if (fields.Length >= 17 &&
            (fields[0].Equals("M", StringComparison.OrdinalIgnoreCase) ||
             fields[0].Equals(
                 "METADATA",
                 StringComparison.OrdinalIgnoreCase)) &&
            TryParseTimestamp(fields[1], out double metadataTimestamp) &&
            int.TryParse(
                fields[4],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int actualDamage) &&
            int.TryParse(
                fields[5],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int rawDamage) &&
            int.TryParse(
                fields[11],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int elementType) &&
            int.TryParse(
                fields[12],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int weaponType) &&
            int.TryParse(
                fields[13],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int bodyPartType) &&
            int.TryParse(
                fields[14],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int basePower))
        {
            ProcessDamageMetadataEvent(
                metadataTimestamp,
                actualDamage,
                rawDamage,
                fields[6],
                fields[7],
                fields[8],
                elementType,
                weaponType,
                bodyPartType,
                basePower
            );
            return;
        }

        // Exact source correlation:
        // C|time|sequence|source type|attacker actor|attacker name|
        // defender|damage|source label|identity|state|age|base power|
        // body part|metadata weapon type
        if (fields.Length >= 15 &&
            fields[0].Equals("C", StringComparison.OrdinalIgnoreCase) &&
            TryParseTimestamp(fields[1], out double correlationTimestamp) &&
            int.TryParse(
                fields[7],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int correlatedDamage) &&
            int.TryParse(
                fields[12],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int correlatedBasePower) &&
            int.TryParse(
                fields[13],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int correlatedBodyPart))
        {
            ProcessSourceCorrelationEvent(
                correlationTimestamp,
                fields[3],
                fields[4],
                fields[5],
                fields[6],
                correlatedDamage,
                fields[8],
                fields[9],
                fields[10],
                correlatedBasePower,
                correlatedBodyPart
            );
            return;
        }

        // Partner-skill player-hit correlation:
        // B|time|sequence|player actor|player name|defender|damage|
        // base power|body part|metadata weapon type|weapon name|
        // weapon identity|partner Pal actor|partner Pal name|
        // internal skill|localized partner skill|...|classification
        if (fields.Length >= 26 &&
            fields[0].Equals("B", StringComparison.OrdinalIgnoreCase) &&
            TryParseTimestamp(fields[1], out double partnerTimestamp) &&
            int.TryParse(
                fields[6],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int partnerDamage) &&
            int.TryParse(
                fields[7],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int partnerBasePower) &&
            int.TryParse(
                fields[8],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int partnerBodyPart) &&
            int.TryParse(
                fields[9],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int partnerMetadataWeaponType))
        {
            ProcessPartnerBonusEvent(
                partnerTimestamp,
                fields[3],
                fields[4],
                fields[5],
                partnerDamage,
                partnerBasePower,
                partnerBodyPart,
                partnerMetadataWeaponType,
                fields[12],
                fields[13],
                fields[14],
                fields[15],
                fields[25]
            );
            return;
        }

        // Exact Burn/Poison damage:
        // T|time|sequence|defender|actual|raw|status ID|status name|
        // source type|source actor|source name|source label|identity|...|
        // match method|active count|active names|generation
        if (fields.Length >= 23 &&
            fields[0].Equals("T", StringComparison.OrdinalIgnoreCase) &&
            TryParseTimestamp(fields[1], out double statusTimestamp) &&
            int.TryParse(
                fields[4],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int statusDamage) &&
            int.TryParse(
                fields[22],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int statusGeneration))
        {
            ProcessStatusDamageEvent(
                statusTimestamp,
                statusDamage,
                fields[3],
                fields[6],
                fields[7],
                fields[8],
                fields[9],
                fields[10],
                fields[11],
                fields[12],
                fields[19],
                statusGeneration
            );
            return;
        }

        // Current format: D|timestamp|damage|attacker|defender
        if (fields.Length >= 5 &&
            (fields[0].Equals("D", StringComparison.OrdinalIgnoreCase) ||
             fields[0].Equals("DAMAGE", StringComparison.OrdinalIgnoreCase)) &&
            TryParseTimestamp(fields[1], out double damageTimestamp) &&
            int.TryParse(
                fields[2],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int damage))
        {
            string? sourceType = fields.Length >= 6
                ? fields[5]
                : null;

            string? sourceName = fields.Length >= 7
                ? fields[6]
                : null;

            ProcessDamageEvent(
                damageTimestamp,
                damage,
                fields[3],
                fields[4],
                sourceType,
                sourceName
            );
            return;
        }

        // Active Pal format: P|timestamp|ACTIVE|actor|display name
        if (fields.Length >= 5 &&
            (fields[0].Equals("P", StringComparison.OrdinalIgnoreCase) ||
             fields[0].Equals("PAL", StringComparison.OrdinalIgnoreCase)) &&
            TryParseTimestamp(fields[1], out _))
        {
            ProcessPalStateEvent(
                fields[2],
                fields[3],
                fields[4]
            );
            return;
        }

        // Pal ownership format:
        // O|timestamp|pal actor|owner player actor|owner display name
        if (fields.Length >= 5 &&
            (fields[0].Equals("O", StringComparison.OrdinalIgnoreCase) ||
             fields[0].Equals("OWNER", StringComparison.OrdinalIgnoreCase)) &&
            TryParseTimestamp(fields[1], out _))
        {
            ProcessOwnershipEvent(
                fields[2],
                fields[3],
                fields[4]
            );
            return;
        }

        // Death format: X|timestamp|defender
        if (fields.Length >= 3 &&
            (fields[0].Equals("X", StringComparison.OrdinalIgnoreCase) ||
             fields[0].Equals("DEATH", StringComparison.OrdinalIgnoreCase)) &&
            TryParseTimestamp(fields[1], out _))
        {
            ProcessDeathEvent(fields[2]);
        }
    }

    private static bool TryParseTimestamp(string text, out double timestamp)
    {
        return double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out timestamp
        );
    }

    private void ProcessDamageEvent(
        double telemetryTimestamp,
        int damage,
        string attacker,
        string defender,
        string? sourceType,
        string? sourceName)
    {
        if (damage <= 0 ||
            string.IsNullOrWhiteSpace(attacker) ||
            string.IsNullOrWhiteSpace(defender))
        {
            return;
        }

        if (_encounterPaused)
        {
            return;
        }

        bool attackerIsPlayer = IsPlayerActor(attacker) ||
            string.Equals(
                sourceType,
                "PLAYER",
                StringComparison.OrdinalIgnoreCase
            );

        bool attackerIsActivePal = string.Equals(
            sourceType,
            "PAL",
            StringComparison.OrdinalIgnoreCase
        );

        bool attackerIsRaidPal = string.Equals(
            sourceType,
            "RAID_PAL",
            StringComparison.OrdinalIgnoreCase
        );

        bool attackerIsPal =
            attackerIsActivePal || attackerIsRaidPal;

        bool defenderIsPlayer = IsPlayerActor(defender);
        double nowSeconds = _applicationClock.Elapsed.TotalSeconds;

        // Damage dealt by the active target also counts as combat activity.
        // This prevents the fallback timer from ending during a defensive
        // stretch where the boss is attacking but players are not dealing
        // damage.
        if (_activeTargetName is not null &&
            (string.Equals(
                 attacker,
                 _activeTargetName,
                 StringComparison.Ordinal) ||
             string.Equals(
                 defender,
                 _activeTargetName,
                 StringComparison.Ordinal)))
        {
            _lastTargetActivitySeconds = nowSeconds;
        }

        // Either a player or a deployed Pal may start a new parse.
        bool canAcquireInitialTarget =
            (attackerIsPlayer || attackerIsPal) &&
            !defenderIsPlayer;

        if (_activeTargetName is null)
        {
            if (!canAcquireInitialTarget)
            {
                return;
            }

            AcquireTarget(defender);
        }
        else
        {
            bool isDifferentTarget = !string.Equals(
                defender,
                _activeTargetName,
                StringComparison.Ordinal
            );

            if (isDifferentTarget)
            {
                // Restore deliberate target switching. A player may change
                // the active target at any time. A Pal may only acquire the
                // next target after the previous encounter has completed,
                // which limits incidental Pal AoE target thrashing.
                bool canSwitchTarget =
                    (attackerIsPlayer && !defenderIsPlayer) ||
                    (_encounterComplete &&
                     attackerIsPal &&
                     !defenderIsPlayer);

                if (canSwitchTarget)
                {
                    AcquireTarget(defender);
                }
                else
                {
                    return;
                }
            }
            else if (_encounterComplete)
            {
                return;
            }
        }

        if (_encounterComplete ||
            !string.Equals(
                defender,
                _activeTargetName,
                StringComparison.Ordinal))
        {
            return;
        }

        if (!_encounterActive)
        {
            StartEncounter(nowSeconds);
        }

        _lastTargetActivitySeconds = nowSeconds;
        _totalDamage += damage;

        if (attackerIsPlayer)
        {
            _playerDamage += damage;

            string resolvedPlayerName = NormalizeSourceName(sourceName);

            if (!string.IsNullOrWhiteSpace(resolvedPlayerName))
            {
                _playerDisplayName = resolvedPlayerName;
            }

            RecordCombatantDamage(
                attacker,
                "PLAYER",
                sourceName,
                damage
            );

            RecordPendingDamageSource(
                telemetryTimestamp,
                damage,
                attacker,
                defender,
                "PLAYER",
                sourceName
            );
        }
        else if (attackerIsPal)
        {
            // Local active Pals and raid-base/remote Pals all contribute to
            // Pal and Combined DPS. Only the local active Pal updates the
            // active-Pal state used by P telemetry.
            _palDamage += damage;

            if (attackerIsActivePal)
            {
                _activePalActorName = attacker;
                _activePalStateKnown = true;
                UpdatePalDisplayName(sourceName, attacker);

                if (!string.IsNullOrWhiteSpace(_localPlayerActorId) &&
                    !string.IsNullOrWhiteSpace(_localPlayerDisplayName))
                {
                    AssignPalOwner(
                        attacker,
                        _localPlayerActorId,
                        _localPlayerDisplayName
                    );
                }
            }

            CacheCombatantName(attacker, sourceName);

            string trackedPalType =
                attackerIsRaidPal ? "RAID_PAL" : "PAL";

            RecordCombatantDamage(
                attacker,
                trackedPalType,
                sourceName,
                damage
            );

            RecordPendingDamageSource(
                telemetryTimestamp,
                damage,
                attacker,
                defender,
                trackedPalType,
                sourceName
            );
        }
    }

    private void RecordPendingDamageSource(
        double telemetryTimestamp,
        int damage,
        string attacker,
        string defender,
        string sourceType,
        string? sourceName)
    {
        RemoveExpiredDamageMetadata(telemetryTimestamp);

        string fallbackKey = AddDamageSource(
            attacker,
            sourceType,
            sourceName,
            "Unclassified",
            damage,
            bodyPartType: null
        );

        _pendingDamageMetadata.Add(
            new PendingDamageMetadataMatch(
                telemetryTimestamp,
                damage,
                attacker,
                defender,
                sourceType,
                sourceName,
                fallbackKey
            )
        );

        if (_pendingDamageMetadata.Count > 128)
        {
            _pendingDamageMetadata.RemoveRange(
                0,
                _pendingDamageMetadata.Count - 128
            );
        }
    }

    private void ProcessPalActionEvent(
        double telemetryTimestamp,
        int sequence,
        string phase,
        string actor,
        string sourceType,
        string sourceName,
        string target,
        string actionInstance,
        string skillId,
        string skillName)
    {
        RemoveExpiredPalSkillActivations(telemetryTimestamp);

        if (!phase.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
            !IsPalSourceType(sourceType) ||
            !IsMeaningfulTelemetryValue(actor) ||
            !IsMeaningfulTelemetryValue(target) ||
            !IsMeaningfulPalSkillName(skillName))
        {
            return;
        }

        string normalizedSkillName = NormalizePalSkillLabel(skillName);
        string normalizedPalName = NormalizeSourceName(sourceName);

        // Some UE4SS hook paths can report the same action more than once.
        // Treat matching actor/skill/action records within a small window as
        // one cast rather than inflating the activation count.
        bool duplicate = _recentPalSkillActivations.Any(candidate =>
            candidate.ActorId.Equals(actor, StringComparison.Ordinal) &&
            candidate.SkillName.Equals(
                normalizedSkillName,
                StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(candidate.TelemetryTimestamp - telemetryTimestamp) <= 0.25 &&
            (!IsMeaningfulTelemetryValue(actionInstance) ||
             !IsMeaningfulTelemetryValue(candidate.ActionInstance) ||
             candidate.ActionInstance.Equals(
                 actionInstance,
                 StringComparison.Ordinal)));

        if (duplicate)
        {
            return;
        }

        CacheCombatantName(actor, normalizedPalName);

        PendingPalSkillActivation activation = new(
            telemetryTimestamp,
            sequence,
            actor,
            sourceType,
            normalizedPalName,
            target,
            actionInstance,
            skillId,
            normalizedSkillName
        );

        _recentPalSkillActivations.Add(activation);

        if (_recentPalSkillActivations.Count > 256)
        {
            _recentPalSkillActivations.RemoveRange(
                0,
                _recentPalSkillActivations.Count - 256
            );
        }

        if (_encounterActive &&
            !_encounterPaused &&
            _activeTargetName is not null &&
            target.Equals(_activeTargetName, StringComparison.Ordinal))
        {
            CountPalSkillActivation(activation);
            UpdateInterface();
        }
    }

    private void EnsurePalSkillActivationCounted(
        double telemetryTimestamp,
        string actor,
        string sourceType,
        string sourceName,
        string target,
        string skillId,
        string skillName)
    {
        if (!IsPalSourceType(sourceType) ||
            !IsMeaningfulPalSkillName(skillName))
        {
            return;
        }

        RemoveExpiredPalSkillActivations(telemetryTimestamp);

        string normalizedSkillName = NormalizePalSkillLabel(skillName);
        string normalizedPalName = NormalizeSourceName(sourceName);

        RegisterPalSkill(
            actor,
            sourceType,
            normalizedPalName,
            skillId,
            normalizedSkillName
        );

        PendingPalSkillActivation? activation =
            _recentPalSkillActivations
                .Where(candidate =>
                    !candidate.Counted &&
                    candidate.ActorId.Equals(actor, StringComparison.Ordinal) &&
                    candidate.TargetActorId.Equals(target, StringComparison.Ordinal) &&
                    (candidate.SkillName.Equals(
                         normalizedSkillName,
                         StringComparison.OrdinalIgnoreCase) ||
                     (IsMeaningfulTelemetryValue(skillId) &&
                      candidate.SkillId.Equals(
                          skillId,
                          StringComparison.OrdinalIgnoreCase))) &&
                    telemetryTimestamp - candidate.TelemetryTimestamp >= -0.25 &&
                    telemetryTimestamp - candidate.TelemetryTimestamp <= 15.0)
                .OrderByDescending(candidate => candidate.TelemetryTimestamp)
                .FirstOrDefault();

        if (activation is not null)
        {
            CountPalSkillActivation(activation);
        }
    }

    private void CountPalSkillActivation(
        PendingPalSkillActivation activation)
    {
        if (activation.Counted)
        {
            return;
        }

        PalSkillRuntimeEntry entry = RegisterPalSkill(
            activation.ActorId,
            activation.SourceType,
            activation.PalName,
            activation.SkillId,
            activation.SkillName
        );

        entry.CastCount++;
        activation.Counted = true;
    }

    private PalSkillRuntimeEntry RegisterPalSkill(
        string actor,
        string sourceType,
        string palName,
        string skillId,
        string skillName)
    {
        string normalizedSkillName = NormalizePalSkillLabel(skillName);
        string key = BuildPalSkillKey(actor, normalizedSkillName);

        if (!_palSkillStats.TryGetValue(
                key,
                out PalSkillRuntimeEntry? entry))
        {
            entry = new PalSkillRuntimeEntry(
                actor,
                sourceType,
                NormalizeSourceName(palName),
                IsMeaningfulTelemetryValue(skillId)
                    ? skillId
                    : string.Empty,
                normalizedSkillName,
                _nextPalSkillOrder++
            );

            _palSkillStats.Add(key, entry);
        }
        else
        {
            if (IsMeaningfulTelemetryValue(palName))
            {
                entry.PalName = NormalizeSourceName(palName);
            }

            if (IsMeaningfulTelemetryValue(skillId))
            {
                entry.SkillId = skillId;
            }
        }

        return entry;
    }

    private static bool IsPalSourceType(string sourceType)
    {
        return sourceType.Equals("PAL", StringComparison.OrdinalIgnoreCase) ||
            sourceType.Equals("RAID_PAL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMeaningfulPalSkillName(string? skillName)
    {
        if (!IsMeaningfulTelemetryValue(skillName))
        {
            return false;
        }

        string value = skillName!.Trim();
        return !value.Equals(
                   "ACTION_SKILL_None",
                   StringComparison.OrdinalIgnoreCase) &&
               !value.Equals(
                   "Unclassified",
                   StringComparison.OrdinalIgnoreCase) &&
               !value.StartsWith(
                   "Unclassified ·",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePalSkillLabel(string? sourceLabel)
    {
        string value = (sourceLabel ?? string.Empty).Trim();
        string[] hitRegionSuffixes =
        {
            " — Weak Point",
            " — Normal",
            " — Strong",
            " — Invincible"
        };

        foreach (string suffix in hitRegionSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return value[..^suffix.Length].TrimEnd();
            }
        }

        return value;
    }

    private static string BuildPalSkillKey(
        string actor,
        string skillName)
    {
        return $"{actor}|{NormalizePalSkillLabel(skillName)}";
    }

    private void RemoveExpiredPalSkillActivations(
        double currentTelemetryTimestamp)
    {
        _recentPalSkillActivations.RemoveAll(candidate =>
            currentTelemetryTimestamp - candidate.TelemetryTimestamp > 20.0
        );
    }

    private void ProcessDamageMetadataEvent(
        double telemetryTimestamp,
        int actualDamage,
        int rawDamage,
        string rawAttacker,
        string resolvedAttacker,
        string defender,
        int elementType,
        int weaponType,
        int bodyPartType,
        int basePower)
    {
        if (actualDamage <= 0)
        {
            return;
        }

        RemoveExpiredDamageMetadata(telemetryTimestamp);

        PendingDamageMetadataMatch? match =
            FindPendingDamageMatch(
                telemetryTimestamp,
                actualDamage,
                rawAttacker,
                resolvedAttacker,
                defender
            );

        if (match is null)
        {
            return;
        }

        string sourceLabel = BuildDamageSourceLabel(
            match.AttributedSourceType,
            weaponType,
            elementType,
            basePower
        );

        FinalizePendingDamageSource(
            match,
            sourceLabel,
            bodyPartType
        );
    }

    private void ProcessSourceCorrelationEvent(
        double telemetryTimestamp,
        string sourceType,
        string attacker,
        string sourceName,
        string defender,
        int damage,
        string sourceLabel,
        string identity,
        string state,
        int basePower,
        int bodyPartType)
    {
        if (damage <= 0)
        {
            return;
        }

        RemoveExpiredDamageMetadata(telemetryTimestamp);

        PendingDamageMetadataMatch? match =
            FindPendingDamageMatch(
                telemetryTimestamp,
                damage,
                attacker,
                attacker,
                defender
            );

        if (match is null)
        {
            return;
        }

        if (!match.HasAttributionOverride &&
            IsPalSourceType(sourceType) &&
            IsMeaningfulPalSkillName(sourceLabel))
        {
            EnsurePalSkillActivationCounted(
                telemetryTimestamp,
                attacker,
                sourceType,
                sourceName,
                defender,
                identity,
                sourceLabel
            );
        }

        // A confirmed partner-skill B record may already have reassigned
        // this hit from the player to the originating Pal. Do not let the
        // later generic C weapon label overwrite that stronger attribution.
        if (!match.HasAttributionOverride)
        {
            match.AttributedActor = attacker;
            match.AttributedSourceType = sourceType;
            match.AttributedSourceName = sourceName;
            match.ExactSourceLabel = IsMeaningfulTelemetryValue(sourceLabel)
                ? sourceLabel
                : null;
        }

        if (!match.HasAttributionOverride &&
            !IsMeaningfulTelemetryValue(sourceLabel))
        {
            // Leave the pending hit for the following M record, which still
            // contains element and BasePower fallback metadata.
            return;
        }

        string resolvedLabel =
            match.ExactSourceLabel ??
            sourceLabel;

        FinalizePendingDamageSource(
            match,
            resolvedLabel,
            bodyPartType
        );
    }

    private void ProcessPartnerBonusEvent(
        double telemetryTimestamp,
        string playerActor,
        string playerName,
        string defender,
        int damage,
        int basePower,
        int bodyPartType,
        int metadataWeaponType,
        string partnerPalActor,
        string partnerPalName,
        string internalSkillName,
        string localizedPartnerSkillName,
        string classification)
    {
        bool confirmedPartnerBonus =
            classification.Equals(
                "PARTNER_BONUS_CONFIRMED",
                StringComparison.OrdinalIgnoreCase) ||
            classification.Equals(
                "PARTNER_BONUS_CANDIDATE",
                StringComparison.OrdinalIgnoreCase);

        if (!confirmedPartnerBonus ||
            damage <= 0 ||
            metadataWeaponType != 0 ||
            !IsMeaningfulTelemetryValue(partnerPalActor) ||
            !IsMeaningfulTelemetryValue(partnerPalName) ||
            !IsMeaningfulTelemetryValue(localizedPartnerSkillName))
        {
            return;
        }

        RemoveExpiredDamageMetadata(telemetryTimestamp);

        PendingDamageMetadataMatch? match =
            FindPendingDamageMatch(
                telemetryTimestamp,
                damage,
                playerActor,
                playerActor,
                defender
            );

        if (match is null || match.AggregateReassigned)
        {
            return;
        }

        match.HasAttributionOverride = true;
        match.AttributedActor = partnerPalActor;
        match.AttributedSourceType = "PAL";
        match.AttributedSourceName = partnerPalName;
        match.ExactSourceLabel = localizedPartnerSkillName;
        match.AggregateReassigned = true;

        CacheCombatantName(partnerPalActor, partnerPalName);

        TransferPlayerDamageToPal(
            playerActor,
            partnerPalActor,
            partnerPalName,
            damage
        );
    }

    private void ProcessStatusDamageEvent(
        double telemetryTimestamp,
        int damage,
        string defender,
        string statusId,
        string statusName,
        string sourceType,
        string sourceActor,
        string sourceName,
        string sourceLabel,
        string sourceIdentity,
        string matchMethod,
        int generation)
    {
        if (damage <= 0 ||
            _encounterPaused ||
            !IsMeaningfulTelemetryValue(sourceActor) ||
            !IsMeaningfulTelemetryValue(sourceType))
        {
            return;
        }

        bool sourceIsPlayer = sourceType.Equals(
            "PLAYER",
            StringComparison.OrdinalIgnoreCase
        );

        bool sourceIsPal =
            sourceType.Equals(
                "PAL",
                StringComparison.OrdinalIgnoreCase) ||
            sourceType.Equals(
                "RAID_PAL",
                StringComparison.OrdinalIgnoreCase);

        if (!sourceIsPlayer && !sourceIsPal)
        {
            return;
        }

        bool defenderIsPlayer = IsPlayerActor(defender);
        double nowSeconds = _applicationClock.Elapsed.TotalSeconds;

        if (_activeTargetName is null)
        {
            if (defenderIsPlayer)
            {
                return;
            }

            AcquireTarget(defender);
        }
        else if (!string.Equals(
                     defender,
                     _activeTargetName,
                     StringComparison.Ordinal))
        {
            return;
        }

        if (_encounterComplete)
        {
            return;
        }

        if (!_encounterActive)
        {
            StartEncounter(nowSeconds);
        }

        _lastTargetActivitySeconds = nowSeconds;
        _totalDamage += damage;

        string normalizedSourceName =
            NormalizeSourceName(sourceName);

        if (sourceIsPlayer)
        {
            _playerDamage += damage;
            RecordCombatantDamage(
                sourceActor,
                "PLAYER",
                normalizedSourceName,
                damage
            );
        }
        else
        {
            _palDamage += damage;
            RecordCombatantDamage(
                sourceActor,
                sourceType,
                normalizedSourceName,
                damage
            );
        }

        string resolvedStatusName =
            IsMeaningfulTelemetryValue(statusName)
                ? statusName
                : $"Status {statusId}";

        string resolvedSourceLabel =
            IsMeaningfulTelemetryValue(sourceLabel)
                ? sourceLabel
                : "Unknown Source";

        string exactLabel =
            $"{resolvedSourceLabel} · {resolvedStatusName}";

        AddDamageSource(
            sourceActor,
            sourceType,
            normalizedSourceName,
            exactLabel,
            damage,
            bodyPartType: null
        );

        UpdateInterface();
    }

    private PendingDamageMetadataMatch? FindPendingDamageMatch(
        double telemetryTimestamp,
        int damage,
        string rawAttacker,
        string resolvedAttacker,
        string defender)
    {
        return _pendingDamageMetadata
            .Where(candidate =>
                candidate.Damage == damage &&
                string.Equals(
                    candidate.Defender,
                    defender,
                    StringComparison.Ordinal) &&
                (string.Equals(
                     candidate.OriginalAttacker,
                     rawAttacker,
                     StringComparison.Ordinal) ||
                 string.Equals(
                     candidate.OriginalAttacker,
                     resolvedAttacker,
                     StringComparison.Ordinal)) &&
                Math.Abs(
                    candidate.TelemetryTimestamp -
                    telemetryTimestamp) <= 0.35)
            .OrderBy(candidate =>
                Math.Abs(
                    candidate.TelemetryTimestamp -
                    telemetryTimestamp))
            .FirstOrDefault();
    }

    private void FinalizePendingDamageSource(
        PendingDamageMetadataMatch match,
        string sourceLabel,
        int? bodyPartType)
    {
        _pendingDamageMetadata.Remove(match);

        RemoveDamageSourceAmount(
            match.FallbackSourceKey,
            match.Damage
        );

        AddDamageSource(
            match.AttributedActor,
            match.AttributedSourceType,
            match.AttributedSourceName,
            sourceLabel,
            match.Damage,
            bodyPartType
        );

        UpdateInterface();
    }

    private void TransferPlayerDamageToPal(
        string playerActor,
        string palActor,
        string palName,
        int damage)
    {
        _playerDamage = Math.Max(_playerDamage - damage, 0);
        _palDamage += damage;

        RemoveCombatantDamage(
            playerActor,
            "PLAYER",
            damage
        );

        RecordCombatantDamage(
            palActor,
            "PAL",
            palName,
            damage
        );
    }

    private void RemoveCombatantDamage(
        string actor,
        string sourceType,
        int damage)
    {
        string key = $"{sourceType}|{actor}";

        if (!_combatants.TryGetValue(
                key,
                out CombatantEntry? combatant))
        {
            return;
        }

        combatant.Damage = Math.Max(
            combatant.Damage - damage,
            0
        );

        if (combatant.Damage == 0)
        {
            _combatants.Remove(key);
        }
    }

    private static bool IsMeaningfulTelemetryValue(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !value.Equals(
                   "unknown",
                   StringComparison.OrdinalIgnoreCase) &&
               !value.Equals(
                   "unresolved",
                   StringComparison.OrdinalIgnoreCase) &&
               !value.Equals(
                   "invalid",
                   StringComparison.OrdinalIgnoreCase) &&
               !value.Equals(
                   "none",
                   StringComparison.OrdinalIgnoreCase);
    }

    private void RemoveExpiredDamageMetadata(
        double currentTelemetryTimestamp)
    {
        _pendingDamageMetadata.RemoveAll(candidate =>
            currentTelemetryTimestamp -
            candidate.TelemetryTimestamp > 1.0
        );
    }

    private string AddDamageSource(
        string actor,
        string sourceType,
        string? sourceName,
        string sourceLabel,
        int damage,
        int? bodyPartType)
    {
        string bodyPartKey =
            bodyPartType?.ToString(
                CultureInfo.InvariantCulture) ??
            "STATUS";

        string displayLabel = sourceLabel;
        string hitRegion = GetHitRegionDisplayName(bodyPartType);

        if (!string.IsNullOrWhiteSpace(hitRegion))
        {
            displayLabel = $"{sourceLabel} — {hitRegion}";
        }

        string key =
            $"{sourceType}|{actor}|{sourceLabel}|{bodyPartKey}";

        if (!_damageSources.TryGetValue(
                key,
                out DamageSourceEntry? entry))
        {
            entry = new DamageSourceEntry(
                actor,
                sourceType,
                NormalizeSourceName(sourceName),
                displayLabel,
                _nextDamageSourceOrder++
            );

            _damageSources.Add(key, entry);
        }

        entry.Damage += damage;
        entry.HitCount++;

        if (bodyPartType == 0)
        {
            entry.WeakHitCount++;
        }
        else if (bodyPartType == 2)
        {
            entry.StrongHitCount++;
        }

        return key;
    }

    private void RemoveDamageSourceAmount(
        string sourceKey,
        int damage)
    {
        if (!_damageSources.TryGetValue(
                sourceKey,
                out DamageSourceEntry? entry))
        {
            return;
        }

        entry.Damage = Math.Max(
            entry.Damage - damage,
            0
        );
        entry.HitCount = Math.Max(
            entry.HitCount - 1,
            0
        );

        if (entry.Damage == 0)
        {
            _damageSources.Remove(sourceKey);
        }
    }

    private static string BuildDamageSourceLabel(
        string sourceType,
        int weaponType,
        int elementType,
        int basePower)
    {
        string elementName =
            GetElementDisplayName(elementType);

        if (weaponType > 0)
        {
            string weaponName =
                GetWeaponDisplayName(weaponType);

            return elementType > 0
                ? $"{weaponName} · {elementName}"
                : weaponName;
        }

        bool isPal = sourceType.Contains(
            "PAL",
            StringComparison.OrdinalIgnoreCase
        );

        if (isPal)
        {
            string powerText = basePower > 0
                ? $" · {basePower} Power"
                : string.Empty;

            return $"{elementName} Skill{powerText}";
        }

        if (elementType > 0)
        {
            string powerText = basePower > 0
                ? $" · {basePower} Power"
                : string.Empty;

            return $"{elementName} Effect{powerText}";
        }

        return basePower > 0
            ? $"Unclassified · {basePower} Power"
            : "Unclassified";
    }

    private static string GetHitRegionDisplayName(
        int? bodyPartType)
    {
        return bodyPartType switch
        {
            0 => "Weak Point",
            1 => "Normal",
            2 => "Strong",
            3 => "Invincible",
            _ => string.Empty
        };
    }

    private static string GetWeaponDisplayName(int weaponType)
    {
        return weaponType switch
        {
            1 => "Throw Object",
            2 => "Handgun",
            3 => "Assault Rifle",
            4 => "Shotgun",
            5 => "Sniper Rifle",
            6 => "Rocket Launcher",
            7 => "Melee Weapon",
            8 => "Bow",
            9 => "Bow Gun",
            10 => "Flamethrower",
            11 => "Gatling Gun",
            12 => "Lifted Object",
            13 => "Laser Rifle",
            14 => "Missile Launcher",
            15 => "Grenade Launcher",
            16 => "Katana",
            17 => "Metal Detector",
            18 => "Giant Club",
            19 => "Fishing Rod",
            20 => "Laser Mining Tool",
            _ => $"Weapon {weaponType}"
        };
    }

    private static string GetElementDisplayName(int elementType)
    {
        return elementType switch
        {
            0 => "Neutral",
            1 => "Normal",
            2 => "Fire",
            3 => "Water",
            4 => "Grass",
            5 => "Electric",
            6 => "Ice",
            7 => "Ground",
            8 => "Dark",
            9 => "Dragon",
            _ => $"Element {elementType}"
        };
    }

    private void RenderDamageSourceRows()
    {
        var groups = _damageSources.Values
            .Where(source => source.Damage > 0)
            .GroupBy(
                source => source.ActorId,
                StringComparer.Ordinal
            )
            .Select(group => new
            {
                ActorId = group.Key,
                DisplayName = GetDamageSourceActorName(group),
                Damage = group.Sum(source => source.Damage),
                FirstSeenOrder = group.Min(
                    source => source.FirstSeenOrder),
                Sources = group
                    .OrderByDescending(source => source.Damage)
                    .ThenBy(source => source.FirstSeenOrder)
                    .ToList()
            })
            .OrderByDescending(group => group.Damage)
            .ThenBy(group => group.FirstSeenOrder)
            .ToList();

        List<DamageSourceDisplayRow> rows = new();

        foreach (var group in groups)
        {
            rows.Add(
                DamageSourceDisplayRow.CreateCombatant(
                    group.DisplayName,
                    group.Damage
                )
            );

            foreach (DamageSourceEntry source in group.Sources)
            {
                double percentage = group.Damage > 0
                    ? source.Damage * 100.0 / group.Damage
                    : 0;

                rows.Add(
                    DamageSourceDisplayRow.CreateSource(
                        source.SourceLabel,
                        source.Damage,
                        percentage
                    )
                );
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(
                DamageSourceDisplayRow.CreatePlaceholder(
                    "No source data yet"
                )
            );
        }

        DamageSourceRowsControl.ItemsSource = rows;
    }

    private void RenderPalSkillRows()
    {
        List<EncounterPalSkillSnapshot> skills = BuildPalSkillSnapshots();

        var groups = skills
            .GroupBy(skill => skill.ActorId, StringComparer.Ordinal)
            .Select(group => new
            {
                PalName = group
                    .Select(skill => skill.PalName)
                    .FirstOrDefault(name =>
                        !string.IsNullOrWhiteSpace(name)) ?? "Unknown Pal",
                Damage = group.Sum(skill => skill.Damage),
                FirstSeenOrder = group.Min(skill => skill.FirstSeenOrder),
                Skills = group
                    .OrderByDescending(skill => skill.Damage)
                    .ThenBy(skill => skill.FirstSeenOrder)
                    .ToList()
            })
            .OrderByDescending(group => group.Damage)
            .ThenBy(group => group.FirstSeenOrder)
            .ToList();

        List<PalSkillDisplayRow> rows = new();

        foreach (var group in groups)
        {
            rows.Add(PalSkillDisplayRow.CreatePal(
                group.PalName,
                group.Damage));

            foreach (EncounterPalSkillSnapshot skill in group.Skills)
            {
                rows.Add(PalSkillDisplayRow.CreateSkill(
                    skill.SkillName,
                    skill.Damage,
                    skill.HitCount,
                    skill.CastCount,
                    skill.AverageDamagePerCast));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(PalSkillDisplayRow.CreatePlaceholder(
                "No attributed Pal skills yet"));
        }

        PalSkillRowsControl.ItemsSource = rows;
    }

    private string GetDamageSourceActorName(
        IEnumerable<DamageSourceEntry> sources)
    {
        DamageSourceEntry first = sources.First();

        if (_knownCombatantNames.TryGetValue(
                first.ActorId,
                out string? knownName))
        {
            return knownName;
        }

        string normalizedName =
            NormalizeSourceName(first.SourceName);

        return string.IsNullOrWhiteSpace(normalizedName)
            ? GetFriendlyActorName(first.ActorId)
            : GetFriendlyActorName(normalizedName);
    }

    private void ProcessLocalPlayerEvent(
        string playerActor,
        string playerDisplayName)
    {
        if (string.IsNullOrWhiteSpace(playerActor))
        {
            return;
        }

        string resolvedPlayerName =
            NormalizeSourceName(playerDisplayName);

        if (string.IsNullOrWhiteSpace(resolvedPlayerName))
        {
            resolvedPlayerName =
                GetFriendlyActorName(playerActor);
        }

        resolvedPlayerName =
            GetFriendlyActorName(resolvedPlayerName);

        _playerDisplayName = resolvedPlayerName;

        RememberLocalPlayer(
            playerActor,
            resolvedPlayerName
        );

        UpdateInterface();
    }

    private void ProcessActorNameEvent(
        string actor,
        string displayName,
        int priority)
    {
        CacheCombatantName(
            actor,
            displayName,
            priority
        );

        if (string.Equals(
                actor,
                _activePalActorName,
                StringComparison.Ordinal))
        {
            UpdatePalDisplayName(displayName, actor);
        }

        UpdateInterface();
    }

    private void ProcessOwnershipEvent(
        string palActor,
        string ownerActor,
        string ownerDisplayName)
    {
        if (string.IsNullOrWhiteSpace(palActor) ||
            string.IsNullOrWhiteSpace(ownerActor))
        {
            return;
        }

        string resolvedOwnerName = NormalizeSourceName(ownerDisplayName);

        if (string.IsNullOrWhiteSpace(resolvedOwnerName))
        {
            resolvedOwnerName = GetFriendlyActorName(ownerActor);
        }

        resolvedOwnerName = GetFriendlyActorName(resolvedOwnerName);

        AssignPalOwner(
            palActor,
            ownerActor,
            resolvedOwnerName
        );

        // Ownership can arrive before or after P|ACTIVE. When it arrives
        // afterward, this confirms which player is the local player.
        if (string.Equals(
                palActor,
                _activePalActorName,
                StringComparison.Ordinal))
        {
            RememberLocalPlayer(
                ownerActor,
                resolvedOwnerName
            );
        }

        UpdateInterface();
    }

    private void AssignPalOwner(
        string palActor,
        string ownerActor,
        string ownerDisplayName)
    {
        CacheCombatantName(ownerActor, ownerDisplayName);

        if (!_palOwners.TryGetValue(
                palActor,
                out PalOwnerInfo? ownerInfo))
        {
            ownerInfo = new PalOwnerInfo(
                ownerActor,
                ownerDisplayName
            );

            _palOwners.Add(palActor, ownerInfo);
        }
        else
        {
            ownerInfo.OwnerActorId = ownerActor;
            ownerInfo.OwnerDisplayName = ownerDisplayName;
        }

        foreach (CombatantEntry combatant in _combatants.Values)
        {
            if (string.Equals(
                    combatant.ActorId,
                    palActor,
                    StringComparison.Ordinal))
            {
                combatant.OwnerActorId = ownerActor;
                combatant.OwnerDisplayName = ownerDisplayName;
            }
        }
    }

    private void RememberLocalPlayer(
        string ownerActor,
        string ownerDisplayName)
    {
        _localPlayerActorId = ownerActor;
        _localPlayerDisplayName = ownerDisplayName;
        CacheCombatantName(
            ownerActor,
            ownerDisplayName,
            priority: 3
        );

        ApplyLocalPlayerOwnership();
    }

    private void ApplyLocalPlayerOwnership()
    {
        if (string.IsNullOrWhiteSpace(_localPlayerActorId) ||
            string.IsNullOrWhiteSpace(_localPlayerDisplayName))
        {
            return;
        }

        // SourceType PAL is emitted only for the local player's deployed
        // Otomo. RAID_PAL remains Trainer-owned because it may belong to a
        // remote player or be assigned at a base.
        List<string> localPalActors = _combatants.Values
            .Where(combatant =>
                combatant.SourceType.Equals(
                    "PAL",
                    StringComparison.OrdinalIgnoreCase))
            .Select(combatant => combatant.ActorId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrWhiteSpace(_activePalActorName) &&
            !localPalActors.Contains(
                _activePalActorName,
                StringComparer.Ordinal))
        {
            localPalActors.Add(_activePalActorName);
        }

        foreach (string palActor in localPalActors)
        {
            AssignPalOwner(
                palActor,
                _localPlayerActorId,
                _localPlayerDisplayName
            );
        }
    }

    private void ProcessPalStateEvent(
        string state,
        string actor,
        string displayName)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return;
        }

        _activePalStateKnown = true;

        if (state.Equals(
                "ACTIVE",
                StringComparison.OrdinalIgnoreCase))
        {
            SetActivePal(actor, displayName);
            return;
        }

        if (state.Equals(
                "INACTIVE",
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                actor,
                _activePalActorName,
                StringComparison.Ordinal))
        {
            _activePalActorName = null;

            // Do not erase the Pal's completed encounter contribution when
            // it is recalled or defeated.
            if (!_encounterActive &&
                !_encounterComplete &&
                _totalDamage == 0)
            {
                _palDisplayName = "No active Pal";
            }
        }
    }

    private void SetActivePal(string actor, string? displayName)
    {
        _activePalActorName = actor;
        CacheCombatantName(actor, displayName);
        UpdatePalDisplayName(displayName, actor);

        if (_palOwners.TryGetValue(
                actor,
                out PalOwnerInfo? confirmedOwner))
        {
            // O telemetry may be written before P|ACTIVE. In that order, the
            // active-Pal event is what proves this owner is the local player.
            RememberLocalPlayer(
                confirmedOwner.OwnerActorId,
                confirmedOwner.OwnerDisplayName
            );
        }
        else if (!string.IsNullOrWhiteSpace(_localPlayerActorId) &&
                 !string.IsNullOrWhiteSpace(_localPlayerDisplayName))
        {
            // Palbox retrieval creates a new temporary actor ID. P|ACTIVE is
            // authoritative for the local deployed Pal, so assign it to the
            // previously confirmed local team immediately.
            AssignPalOwner(
                actor,
                _localPlayerActorId,
                _localPlayerDisplayName
            );
        }

        UpdateInterface();
    }

    private void UpdatePalDisplayName(
        string? displayName,
        string actor)
    {
        string resolvedName = NormalizeSourceName(displayName);

        _palDisplayName = string.IsNullOrWhiteSpace(resolvedName)
            ? GetFriendlyActorName(actor)
            : GetFriendlyActorName(resolvedName);
    }

    private void CacheCombatantName(
        string actor,
        string? displayName,
        int priority = 1)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return;
        }

        string resolvedName = NormalizeSourceName(displayName);

        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            return;
        }

        priority = Math.Clamp(priority, 1, 3);

        if (_knownActorNamePriorities.TryGetValue(
                actor,
                out int existingPriority) &&
            existingPriority > priority)
        {
            // Never let a cleaned codename or species fallback replace a
            // confirmed official name or custom nickname.
            return;
        }

        string friendlyName = GetFriendlyActorName(resolvedName);
        _knownCombatantNames[actor] = friendlyName;
        _knownActorNamePriorities[actor] = priority;

        foreach (CombatantEntry combatant in _combatants.Values)
        {
            if (string.Equals(
                    combatant.ActorId,
                    actor,
                    StringComparison.Ordinal))
            {
                combatant.DisplayName = friendlyName;

                if (combatant.SourceType.Equals(
                        "PLAYER",
                        StringComparison.OrdinalIgnoreCase))
                {
                    combatant.OwnerActorId = actor;
                    combatant.OwnerDisplayName = friendlyName;
                }
            }

            if (string.Equals(
                    combatant.OwnerActorId,
                    actor,
                    StringComparison.Ordinal))
            {
                combatant.OwnerDisplayName = friendlyName;
            }
        }

        foreach (PalOwnerInfo ownerInfo in _palOwners.Values)
        {
            if (string.Equals(
                    ownerInfo.OwnerActorId,
                    actor,
                    StringComparison.Ordinal))
            {
                ownerInfo.OwnerDisplayName = friendlyName;
            }
        }
    }

    private string GetKnownActorDisplayName(string actor)
    {
        return _knownCombatantNames.TryGetValue(
            actor,
            out string? displayName)
            ? displayName
            : GetFriendlyActorName(actor);
    }

    private void RecordCombatantDamage(
        string actor,
        string sourceType,
        string? sourceName,
        int damage)
    {
        CacheCombatantName(actor, sourceName);

        string key = $"{sourceType}|{actor}";

        if (!_combatants.TryGetValue(key, out CombatantEntry? combatant))
        {
            string displayName = _knownCombatantNames.TryGetValue(
                actor,
                out string? knownName)
                ? knownName
                : GetFriendlyActorName(actor);

            combatant = new CombatantEntry(
                actor,
                sourceType,
                displayName,
                _nextCombatantOrder++
            );

            _combatants.Add(key, combatant);
        }

        ApplyKnownOwnership(combatant);
        combatant.Damage += damage;
    }

    private void ApplyKnownOwnership(CombatantEntry combatant)
    {
        if (combatant.SourceType.Equals(
                "PLAYER",
                StringComparison.OrdinalIgnoreCase))
        {
            combatant.OwnerActorId = combatant.ActorId;
            combatant.OwnerDisplayName = combatant.DisplayName;
            return;
        }

        if (_palOwners.TryGetValue(
                combatant.ActorId,
                out PalOwnerInfo? ownerInfo))
        {
            combatant.OwnerActorId = ownerInfo.OwnerActorId;
            combatant.OwnerDisplayName = ownerInfo.OwnerDisplayName;
        }
    }

    private void UpdateCombatantDps(double combatDuration)
    {
        foreach (CombatantEntry combatant in _combatants.Values)
        {
            combatant.DisplayedDps = combatDuration > 0.01
                ? combatant.Damage / combatDuration
                : combatant.Damage;
        }
    }

    private void RenderCombatantRows()
    {
        const string unassignedOwnerKey = "__UNASSIGNED_PALS__";
        const string raidTeamOwnerKey = "__RAID_TEAM__";

        var ownerGroups = _combatants.Values
            .GroupBy(
                combatant =>
                    combatant.SourceType.Equals(
                        "RAID_PAL",
                        StringComparison.OrdinalIgnoreCase)
                        ? raidTeamOwnerKey
                        : string.IsNullOrWhiteSpace(
                            combatant.OwnerActorId)
                            ? unassignedOwnerKey
                            : combatant.OwnerActorId!,
                StringComparer.Ordinal
            )
            .Select(group => new
            {
                OwnerKey = group.Key,
                OwnerDisplayName =
                    group.Key == raidTeamOwnerKey
                        ? "Raid Team"
                        : group
                            .Select(combatant =>
                                combatant.OwnerDisplayName)
                            .FirstOrDefault(name =>
                                !string.IsNullOrWhiteSpace(name))
                            ?? "Unassigned Pals",
                DisplayedDps = group.Sum(
                    combatant => combatant.DisplayedDps),
                FirstSeenOrder = group.Min(
                    combatant => combatant.FirstSeenOrder),
                Combatants = group
                    .OrderByDescending(
                        combatant => combatant.DisplayedDps)
                    .ThenBy(
                        combatant => combatant.FirstSeenOrder)
                    .ToList()
            })
            .OrderByDescending(group => group.DisplayedDps)
            .ThenBy(group => group.FirstSeenOrder)
            .ToList();

        List<CombatantDisplayRow> rows = new();
        double totalDisplayedDps = Math.Max(
            ownerGroups.Sum(group => group.DisplayedDps),
            0.01
        );

        for (int groupIndex = 0;
             groupIndex < ownerGroups.Count;
             groupIndex++)
        {
            var ownerGroup = ownerGroups[groupIndex];

            string groupDisplayName =
                ownerGroup.OwnerKey == raidTeamOwnerKey
                    ? "Raid Team"
                    : ownerGroup.OwnerKey == unassignedOwnerKey
                        ? ownerGroup.OwnerDisplayName
                        : $"Team {ownerGroup.OwnerDisplayName}";

            double groupContribution =
                ownerGroup.DisplayedDps * 100.0 /
                totalDisplayedDps;

            rows.Add(CombatantDisplayRow.CreateOwnerGroup(
                groupDisplayName,
                ownerGroup.DisplayedDps,
                groupContribution,
                isLeadingGroup: groupIndex == 0
            ));

            foreach (CombatantEntry combatant in ownerGroup.Combatants)
            {
                double combatantContribution =
                    combatant.DisplayedDps * 100.0 /
                    totalDisplayedDps;

                rows.Add(CombatantDisplayRow.CreateCombatant(
                    combatant.DisplayName,
                    combatant.DisplayedDps,
                    combatantContribution,
                    combatant.SourceType
                ));
            }
        }

        CombatantRowsControl.ItemsSource = rows;
    }

    private void TryFinalizeInactiveEncounter()
    {
        if (!_encounterActive ||
            _encounterPaused ||
            _activeTargetName is null ||
            _lastTargetActivitySeconds <= 0)
        {
            return;
        }

        double inactiveSeconds =
            _applicationClock.Elapsed.TotalSeconds -
            _lastTargetActivitySeconds;

        if (inactiveSeconds < EncounterInactivityTimeoutSeconds)
        {
            return;
        }

        // The result is finalized without claiming confirmed zero HP because
        // no exact death event was received.
        StopEncounter(
            targetConfirmedDead: false,
            endReason: EncounterEndReasons.InactivityTimeout
        );
    }

    private void ProcessDeathEvent(string defender)
    {
        if ((!_encounterActive && !_encounterPaused) ||
            _activeTargetName is null ||
            !string.Equals(
                defender,
                _activeTargetName,
                StringComparison.Ordinal))
        {
            return;
        }

        StopEncounter(
            targetConfirmedDead: true,
            endReason: EncounterEndReasons.TargetDefeated
        );
    }

    private void AcquireTarget(string defender)
    {
        if ((_encounterActive || _encounterPaused) &&
            _totalDamage > 0)
        {
            StopEncounter(
                targetConfirmedDead: false,
                endReason: EncounterEndReasons.TargetChanged
            );
        }

        ResetEncounter(clearTarget: false);
        _activeTargetName = defender;
        _targetPlaceholder = "No target";
    }

    private void StartEncounter(double nowSeconds)
    {
        _encounterActive = true;
        _encounterPaused = false;
        _encounterComplete = false;
        _targetConfirmedDead = false;

        _playerDamage = 0;
        _palDamage = 0;
        _totalDamage = 0;

        _combatants.Clear();
        _damageSources.Clear();
        _palSkillStats.Clear();
        _pendingDamageMetadata.Clear();
        _nextCombatantOrder = 0;
        _nextDamageSourceOrder = 0;
        _nextPalSkillOrder = 0;

        _displayedCombinedDps = 0;
        _displayedPlayerDps = 0;
        _displayedPalDps = 0;
        _encounterStartSeconds = nowSeconds;
        _lastTargetActivitySeconds = nowSeconds;
        _finalizedDurationSeconds = 0;
        _encounterStartedAtUtc = DateTimeOffset.UtcNow;
        _encounterSnapshotSaved = false;
    }

    private void PauseEncounter()
    {
        if (!_encounterActive)
        {
            return;
        }

        _finalizedDurationSeconds = Math.Max(
            _applicationClock.Elapsed.TotalSeconds - _encounterStartSeconds,
            0.01
        );

        _encounterActive = false;
        _encounterPaused = true;

        RecalculateFinalDps();
        UpdateInterface();
    }

    private void ResumeEncounter()
    {
        if (!_encounterPaused ||
            _encounterComplete ||
            _activeTargetName is null)
        {
            return;
        }

        _encounterPaused = false;
        _encounterActive = true;

        // Continue from the previously accumulated active-combat duration,
        // excluding the time spent paused.
        _encounterStartSeconds =
            _applicationClock.Elapsed.TotalSeconds -
            _finalizedDurationSeconds;
        _lastTargetActivitySeconds =
            _applicationClock.Elapsed.TotalSeconds;

        UpdateInterface();
    }

    private void StopEncounter(
        bool targetConfirmedDead,
        string endReason)
    {
        if (!_encounterActive && !_encounterPaused)
        {
            return;
        }

        if (_encounterActive)
        {
            _finalizedDurationSeconds = Math.Max(
                _applicationClock.Elapsed.TotalSeconds -
                _encounterStartSeconds,
                0.01
            );
        }

        _encounterActive = false;
        _encounterPaused = false;
        _encounterComplete = true;
        _targetConfirmedDead = targetConfirmedDead;

        RecalculateFinalDps();
        SaveEncounterSnapshot(endReason, targetConfirmedDead);
        UpdateInterface();
    }

    private List<EncounterPalSkillSnapshot> BuildPalSkillSnapshots()
    {
        List<EncounterPalSkillSnapshot> snapshots = new();

        foreach (PalSkillRuntimeEntry skill in _palSkillStats.Values
                     .OrderBy(entry => entry.FirstSeenOrder))
        {
            List<DamageSourceEntry> matchingSources = _damageSources.Values
                .Where(source =>
                    source.Damage > 0 &&
                    source.ActorId.Equals(
                        skill.ActorId,
                        StringComparison.Ordinal) &&
                    IsPalSourceType(source.SourceType) &&
                    NormalizePalSkillLabel(source.SourceLabel).Equals(
                        skill.SkillName,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            long damage = matchingSources.Sum(source => source.Damage);
            int hitCount = matchingSources.Sum(source => source.HitCount);

            if (damage <= 0 || hitCount <= 0)
            {
                continue;
            }

            string palName = GetDamageSourceActorName(matchingSources);

            snapshots.Add(new EncounterPalSkillSnapshot
            {
                ActorId = skill.ActorId,
                SourceType = skill.SourceType,
                PalName = palName,
                SkillId = skill.SkillId,
                SkillName = skill.SkillName,
                FirstSeenOrder = skill.FirstSeenOrder,
                Damage = damage,
                HitCount = hitCount,
                CastCount = skill.CastCount
            });
        }

        return snapshots;
    }

    private void SaveEncounterSnapshot(
        string endReason,
        bool targetConfirmedDead)
    {
        if (_encounterSnapshotSaved ||
            _activeTargetName is null ||
            _totalDamage <= 0 ||
            _finalizedDurationSeconds < 2.0 ||
            _combatants.Count == 0)
        {
            return;
        }

        double duration = Math.Max(
            _finalizedDurationSeconds,
            0.01
        );
        DateTimeOffset endedAtUtc = DateTimeOffset.UtcNow;
        DateTimeOffset startedAtUtc = _encounterStartedAtUtc == default
            ? endedAtUtc.AddSeconds(-duration)
            : _encounterStartedAtUtc;

        EncounterSnapshot snapshot = new()
        {
            TargetActorId = _activeTargetName,
            SessionId = _sessionId,
            TargetName = GetKnownActorDisplayName(_activeTargetName),
            StartedAtUtc = startedAtUtc,
            EndedAtUtc = endedAtUtc,
            DurationSeconds = duration,
            EndReason = endReason,
            TargetConfirmedDead = targetConfirmedDead,
            TotalDamage = _totalDamage,
            PlayerDamage = _playerDamage,
            PalDamage = _palDamage,
            TeamDps = _displayedCombinedDps,
            Combatants = _combatants.Values
                .Where(combatant => combatant.Damage > 0)
                .OrderBy(combatant => combatant.FirstSeenOrder)
                .Select(combatant =>
                    new EncounterCombatantSnapshot
                    {
                        ActorId = combatant.ActorId,
                        SourceType = combatant.SourceType,
                        DisplayName = combatant.DisplayName,
                        OwnerActorId = combatant.OwnerActorId,
                        OwnerDisplayName = combatant.OwnerDisplayName,
                        FirstSeenOrder = combatant.FirstSeenOrder,
                        Damage = combatant.Damage,
                        Dps = combatant.Damage / duration
                    })
                .ToList(),
            DamageSources = _damageSources.Values
                .Where(source => source.Damage > 0)
                .OrderBy(source => source.FirstSeenOrder)
                .Select(source =>
                    new EncounterDamageSourceSnapshot
                    {
                        ActorId = source.ActorId,
                        SourceType = source.SourceType,
                        SourceName = GetDamageSourceActorName(
                            new[] { source }
                        ),
                        SourceLabel = source.SourceLabel,
                        FirstSeenOrder = source.FirstSeenOrder,
                        Damage = source.Damage,
                        HitCount = source.HitCount,
                        WeakHitCount = source.WeakHitCount,
                        StrongHitCount = source.StrongHitCount
                    })
                .ToList(),
            PalSkills = BuildPalSkillSnapshots()
        };

        _encounterHistory.Insert(0, snapshot);
        EncounterHistoryStore.NormalizeInPlace(_encounterHistory);

        _encounterSnapshotSaved = true;
        PersistEncounterHistory();
        _historyWindow?.RefreshEncounters(selectNewest: true);
    }

    private void RecalculateFinalDps()
    {
        double effectiveDuration = Math.Max(
            _finalizedDurationSeconds,
            0.01
        );

        _displayedPlayerDps =
            _playerDamage / effectiveDuration;
        _displayedPalDps =
            _palDamage / effectiveDuration;
        _displayedCombinedDps =
            (_playerDamage + _palDamage) / effectiveDuration;

        UpdateCombatantDps(effectiveDuration);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_encounterActive && !_encounterPaused)
        {
            return;
        }

        StopEncounter(
            targetConfirmedDead: false,
            endReason: EncounterEndReasons.ManualStop
        );

        // A button action should show the frozen result immediately rather
        // than waiting for the next one-second UI refresh.
        _interfaceDirty = false;
        RenderInterface();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetEncounter(clearTarget: true);
        _targetPlaceholder = _connected
            ? "No target"
            : "Waiting for Palworld";

        UpdateInterface();
    }

    private void ResetEncounter(bool clearTarget)
    {
        _encounterActive = false;
        _encounterPaused = false;
        _encounterComplete = false;
        _targetConfirmedDead = false;

        _playerDamage = 0;
        _palDamage = 0;
        _totalDamage = 0;

        _combatants.Clear();
        _damageSources.Clear();
        _palSkillStats.Clear();
        _pendingDamageMetadata.Clear();
        _nextCombatantOrder = 0;
        _nextDamageSourceOrder = 0;
        _nextPalSkillOrder = 0;

        _displayedCombinedDps = 0;
        _displayedPlayerDps = 0;
        _displayedPalDps = 0;
        _encounterStartSeconds = 0;
        _lastTargetActivitySeconds = 0;
        _finalizedDurationSeconds = 0;
        _encounterStartedAtUtc = default;
        _encounterSnapshotSaved = false;

        if (clearTarget)
        {
            _activeTargetName = null;
        }
    }

    private void HandleProcessExit()
    {
        DisconnectTelemetry();
        ResetEncounter(clearTarget: true);

        _telemetryConnectionState =
            TelemetryConnectionState.WaitingForPalworld;
        _targetPlaceholder = "Waiting for Palworld";
        _lastTelemetryError = string.Empty;
        _nextAttachAttemptUtc = DateTime.MinValue;
        UpdateInterface();
    }

    private void HandleTelemetryFailure()
    {
        DisconnectTelemetry();
        _telemetryConnectionState =
            TelemetryConnectionState.TelemetryError;
        _targetPlaceholder = "Telemetry lost";
        _lastTelemetryError = "The telemetry file became unavailable.";
        _nextAttachAttemptUtc = DateTime.UtcNow.AddSeconds(2);
        UpdateInterface();
    }

    private void DisconnectTelemetry()
    {
        _pollTimer.Stop();

        _palworldProcess?.Dispose();
        _palworldProcess = null;

        _connected = false;
        _telemetryFilePath = string.Empty;
        _telemetryPosition = 0;
        _pendingTelemetryText = string.Empty;
        _palOwners.Clear();
        _damageSources.Clear();
        _palSkillStats.Clear();
        _recentPalSkillActivations.Clear();
        _pendingDamageMetadata.Clear();
        _knownCombatantNames.Clear();
        _knownActorNamePriorities.Clear();
        _localPlayerActorId = null;
        _localPlayerDisplayName = null;
    }

    private void ResetTelemetryHandshake()
    {
        _luaVersion = "Not reported";
        _luaReleaseCandidate = null;
        _telemetryFormatVersion = null;
        _telemetryProfile = "Unknown";
        _diagnosticsMode = "Unknown";
        _versionHandshakeSeen = false;
    }

    private void InspectExistingTelemetryHandshake(string telemetryPath)
    {
        try
        {
            using FileStream stream = new(
                telemetryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using StreamReader reader = new(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024,
                leaveOpen: false
            );

            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.StartsWith("V|", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] fields = line.Split('|');

                if (fields.Length >= 4 &&
                    TryParseTimestamp(fields[1], out _))
                {
                    ApplyTelemetryVersionRecord(fields);
                }
            }
        }
        catch (IOException)
        {
            // Attaching can continue. The health panel will report that the
            // version was not available instead of blocking combat tracking.
        }
        catch (UnauthorizedAccessException)
        {
            // Same behavior as an IO race: keep tracking and report unknown.
        }
    }

    private void ApplyTelemetryVersionRecord(string[] fields)
    {
        if (fields.Length < 4)
        {
            return;
        }

        string reportedVersion = fields[2].Trim();
        string reportedProfile = fields[3].Trim();

        if (!string.IsNullOrWhiteSpace(reportedVersion))
        {
            _luaVersion = reportedVersion;
            _luaReleaseCandidate = ParseReleaseCandidate(reportedVersion);
        }

        if (reportedProfile.StartsWith(
                "RAID_TEAM",
                StringComparison.OrdinalIgnoreCase))
        {
            _telemetryProfile = "RAID_TEAM";
        }
        else if (reportedProfile.Equals(
                     "SIZE_LIMIT_RESET",
                     StringComparison.OrdinalIgnoreCase) &&
                 _luaReleaseCandidate.GetValueOrDefault() >= 7)
        {
            // RC7 maintenance emits a reset reason instead of repeating the
            // profile. It is still the RAID_TEAM telemetry schema.
            _telemetryProfile = "RAID_TEAM";
        }
        else if (!string.IsNullOrWhiteSpace(reportedProfile))
        {
            _telemetryProfile = reportedProfile;
        }

        if (fields.Length >= 5 &&
            int.TryParse(
                fields[4],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int explicitFormatVersion))
        {
            _telemetryFormatVersion = explicitFormatVersion;
        }
        else
        {
            _telemetryFormatVersion = InferTelemetryFormatVersion(
                _telemetryProfile,
                _luaReleaseCandidate
            );
        }

        if (fields.Length >= 6)
        {
            _diagnosticsMode = NormalizeDiagnosticsMode(fields[5]);
        }
        else if (_luaReleaseCandidate.GetValueOrDefault() >= 7 &&
                 _telemetryProfile.StartsWith(
                     "RAID_TEAM",
                     StringComparison.OrdinalIgnoreCase))
        {
            _diagnosticsMode = _luaReleaseCandidate.GetValueOrDefault() >= 12
                ? "ON (RC12 combat profile)"
                : "ON (RC7 profile)";
        }
        else
        {
            _diagnosticsMode = "Not reported";
        }

        _versionHandshakeSeen = true;
        UpdateInterface();
    }

    private static int? ParseReleaseCandidate(string version)
    {
        int markerIndex = version.LastIndexOf(
            "RC",
            StringComparison.OrdinalIgnoreCase
        );

        if (markerIndex < 0 || markerIndex + 2 >= version.Length)
        {
            return null;
        }

        int start = markerIndex + 2;
        int length = 0;

        while (start + length < version.Length &&
               char.IsDigit(version[start + length]))
        {
            length++;
        }

        if (length == 0)
        {
            return null;
        }

        return int.TryParse(
            version.Substring(start, length),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int releaseCandidate)
                ? releaseCandidate
                : null;
    }

    private static int? InferTelemetryFormatVersion(
        string profile,
        int? luaReleaseCandidate)
    {
        if (profile.Equals(
                "RAID_TEAM",
                StringComparison.OrdinalIgnoreCase) &&
            luaReleaseCandidate.GetValueOrDefault() >= 4)
        {
            return 1;
        }

        return null;
    }

    private static string NormalizeDiagnosticsMode(string value)
    {
        string normalized = value.Trim();

        if (normalized.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ON", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(
                "DIAGNOSTICS_ON",
                StringComparison.OrdinalIgnoreCase))
        {
            return "ON";
        }

        if (normalized.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("OFF", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(
                "DIAGNOSTICS_OFF",
                StringComparison.OrdinalIgnoreCase))
        {
            return "OFF";
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? "Not reported"
            : normalized;
    }

    private void RefreshTelemetryFileMetadata()
    {
        string path = !string.IsNullOrWhiteSpace(_telemetryFilePath)
            ? _telemetryFilePath
            : _expectedTelemetryFilePath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            _lastTelemetryWriteUtc = File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            // A concurrent Lua write can briefly prevent metadata access.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the last successful timestamp.
        }
    }

    private bool IsLuaVersionCompatible()
    {
        return _versionHandshakeSeen &&
               _luaReleaseCandidate.GetValueOrDefault() >=
                   MinimumCompatibleLuaReleaseCandidate;
    }

    private bool IsTelemetryFormatSupported()
    {
        return _versionHandshakeSeen &&
               _telemetryFormatVersion ==
                   SupportedTelemetryFormatVersion;
    }

    private bool IsTelemetryStalled()
    {
        if (!_connected || !_encounterActive ||
            _lastTelemetryWriteUtc == DateTime.MinValue)
        {
            return false;
        }

        return DateTime.UtcNow - _lastTelemetryWriteUtc >=
               TimeSpan.FromSeconds(TelemetryStallWarningSeconds);
    }

    private string GetTelemetryHealthHeadline()
    {
        if (!_connected)
        {
            return _telemetryConnectionState switch
            {
                TelemetryConnectionState.WaitingForTelemetry =>
                    "Waiting for telemetry file",
                TelemetryConnectionState.AttachmentError =>
                    "Palworld attachment error",
                TelemetryConnectionState.TelemetryError =>
                    "Telemetry connection lost",
                _ => "Waiting for Palworld"
            };
        }

        if (!_versionHandshakeSeen)
        {
            return "Lua version was not reported";
        }

        if (!IsTelemetryFormatSupported())
        {
            return "Unsupported telemetry format";
        }

        if (!IsLuaVersionCompatible())
        {
            return $"{_luaVersion} requires an update";
        }

        if (IsTelemetryStalled())
        {
            return "Telemetry stalled during combat";
        }

        return _encounterPaused
            ? "Connected · encounter paused"
            : _encounterActive
                ? "Connected · combat telemetry live"
                : "Connected · ready";
    }

    private string GetTelemetryHeaderStatus(out string brushKey)
    {
        if (!_connected)
        {
            brushKey = "InactiveBrush";

            return _telemetryConnectionState switch
            {
                TelemetryConnectionState.WaitingForTelemetry => "WAITING",
                TelemetryConnectionState.AttachmentError => "ATTACH ERR",
                TelemetryConnectionState.TelemetryError => "LOST",
                _ => "OFFLINE"
            };
        }

        if (!_versionHandshakeSeen)
        {
            brushKey = "PausedBrush";
            return "NO VERSION";
        }

        if (!IsTelemetryFormatSupported())
        {
            brushKey = "PausedBrush";
            return "FORMAT ERR";
        }

        if (!IsLuaVersionCompatible())
        {
            brushKey = "PausedBrush";
            return _luaReleaseCandidate.HasValue
                ? $"LUA RC{_luaReleaseCandidate.Value}"
                : "LUA UPDATE";
        }

        if (IsTelemetryStalled())
        {
            brushKey = "PausedBrush";
            return "STALLED";
        }

        if (_encounterPaused)
        {
            brushKey = "PausedBrush";
            return "PAUSED";
        }

        if (_encounterActive)
        {
            brushKey = "LiveBrush";
            return "LIVE";
        }

        brushKey = "ReadyBrush";
        return "READY";
    }

    private void RenderTelemetryHealth()
    {
        LiveStatusText.Text = GetTelemetryHeaderStatus(
            out string liveBrushKey
        );
        LiveIndicator.Fill = ThemeResourceHelper.GetBrush(
            liveBrushKey,
            "#FF59616E"
        );

        string headline = GetTelemetryHealthHeadline();
        HealthStatusButton.ToolTip =
            headline + "\nClick for telemetry details.";
        TelemetryHealthHeadlineText.Text = headline;
        HealthTrackerVersionText.Text = TrackerVersion;
        HealthLuaVersionText.Text = _versionHandshakeSeen
            ? _luaVersion + (IsLuaVersionCompatible()
                ? " · compatible"
                : " · update required")
            : "Not reported";
        HealthFormatText.Text = _telemetryFormatVersion.HasValue
            ? $"{_telemetryProfile} · v{_telemetryFormatVersion.Value}" +
              (IsTelemetryFormatSupported()
                  ? " · supported"
                  : " · unsupported")
            : $"{_telemetryProfile} · unknown";
        HealthDiagnosticsText.Text = _diagnosticsMode;
        HealthLastUpdateText.Text = FormatLastTelemetryUpdate();

        string displayedPath = !string.IsNullOrWhiteSpace(
            _telemetryFilePath)
                ? _telemetryFilePath
                : _expectedTelemetryFilePath;
        HealthTelemetryPathText.Text = string.IsNullOrWhiteSpace(
            displayedPath)
                ? "Not resolved"
                : displayedPath;
        OpenTelemetryFolderButton.IsEnabled =
            !string.IsNullOrWhiteSpace(displayedPath);
        CreatePublicDiagnosticZipButton.IsEnabled = true;
        CreatePrivateDiagnosticZipButton.IsEnabled = true;
        OpenDiagnosticsFolderButton.IsEnabled =
            Directory.Exists(DiagnosticsDirectory);
        CopyTelemetryDetailsButton.IsEnabled = true;
        DiagnosticBundlePathText.Text =
            string.IsNullOrWhiteSpace(_lastDiagnosticZipPath)
                ? "Not created this session"
                : _lastDiagnosticZipPath;
    }

    private string FormatLastTelemetryUpdate()
    {
        if (_lastTelemetryWriteUtc == DateTime.MinValue)
        {
            return "Never";
        }

        TimeSpan elapsed = DateTime.UtcNow - _lastTelemetryWriteUtc;

        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalSeconds < 2)
        {
            return "Just now";
        }

        if (elapsed.TotalSeconds < 60)
        {
            return $"{Math.Floor(elapsed.TotalSeconds):N0} sec ago";
        }

        if (elapsed.TotalMinutes < 60)
        {
            return $"{Math.Floor(elapsed.TotalMinutes):N0} min ago";
        }

        return $"{Math.Floor(elapsed.TotalHours):N0} hr ago";
    }

    private string BuildTelemetryHealthDetails()
    {
        string path = !string.IsNullOrWhiteSpace(_telemetryFilePath)
            ? _telemetryFilePath
            : _expectedTelemetryFilePath;

        StringBuilder details = new();
        details.AppendLine("RogueMode Combat Tracker · Combat Feed Status");
        details.AppendLine($"Status: {GetTelemetryHealthHeadline()}");
        details.AppendLine($"Tracker: {TrackerVersion}");
        details.AppendLine($"Lua: {_luaVersion}");
        details.AppendLine(
            $"Lua compatible: {(IsLuaVersionCompatible() ? "Yes" : "No")}");
        details.AppendLine(
            $"Format: {_telemetryProfile} · " +
            (_telemetryFormatVersion?.ToString(
                CultureInfo.InvariantCulture) ?? "Unknown"));
        details.AppendLine(
            $"Format supported: {(IsTelemetryFormatSupported() ? "Yes" : "No")}");
        details.AppendLine($"Diagnostics: {_diagnosticsMode}");
        details.AppendLine($"Last update: {FormatLastTelemetryUpdate()}");
        details.AppendLine(
            $"Telemetry path: {(string.IsNullOrWhiteSpace(path) ? "Not resolved" : path)}");

        if (!string.IsNullOrWhiteSpace(_lastTelemetryError))
        {
            details.AppendLine($"Last error: {_lastTelemetryError}");
        }

        return details.ToString().TrimEnd();
    }

    private void HealthStatusButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        TelemetryHealthPanel.Visibility =
            TelemetryHealthPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        HealthActionFeedbackText.Text = string.Empty;
        RenderTelemetryHealth();
    }

    private void OpenTelemetryFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string path = !string.IsNullOrWhiteSpace(_telemetryFilePath)
            ? _telemetryFilePath
            : _expectedTelemetryFilePath;
        string? directory = string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory))
        {
            HealthActionFeedbackText.Text = "FOLDER NOT FOUND";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
            HealthActionFeedbackText.Text = "OPENED";
        }
        catch
        {
            HealthActionFeedbackText.Text = "OPEN FAILED";
        }
    }

    private void CopyTelemetryDetailsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            DiagnosticPrivacyReport privacy = CreateDiagnosticPrivacyReport();
            Clipboard.SetText(BuildDiagnosticSummary(
                DiagnosticBundleMode.PublicSupport,
                privacy));
            HealthActionFeedbackText.Text = "COPIED";
        }
        catch
        {
            HealthActionFeedbackText.Text = "COPY FAILED";
        }
    }

    private void CreatePublicDiagnosticZipButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CreateDiagnosticZip(DiagnosticBundleMode.PublicSupport);
    }

    private void CreatePrivateDiagnosticZipButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        MessageBoxResult result = MessageBox.Show(
            this,
            "The private developer bundle retains raw local paths, player and Pal names, actor identifiers, and memory addresses. Only share it with a trusted developer.\n\nCreate the private bundle?",
            "Create Private Developer Bundle",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (result == MessageBoxResult.Yes)
        {
            CreateDiagnosticZip(DiagnosticBundleMode.PrivateDeveloper);
        }
    }

    private void CreateDiagnosticZip(DiagnosticBundleMode mode)
    {
        CreatePublicDiagnosticZipButton.IsEnabled = false;
        CreatePrivateDiagnosticZipButton.IsEnabled = false;
        HealthActionFeedbackText.Text = mode == DiagnosticBundleMode.PublicSupport
            ? "SANITIZING..."
            : "CREATING...";

        string outputPath = CreateUniqueDiagnosticZipPath(mode);

        try
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
            List<string> manifest = new();
            DiagnosticPrivacyReport privacy = CreateDiagnosticPrivacyReport();
            manifest.Add($"Bundle mode: {GetModeLabel(mode)}");
            manifest.Add(
                mode == DiagnosticBundleMode.PublicSupport
                    ? "Privacy: aggressive public-support sanitization enabled"
                    : "Privacy: raw private-developer details retained");

            using (FileStream outputStream = new(
                       outputPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            using (ZipArchive archive = new(
                       outputStream,
                       ZipArchiveMode.Create,
                       leaveOpen: false))
            {
                AddTextEntry(
                    archive,
                    "diagnostic-summary.txt",
                    BuildDiagnosticSummary(mode, privacy)
                );
                manifest.Add("Included: diagnostic-summary.txt");

                string recentEncounters =
                    BuildRecentEncounterDiagnosticMetadata();
                if (mode == DiagnosticBundleMode.PublicSupport)
                {
                    recentEncounters = SanitizePublicDiagnosticText(
                        recentEncounters,
                        privacy,
                        sanitizeTelemetryFields: false);
                }

                AddTextEntry(
                    archive,
                    "tracker/recent-encounters.txt",
                    recentEncounters
                );
                manifest.Add(
                    "Included: tracker/recent-encounters.txt (sanitized)");

                string telemetryPath = GetDisplayedTelemetryPath();
                TryAddFileExcerpt(
                    archive,
                    telemetryPath,
                    "telemetry/RogueModeTelemetry_excerpt.txt",
                    DiagnosticTelemetryFirstBytes,
                    DiagnosticTelemetryTailBytes,
                    manifest,
                    mode,
                    privacy,
                    sanitizeTelemetryFields: true,
                    historicalCrashLog: false
                );

                string? ue4ssLogPath = FindNewestUe4ssLogPath();
                TryAddFileExcerpt(
                    archive,
                    ue4ssLogPath,
                    "ue4ss/UE4SS_log_excerpt.txt",
                    DiagnosticLogFirstBytes,
                    DiagnosticLogTailBytes,
                    manifest,
                    mode,
                    privacy,
                    sanitizeTelemetryFields: false,
                    historicalCrashLog: false
                );

                TryAddFileExcerpt(
                    archive,
                    CrashLogPath,
                    "tracker/crash_log_excerpt.txt",
                    0,
                    DiagnosticCrashTailBytes,
                    manifest,
                    mode,
                    privacy,
                    sanitizeTelemetryFields: false,
                    historicalCrashLog: true
                );

                TryAddFileExcerpt(
                    archive,
                    ThemePreferencePath,
                    "tracker/theme.txt",
                    16 * 1024,
                    0,
                    manifest,
                    mode,
                    privacy,
                    sanitizeTelemetryFields: false,
                    historicalCrashLog: false
                );

                AddInstallationDiagnosticFiles(
                    archive,
                    manifest,
                    mode,
                    privacy);

                manifest.Add("Included: privacy-report.txt");
                manifest.Add(
                    $"Privacy redactions applied: {(mode == DiagnosticBundleMode.PublicSupport ? "Yes" : "No")}");

                string manifestText =
                    string.Join(Environment.NewLine, manifest) +
                    Environment.NewLine;
                if (mode == DiagnosticBundleMode.PublicSupport)
                {
                    manifestText = SanitizePublicDiagnosticText(
                        manifestText,
                        privacy,
                        sanitizeTelemetryFields: false);
                }

                manifestText +=
                    $"User/profile paths redacted: {privacy.UserPathRedactions:N0}" +
                    Environment.NewLine +
                    $"Other local paths redacted: {privacy.LocalPathRedactions:N0}" +
                    Environment.NewLine +
                    $"Player names redacted: {privacy.PlayerNameRedactions:N0}" +
                    Environment.NewLine +
                    $"Owner names redacted: {privacy.OwnerNameRedactions:N0}" +
                    Environment.NewLine +
                    $"Pal/display names redacted: {privacy.DisplayNameRedactions:N0}" +
                    Environment.NewLine +
                    $"Actor instances redacted: {privacy.ActorInstanceRedactions:N0}" +
                    Environment.NewLine +
                    $"Memory addresses redacted: {privacy.MemoryAddressRedactions:N0}" +
                    Environment.NewLine +
                    $"GUID/session identifiers redacted: {privacy.IdentifierRedactions:N0}" +
                    Environment.NewLine +
                    $"Total privacy replacements: {privacy.TotalRedactions:N0}" +
                    Environment.NewLine;

                AddTextEntry(
                    archive,
                    "manifest.txt",
                    manifestText
                );

                AddTextEntry(
                    archive,
                    "privacy-report.txt",
                    privacy.BuildReport(mode)
                );
            }

            _lastDiagnosticZipPath = outputPath;
            HealthActionFeedbackText.Text =
                mode == DiagnosticBundleMode.PublicSupport
                    ? "PUBLIC ZIP CREATED"
                    : "PRIVATE ZIP CREATED";
            CreatePublicDiagnosticZipButton.ToolTip = outputPath;
            CreatePrivateDiagnosticZipButton.ToolTip = outputPath;
            OpenDiagnosticsFolderButton.IsEnabled = true;
            RenderTelemetryHealth();
        }
        catch (Exception exception)
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
            catch
            {
                // A failed cleanup must not hide the original bundle error.
            }

            _lastTelemetryError =
                "Diagnostic ZIP failed: " + exception.Message;
            HealthActionFeedbackText.Text = "ZIP FAILED";

            MessageBox.Show(
                this,
                "The diagnostic ZIP could not be created.\n\n" +
                exception.Message,
                "Diagnostic ZIP Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
        finally
        {
            CreatePublicDiagnosticZipButton.IsEnabled = true;
            CreatePrivateDiagnosticZipButton.IsEnabled = true;
        }
    }

    private void OpenDiagnosticsFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = DiagnosticsDirectory,
                UseShellExecute = true
            });
            HealthActionFeedbackText.Text = "OPENED";
        }
        catch
        {
            HealthActionFeedbackText.Text = "OPEN FAILED";
        }
    }

    private string CreateUniqueDiagnosticZipPath(
        DiagnosticBundleMode mode)
    {
        string timestamp = DateTime.Now.ToString(
            "yyyyMMdd_HHmmss",
            CultureInfo.InvariantCulture
        );
        string modeName = mode == DiagnosticBundleMode.PublicSupport
            ? "Public_Diagnostic"
            : "Private_Developer_Diagnostic";
        string baseName = $"RMCT_{modeName}_{timestamp}";
        string candidate = Path.Combine(
            DiagnosticsDirectory,
            baseName + ".zip"
        );
        int suffix = 2;

        while (File.Exists(candidate))
        {
            candidate = Path.Combine(
                DiagnosticsDirectory,
                $"{baseName}_{suffix}.zip"
            );
            suffix++;
        }

        return candidate;
    }

    private string BuildDiagnosticSummary(
        DiagnosticBundleMode mode,
        DiagnosticPrivacyReport privacy)
    {
        StringBuilder summary = new();
        summary.AppendLine("RogueMode Combat Tracker · Diagnostic Summary");
        summary.AppendLine($"Bundle mode: {GetModeLabel(mode)}");
        summary.AppendLine(
            $"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        summary.AppendLine();
        summary.AppendLine(BuildTelemetryHealthDetails());
        summary.AppendLine();
        summary.AppendLine("Runtime");
        summary.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        summary.AppendLine(
            $"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        summary.AppendLine($".NET runtime: {Environment.Version}");
        summary.AppendLine($"Application folder: {AppContext.BaseDirectory}");
        summary.AppendLine(
            $"Palworld process: {GetPalworldDiagnosticState()}");
        summary.AppendLine(
            $"Encounter state: {GetEncounterDiagnosticState()}");
        summary.AppendLine(
            $"Saved encounters: {_encounterHistory.Count:N0}");
        summary.AppendLine();
        summary.AppendLine("Installation");
        summary.Append(BuildInstallationDiagnosticMetadata());

        string result = summary.ToString().TrimEnd();
        return mode == DiagnosticBundleMode.PublicSupport
            ? SanitizePublicDiagnosticText(
                result,
                privacy,
                sanitizeTelemetryFields: false)
            : result;
    }

    private string GetPalworldDiagnosticState()
    {
        if (_palworldProcess is null)
        {
            return "Not attached";
        }

        try
        {
            return _palworldProcess.HasExited
                ? "Exited"
                : $"Running · PID {_palworldProcess.Id}";
        }
        catch
        {
            return "State unavailable";
        }
    }

    private string GetEncounterDiagnosticState()
    {
        if (_encounterPaused)
        {
            return "Paused";
        }

        if (_encounterActive)
        {
            return IsTelemetryStalled() ? "Active · stalled" : "Active";
        }

        if (_encounterComplete)
        {
            return "Complete";
        }

        return "Idle";
    }

    private string BuildRecentEncounterDiagnosticMetadata()
    {
        StringBuilder text = new();
        text.AppendLine("RogueMode Combat Tracker · Recent Encounter Metadata");
        text.AppendLine(
            "Custom names, notes, actor IDs, owner names, and session IDs are excluded.");
        text.AppendLine();

        List<EncounterSnapshot> recent = _encounterHistory
            .OrderByDescending(encounter => encounter.EndedAtUtc)
            .Take(DiagnosticRecentEncounterCount)
            .ToList();

        if (recent.Count == 0)
        {
            text.AppendLine("No saved encounters.");
            return text.ToString().TrimEnd();
        }

        for (int index = 0; index < recent.Count; index++)
        {
            EncounterSnapshot encounter = recent[index];
            text.AppendLine($"Encounter {index + 1}");
            text.AppendLine($"Target: {encounter.TargetName}");
            text.AppendLine(
                $"Ended: {encounter.EndedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
            text.AppendLine(
                $"Duration: {encounter.DurationSeconds:F2} sec");
            text.AppendLine($"End reason: {encounter.EndReason}");
            text.AppendLine(
                $"Confirmed defeat: {(encounter.TargetConfirmedDead ? "Yes" : "No")}");
            text.AppendLine($"Total damage: {encounter.TotalDamage:N0}");
            text.AppendLine($"Team DPS: {encounter.TeamDps:N2}");
            text.AppendLine($"Player DPS: {encounter.PlayerDps:N2}");
            text.AppendLine($"Pal DPS: {encounter.PalDps:N2}");
            text.AppendLine(
                $"Contributors: {encounter.Combatants?.Count ?? 0}");
            text.AppendLine(
                $"Damage sources: {encounter.DamageSources?.Count ?? 0}");
            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    private string BuildInstallationDiagnosticMetadata()
    {
        string telemetryDirectory = GetTelemetryDirectory() ?? "Not resolved";
        string? mainLuaPath = GetRogueModeMainLuaPath();
        string? enabledPath = GetRogueModeEnabledPath();
        string? modsPath = GetUe4ssModsListPath();
        string? settingsPath = GetUe4ssSettingsPath();
        string? logPath = FindNewestUe4ssLogPath();

        StringBuilder text = new();
        text.AppendLine($"UE4SS folder: {telemetryDirectory}");
        text.AppendLine(BuildFileMetadataLine("Telemetry", GetDisplayedTelemetryPath()));
        text.AppendLine(BuildFileMetadataLine("UE4SS log", logPath));
        text.AppendLine(BuildFileMetadataLine("main.lua", mainLuaPath));
        text.AppendLine(BuildFileMetadataLine("enabled.txt", enabledPath));
        text.AppendLine(BuildFileMetadataLine("mods.txt", modsPath));
        text.AppendLine(BuildFileMetadataLine("UE4SS settings", settingsPath));

        if (!string.IsNullOrWhiteSpace(mainLuaPath) && File.Exists(mainLuaPath))
        {
            text.AppendLine($"main.lua SHA-256: {TryComputeSha256(mainLuaPath)}");
            text.AppendLine(
                $"main.lua banner: {TryReadLuaBanner(mainLuaPath)}");
        }

        return text.ToString();
    }

    private static string BuildFileMetadataLine(
        string label,
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{label}: Not resolved";
        }

        try
        {
            if (!File.Exists(path))
            {
                return $"{label}: Missing · {path}";
            }

            FileInfo info = new(path);
            return $"{label}: Present · {info.Length:N0} bytes · " +
                   $"{info.LastWriteTime:yyyy-MM-dd HH:mm:ss} · {path}";
        }
        catch (Exception exception)
        {
            return $"{label}: Metadata unavailable · {path} · " +
                   exception.Message;
        }
    }

    private static string GetModeLabel(DiagnosticBundleMode mode)
    {
        return mode == DiagnosticBundleMode.PublicSupport
            ? "Public Support"
            : "Private Developer";
    }

    private DiagnosticPrivacyReport CreateDiagnosticPrivacyReport()
    {
        DiagnosticPrivacyReport report = new();
        report.RememberName(_playerDisplayName, "<PLAYER_NAME>");
        report.RememberName(_palDisplayName, "<PAL_NAME>");

        foreach (CombatantEntry combatant in _combatants.Values)
        {
            string token = combatant.SourceType.Contains(
                "PLAYER",
                StringComparison.OrdinalIgnoreCase)
                    ? "<PLAYER_NAME>"
                    : "<PAL_NAME>";
            report.RememberName(combatant.DisplayName, token);
            report.RememberName(
                combatant.OwnerDisplayName,
                "<OWNER_NAME>");
        }

        foreach (PalOwnerInfo owner in _palOwners.Values)
        {
            report.RememberName(
                owner.OwnerDisplayName,
                "<OWNER_NAME>");
        }

        return report;
    }

    private string SanitizePublicDiagnosticText(
        string content,
        DiagnosticPrivacyReport privacy,
        bool sanitizeTelemetryFields)
    {
        string sanitized = sanitizeTelemetryFields
            ? SanitizeTelemetryNameFields(content, privacy)
            : content;

        foreach (KeyValuePair<string, string> pair in
                 privacy.SensitiveNames
                     .OrderByDescending(item => item.Key.Length))
        {
            int replaced;
            sanitized = ReplaceLiteralWithCount(
                sanitized,
                pair.Key,
                pair.Value,
                out replaced);
            AddNameRedactionCount(privacy, pair.Value, replaced);
        }

        List<(string Path, string Token, bool UserPath)> knownPaths = new()
        {
            (GetTelemetryDirectory() ?? string.Empty, "<UE4SS>", false),
            (AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar), "<APP_FOLDER>", false),
            (DiagnosticsDirectory, "<DIAGNOSTICS>", true),
            (Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "<LOCAL_APP_DATA>", true),
            (Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
                "<USER_PROFILE>", true)
        };

        foreach ((string path, string token, bool userPath) in knownPaths
                     .Where(item => !string.IsNullOrWhiteSpace(item.Path))
                     .OrderByDescending(item => item.Path.Length))
        {
            int replaced;
            sanitized = ReplaceLiteralWithCount(
                sanitized,
                path,
                token,
                out replaced);

            if (userPath)
            {
                privacy.UserPathRedactions += replaced;
            }
            else
            {
                privacy.LocalPathRedactions += replaced;
            }
        }

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)\b[A-Z]:\\Users\\[^\\\r\n]+",
            "<USER_PROFILE>",
            count => privacy.UserPathRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            "(?i)\\b[A-Z]:\\\\(?:[^\\\\/:*?\"<>|\\r\\n]+\\\\)*[^\\\\/:*?\"<>|\\r\\n]*",
            "<LOCAL_PATH>",
            count => privacy.LocalPathRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)\b[A-Z]:/(?:[^/\r\n]+/)*[^/\r\n]*",
            "<LOCAL_PATH>",
            count => privacy.LocalPathRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)_C_[0-9]+",
            "_C_<INSTANCE>",
            count => privacy.ActorInstanceRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)\b(table|TrivialObject|UObject|Object):\s*(?:0x)?[0-9A-F]{8,16}\b",
            "$1: <MEMORY_ADDRESS>",
            count => privacy.MemoryAddressRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)\b0x[0-9A-F]{8,16}\b",
            "<MEMORY_ADDRESS>",
            count => privacy.MemoryAddressRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)\b[0-9A-F]{12,16}\b",
            "<MEMORY_ADDRESS>",
            count => privacy.MemoryAddressRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)\b[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\b",
            "<GUID>",
            count => privacy.IdentifierRedactions += count);

        sanitized = ReplaceRegexWithCount(
            sanitized,
            @"(?i)\b[0-9A-F]{32}\b",
            "<SESSION_ID>",
            count => privacy.IdentifierRedactions += count);

        return sanitized;
    }

    private static string SanitizeTelemetryNameFields(
        string content,
        DiagnosticPrivacyReport privacy)
    {
        StringBuilder output = new();
        using StringReader reader = new(content);
        string? line;
        bool firstLine = true;

        while ((line = reader.ReadLine()) is not null)
        {
            if (!firstLine)
            {
                output.AppendLine();
            }
            firstLine = false;

            if (line.Length == 0 || line[0] == '#')
            {
                output.Append(line);
                continue;
            }

            string[] fields = line.Split('|');
            if (fields.Length < 2)
            {
                output.Append(line);
                continue;
            }

            string code = fields[0];
            switch (code)
            {
                case "L":
                    RedactTelemetryNameField(
                        fields, 3, "<PLAYER_NAME>", privacy);
                    break;
                case "O":
                    RedactTelemetryNameField(
                        fields, 4, "<OWNER_NAME>", privacy);
                    break;
                case "N":
                    RedactTelemetryNameField(
                        fields, 3, "<PAL_NAME>", privacy);
                    break;
                case "P":
                    RedactTelemetryNameField(
                        fields, 4, "<PAL_NAME>", privacy);
                    break;
                case "Q":
                    RedactTelemetryNameField(
                        fields,
                        6,
                        GetSourceNameToken(fields, 5),
                        privacy);
                    break;
                case "D":
                    RedactTelemetryNameField(
                        fields,
                        6,
                        GetSourceNameToken(fields, 5),
                        privacy);
                    break;
                case "B":
                    RedactTelemetryNameField(
                        fields,
                        4,
                        fields.Length > 3 &&
                        fields[3].Contains(
                            "BP_Player_",
                            StringComparison.OrdinalIgnoreCase)
                                ? "<PLAYER_NAME>"
                                : "<PAL_NAME>",
                        privacy);
                    break;
                case "C":
                    RedactTelemetryNameField(
                        fields,
                        5,
                        GetSourceNameToken(fields, 3),
                        privacy);
                    break;
                case "M":
                    RedactTelemetryNameField(
                        fields,
                        10,
                        GetSourceNameToken(fields, 9),
                        privacy);
                    break;
                case "I":
                    RedactTelemetryNameField(
                        fields,
                        5,
                        GetSourceNameToken(fields, 4),
                        privacy);
                    break;
                case "W":
                    RedactTelemetryNameField(
                        fields,
                        6,
                        GetSourceNameToken(fields, 5),
                        privacy);
                    break;
                case "K":
                    RedactTelemetryNameField(
                        fields, 5, "<PAL_NAME>", privacy);
                    RedactTelemetryNameField(
                        fields, 7, "<OWNER_NAME>", privacy);
                    break;
                case "Z":
                    if (fields.Length > 3 &&
                        fields[3].Equals(
                            "BEGIN",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        RedactTelemetryNameField(
                            fields, 5, "<PLAYER_NAME>", privacy);
                    }
                    else if (fields.Length > 3 &&
                             fields[3].Equals(
                                 "PARTNER",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        RedactTelemetryNameField(
                            fields, 5, "<PAL_NAME>", privacy);
                        RedactTelemetryNameField(
                            fields, 7, "<OWNER_NAME>", privacy);
                    }
                    else if (fields.Length > 4 &&
                             (fields[3].Equals(
                                  "VALUE",
                                  StringComparison.OrdinalIgnoreCase) ||
                              fields[3].Equals(
                                  "FUNCTION",
                                  StringComparison.OrdinalIgnoreCase)) &&
                             fields[4].StartsWith(
                                 "PARTNER:",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        fields[4] = "PARTNER:<PAL_NAME>";
                        privacy.DisplayNameRedactions++;
                    }
                    break;
            }

            output.Append(string.Join('|', fields));
        }

        return output.ToString();
    }

    private static string GetSourceNameToken(
        IReadOnlyList<string> fields,
        int sourceTypeIndex)
    {
        return fields.Count > sourceTypeIndex &&
               fields[sourceTypeIndex].Contains(
                   "PLAYER",
                   StringComparison.OrdinalIgnoreCase)
            ? "<PLAYER_NAME>"
            : "<PAL_NAME>";
    }

    private static void RedactTelemetryNameField(
        IList<string> fields,
        int fieldIndex,
        string replacement,
        DiagnosticPrivacyReport privacy)
    {
        if (fields.Count <= fieldIndex)
        {
            return;
        }

        string value = fields[fieldIndex].Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unresolved", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("nil", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invalid", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        privacy.RememberName(value, replacement);
        fields[fieldIndex] = replacement;
        AddNameRedactionCount(privacy, replacement, 1);
    }

    private static void AddNameRedactionCount(
        DiagnosticPrivacyReport privacy,
        string replacement,
        int count)
    {
        if (count <= 0)
        {
            return;
        }

        switch (replacement)
        {
            case "<PLAYER_NAME>":
                privacy.PlayerNameRedactions += count;
                break;
            case "<OWNER_NAME>":
                privacy.OwnerNameRedactions += count;
                break;
            default:
                privacy.DisplayNameRedactions += count;
                break;
        }
    }

    private static string ReplaceLiteralWithCount(
        string input,
        string value,
        string replacement,
        out int replacementCount)
    {
        replacementCount = 0;
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(value))
        {
            return input;
        }

        StringBuilder output = new(input.Length);
        int searchIndex = 0;

        while (true)
        {
            int matchIndex = input.IndexOf(
                value,
                searchIndex,
                StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                output.Append(input, searchIndex, input.Length - searchIndex);
                break;
            }

            output.Append(input, searchIndex, matchIndex - searchIndex);
            output.Append(replacement);
            replacementCount++;
            searchIndex = matchIndex + value.Length;
        }

        return output.ToString();
    }

    private static string ReplaceRegexWithCount(
        string input,
        string pattern,
        string replacement,
        Action<int> recordCount)
    {
        int count = 0;
        string result = Regex.Replace(
            input,
            pattern,
            match =>
            {
                count++;
                return match.Result(replacement);
            },
            RegexOptions.CultureInvariant);
        recordCount(count);
        return result;
    }

    private void AddInstallationDiagnosticFiles(
        ZipArchive archive,
        ICollection<string> manifest,
        DiagnosticBundleMode mode,
        DiagnosticPrivacyReport privacy)
    {
        string? mainLuaPath = GetRogueModeMainLuaPath();
        string mainLuaMetadata = BuildInstallationDiagnosticMetadata();
        if (mode == DiagnosticBundleMode.PublicSupport)
        {
            mainLuaMetadata = SanitizePublicDiagnosticText(
                mainLuaMetadata,
                privacy,
                sanitizeTelemetryFields: false);
        }

        AddTextEntry(
            archive,
            "installation/RogueModeTelemetry_metadata.txt",
            mainLuaMetadata
        );
        manifest.Add(
            "Included: installation/RogueModeTelemetry_metadata.txt");

        TryAddFileExcerpt(
            archive,
            GetRogueModeEnabledPath(),
            "installation/enabled.txt",
            64 * 1024,
            0,
            manifest,
            mode,
            privacy,
            sanitizeTelemetryFields: false,
            historicalCrashLog: false
        );
        TryAddFileExcerpt(
            archive,
            GetUe4ssModsListPath(),
            "installation/mods.txt",
            256 * 1024,
            0,
            manifest,
            mode,
            privacy,
            sanitizeTelemetryFields: false,
            historicalCrashLog: false
        );
        TryAddFileExcerpt(
            archive,
            GetUe4ssSettingsPath(),
            "installation/UE4SS-settings_excerpt.ini",
            256 * 1024,
            0,
            manifest,
            mode,
            privacy,
            sanitizeTelemetryFields: false,
            historicalCrashLog: false
        );

        if (string.IsNullOrWhiteSpace(mainLuaPath) || !File.Exists(mainLuaPath))
        {
            manifest.Add("Missing: RogueModeTelemetry main.lua");
        }
    }

    private static void AddTextEntry(
        ZipArchive archive,
        string entryName,
        string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            entryName,
            CompressionLevel.Optimal
        );

        using Stream entryStream = entry.Open();
        using StreamWriter writer = new(
            entryStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        writer.Write(content);
    }

    private void TryAddFileExcerpt(
        ZipArchive archive,
        string? sourcePath,
        string entryName,
        int firstBytes,
        int tailBytes,
        ICollection<string> manifest,
        DiagnosticBundleMode mode,
        DiagnosticPrivacyReport privacy,
        bool sanitizeTelemetryFields,
        bool historicalCrashLog)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            manifest.Add($"Missing: {entryName} · path not resolved");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            manifest.Add($"Missing: {entryName} · {sourcePath}");
            return;
        }

        try
        {
            string excerpt = ReadTextFileExcerpt(
                sourcePath,
                firstBytes,
                tailBytes
            );

            if (historicalCrashLog)
            {
                excerpt =
                    "# Historical crash log\n" +
                    "# Entries may describe failures from older tracker versions and are not proof of a current tracker failure.\n\n" +
                    excerpt;
            }

            if (mode == DiagnosticBundleMode.PublicSupport)
            {
                excerpt = SanitizePublicDiagnosticText(
                    excerpt,
                    privacy,
                    sanitizeTelemetryFields);
            }

            AddTextEntry(archive, entryName, excerpt);
            manifest.Add($"Included: {entryName} · source {sourcePath}");
        }
        catch (Exception exception)
        {
            manifest.Add(
                $"Failed: {entryName} · {sourcePath} · {exception.Message}");
        }
    }

    private static string ReadTextFileExcerpt(
        string sourcePath,
        int firstBytes,
        int tailBytes)
    {
        using FileStream stream = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );

        long originalLength = stream.Length;
        int safeFirstBytes = Math.Max(0, firstBytes);
        int safeTailBytes = Math.Max(0, tailBytes);
        long requestedBytes = (long)safeFirstBytes + safeTailBytes;
        UTF8Encoding encoding = new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: false
        );
        FileInfo fileInfo = new(sourcePath);
        TimeSpan age = DateTime.Now - fileInfo.LastWriteTime;
        StringBuilder output = new();
        output.AppendLine($"# Source: {sourcePath}");
        output.AppendLine($"# Original size: {originalLength:N0} bytes");
        output.AppendLine(
            $"# Last write: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss zzz}");
        output.AppendLine(
            $"# Age at bundle creation: {FormatDiagnosticFileAge(age)}");

        if (originalLength <= requestedBytes || safeTailBytes == 0)
        {
            int bytesToRead = safeTailBytes == 0
                ? (int)Math.Min(originalLength, safeFirstBytes)
                : (int)Math.Min(originalLength, requestedBytes);
            byte[] bytes = ReadSegment(stream, 0, bytesToRead);
            output.AppendLine($"# Captured: first {bytes.Length:N0} bytes");
            output.AppendLine();
            output.Append(encoding.GetString(bytes));
            return output.ToString();
        }

        byte[] first = ReadSegment(
            stream,
            0,
            (int)Math.Min(originalLength, safeFirstBytes)
        );
        long tailOffset = Math.Max(0, originalLength - safeTailBytes);
        byte[] tail = ReadSegment(
            stream,
            tailOffset,
            (int)Math.Min(originalLength - tailOffset, safeTailBytes)
        );

        output.AppendLine(
            $"# Captured: first {first.Length:N0} bytes and " +
            $"last {tail.Length:N0} bytes");
        output.AppendLine();
        output.Append(encoding.GetString(first));
        output.AppendLine();
        output.AppendLine();
        output.AppendLine(
            $"----- OMITTED {Math.Max(0, originalLength - first.Length - tail.Length):N0} BYTES -----");
        output.AppendLine();
        output.Append(encoding.GetString(tail));
        return output.ToString();
    }

    private static string FormatDiagnosticFileAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
        {
            return $"{Math.Floor(age.TotalDays):N0} day(s)";
        }

        if (age.TotalHours >= 1)
        {
            return $"{Math.Floor(age.TotalHours):N0} hour(s)";
        }

        if (age.TotalMinutes >= 1)
        {
            return $"{Math.Floor(age.TotalMinutes):N0} minute(s)";
        }

        return $"{Math.Max(0, Math.Floor(age.TotalSeconds)):N0} second(s)";
    }

    private static byte[] ReadSegment(
        FileStream stream,
        long offset,
        int count)
    {
        if (count <= 0)
        {
            return Array.Empty<byte>();
        }

        stream.Seek(offset, SeekOrigin.Begin);
        byte[] buffer = new byte[count];
        int totalRead = 0;

        while (totalRead < count)
        {
            int read = stream.Read(
                buffer,
                totalRead,
                count - totalRead
            );

            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        if (totalRead == count)
        {
            return buffer;
        }

        Array.Resize(ref buffer, totalRead);
        return buffer;
    }

    private string GetDisplayedTelemetryPath()
    {
        return !string.IsNullOrWhiteSpace(_telemetryFilePath)
            ? _telemetryFilePath
            : _expectedTelemetryFilePath;
    }

    private string? GetTelemetryDirectory()
    {
        string path = GetDisplayedTelemetryPath();
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetDirectoryName(path);
    }

    private string? FindNewestUe4ssLogPath()
    {
        string? directory = GetTelemetryDirectory();

        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(directory, "UE4SS*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private string? GetRogueModeMainLuaPath()
    {
        string? directory = GetTelemetryDirectory();
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(
                directory,
                "Mods",
                "RogueModeTelemetry",
                "Scripts",
                "main.lua"
            );
    }

    private string? GetRogueModeEnabledPath()
    {
        string? directory = GetTelemetryDirectory();
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(
                directory,
                "Mods",
                "RogueModeTelemetry",
                "enabled.txt"
            );
    }

    private string? GetUe4ssModsListPath()
    {
        string? directory = GetTelemetryDirectory();
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, "Mods", "mods.txt");
    }

    private string? GetUe4ssSettingsPath()
    {
        string? directory = GetTelemetryDirectory();
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, "UE4SS-settings.ini");
    }

    private static string TryComputeSha256(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(
                sha256.ComputeHash(stream)
            ).ToLowerInvariant();
        }
        catch (Exception exception)
        {
            return "Unavailable · " + exception.Message;
        }
    }

    private static string TryReadLuaBanner(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using StreamReader reader = new(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true
            );

            for (int index = 0; index < 40; index++)
            {
                string? line = reader.ReadLine();

                if (line is null)
                {
                    break;
                }

                if (line.Contains(
                        "Loading combat tracker telemetry",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return line.Trim();
                }
            }

            return "Not found in first 40 lines";
        }
        catch (Exception exception)
        {
            return "Unavailable · " + exception.Message;
        }
    }

    private bool HasPalworldExited()
    {
        try
        {
            return _palworldProcess?.HasExited ?? true;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsPlayerActor(string actorName)
    {
        return actorName.Contains(
            "BP_Player_",
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static string GetFriendlyActorName(string? actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName))
        {
            return "Unknown target";
        }

        string value = actorName;

        if (value.StartsWith("BP_", StringComparison.OrdinalIgnoreCase))
        {
            value = value[3..];
        }

        int generatedClassIndex = value.LastIndexOf(
            "_C_",
            StringComparison.OrdinalIgnoreCase
        );

        if (generatedClassIndex > 0)
        {
            value = value[..generatedClassIndex];
        }
        else if (value.EndsWith("_C", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^2];
        }

        return value.Replace('_', ' ');
    }

    private static string NormalizeSourceName(string? sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return string.Empty;
        }

        string value = sourceName.Trim();

        if (value.Equals("nil", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("invalid", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("FString:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("FText:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return value;
    }

    private void UpdateInterface()
    {
        // Data can arrive every 50 ms, but the visible tracker refreshes only
        // on the one-second UI timer.
        _interfaceDirty = true;
    }

    private void RenderInterface()
    {
        double combatDuration;

        if (_encounterActive)
        {
            combatDuration = Math.Max(
                _applicationClock.Elapsed.TotalSeconds - _encounterStartSeconds,
                0
            );

            _displayedPlayerDps = combatDuration > 0.01
                ? _playerDamage / combatDuration
                : _playerDamage;
            _displayedPalDps = combatDuration > 0.01
                ? _palDamage / combatDuration
                : _palDamage;
            _displayedCombinedDps = combatDuration > 0.01
                ? (_playerDamage + _palDamage) / combatDuration
                : _playerDamage + _palDamage;
        }
        else
        {
            combatDuration = _finalizedDurationSeconds;
        }

        TargetNameValueText.Text = _activeTargetName is null
            ? _targetPlaceholder
            : GetKnownActorDisplayName(_activeTargetName);

        TimeSpan displayedTime = TimeSpan.FromSeconds(
            Math.Max(combatDuration, 0)
        );

        HeaderCombatTimeText.Text = displayedTime.TotalHours >= 1
            ? displayedTime.ToString(@"hh\:mm\:ss")
            : displayedTime.ToString(@"mm\:ss");

        RenderTelemetryHealth();

        TargetBadgeText.Text = _targetConfirmedDead
            ? "DEFEATED"
            : _encounterPaused
                ? "PAUSED"
                : _encounterActive
                    ? "ACTIVE"
                    : "TARGET";

        TargetTelemetryText.Text = _targetConfirmedDead
            ? "TARGET DEFEATED"
            : "LIVE HP NOT AVAILABLE";

        TargetActivityBar.Value =
            _encounterActive ||
            _encounterPaused ||
            _targetConfirmedDead
                ? 100
                : 0;

        SummaryDpsValueText.Text =
            _displayedCombinedDps.ToString("N0");
        SummaryMetaText.Text =
            $"{_totalDamage:N0} DAMAGE  •  {combatDuration:F1} SEC";

        // Combined player + Pal damage across every tracked combatant is
        // shown as Total DPS inside the collapsible Encounter Details panel.
        UpdateCombatantDps(combatDuration);
        RenderCombatantRows();
        RenderDamageSourceRows();
        RenderPalSkillRows();

        TotalDpsValueText.Text = _displayedCombinedDps.ToString("N0");
        TotalDamageValueText.Text = _totalDamage.ToString("N0");
        CombatTimeValueText.Text = $"{combatDuration:F2} s";

        StopButton.IsEnabled =
            _encounterActive || _encounterPaused;

        // Exact live HP is not included in the current telemetry format yet.
        // Only a confirmed game death can safely display zero. A manual stop
        // freezes results without claiming that the target died.
        CurrentHpValueText.Text = _targetConfirmedDead ? "0" : "—";
    }

    private void Header_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_overlayLocked &&
            e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(
        object? sender,
        CancelEventArgs e)
    {
        _closing = true;

        Dispatcher.UnhandledException -= MainWindow_DispatcherUnhandledException;

        _maintenanceTimer.Stop();
        _pollTimer.Stop();
        _uiTimer.Stop();

        if (_overlayToggleWindow is not null)
        {
            _overlayToggleWindow.ToggleRequested -=
                OverlayToggleWindow_ToggleRequested;
            _overlayToggleWindow.Close();
            _overlayToggleWindow = null;
        }

        if (_historyWindow is not null)
        {
            _historyWindow.Close();
            _historyWindow = null;
        }

        DisconnectTelemetry();
    }
}

internal enum TelemetryConnectionState
{
    WaitingForPalworld,
    WaitingForTelemetry,
    Connected,
    AttachmentError,
    TelemetryError
}

internal sealed class PalOwnerInfo
{
    public PalOwnerInfo(
        string ownerActorId,
        string ownerDisplayName)
    {
        OwnerActorId = ownerActorId;
        OwnerDisplayName = ownerDisplayName;
    }

    public string OwnerActorId { get; set; }
    public string OwnerDisplayName { get; set; }
}

internal sealed class PendingDamageMetadataMatch
{
    public PendingDamageMetadataMatch(
        double telemetryTimestamp,
        int damage,
        string attacker,
        string defender,
        string sourceType,
        string? sourceName,
        string fallbackSourceKey)
    {
        TelemetryTimestamp = telemetryTimestamp;
        Damage = damage;
        OriginalAttacker = attacker;
        Defender = defender;
        OriginalSourceType = sourceType;
        OriginalSourceName = sourceName;
        FallbackSourceKey = fallbackSourceKey;

        AttributedActor = attacker;
        AttributedSourceType = sourceType;
        AttributedSourceName = sourceName;
    }

    public double TelemetryTimestamp { get; }
    public int Damage { get; }
    public string OriginalAttacker { get; }
    public string Defender { get; }
    public string OriginalSourceType { get; }
    public string? OriginalSourceName { get; }
    public string FallbackSourceKey { get; }

    public string AttributedActor { get; set; }
    public string AttributedSourceType { get; set; }
    public string? AttributedSourceName { get; set; }
    public string? ExactSourceLabel { get; set; }
    public bool HasAttributionOverride { get; set; }
    public bool AggregateReassigned { get; set; }
}

internal sealed class PendingPalSkillActivation
{
    public PendingPalSkillActivation(
        double telemetryTimestamp,
        int sequence,
        string actorId,
        string sourceType,
        string palName,
        string targetActorId,
        string actionInstance,
        string skillId,
        string skillName)
    {
        TelemetryTimestamp = telemetryTimestamp;
        Sequence = sequence;
        ActorId = actorId;
        SourceType = sourceType;
        PalName = palName;
        TargetActorId = targetActorId;
        ActionInstance = actionInstance;
        SkillId = skillId;
        SkillName = skillName;
    }

    public double TelemetryTimestamp { get; }
    public int Sequence { get; }
    public string ActorId { get; }
    public string SourceType { get; }
    public string PalName { get; }
    public string TargetActorId { get; }
    public string ActionInstance { get; }
    public string SkillId { get; }
    public string SkillName { get; }
    public bool Counted { get; set; }
}

internal sealed class PalSkillRuntimeEntry
{
    public PalSkillRuntimeEntry(
        string actorId,
        string sourceType,
        string palName,
        string skillId,
        string skillName,
        int firstSeenOrder)
    {
        ActorId = actorId;
        SourceType = sourceType;
        PalName = palName;
        SkillId = skillId;
        SkillName = skillName;
        FirstSeenOrder = firstSeenOrder;
    }

    public string ActorId { get; }
    public string SourceType { get; }
    public string PalName { get; set; }
    public string SkillId { get; set; }
    public string SkillName { get; }
    public int FirstSeenOrder { get; }
    public int CastCount { get; set; }
}

internal sealed class DamageSourceEntry
{
    public DamageSourceEntry(
        string actorId,
        string sourceType,
        string sourceName,
        string sourceLabel,
        int firstSeenOrder)
    {
        ActorId = actorId;
        SourceType = sourceType;
        SourceName = sourceName;
        SourceLabel = sourceLabel;
        FirstSeenOrder = firstSeenOrder;
    }

    public string ActorId { get; }
    public string SourceType { get; }
    public string SourceName { get; }
    public string SourceLabel { get; }
    public int FirstSeenOrder { get; }
    public long Damage { get; set; }
    public int HitCount { get; set; }
    public int WeakHitCount { get; set; }
    public int StrongHitCount { get; set; }
}

internal sealed class PalSkillDisplayRow
{
    private PalSkillDisplayRow(
        string displayName,
        string damageText,
        string hitCastText,
        string averageText,
        Brush nameBrush,
        Brush damageBrush,
        Brush dividerBrush,
        double rowHeight,
        double nameFontSize,
        FontWeight nameFontWeight,
        Thickness nameMargin)
    {
        DisplayName = displayName;
        DamageText = damageText;
        HitCastText = hitCastText;
        AverageText = averageText;
        NameBrush = nameBrush;
        DamageBrush = damageBrush;
        DividerBrush = dividerBrush;
        RowHeight = rowHeight;
        NameFontSize = nameFontSize;
        NameFontWeight = nameFontWeight;
        NameMargin = nameMargin;
    }

    public static PalSkillDisplayRow CreatePal(
        string displayName,
        long damage)
    {
        return new PalSkillDisplayRow(
            displayName,
            damage.ToString("N0"),
            string.Empty,
            string.Empty,
            ThemeResourceHelper.GetBrush(
                "SourceCombatantNameBrush",
                "#F2DADA"),
            ThemeResourceHelper.GetBrush(
                "SourceCombatantDamageBrush",
                "#FF5656"),
            ThemeResourceHelper.GetBrush(
                "SourceCombatantDividerBrush",
                "#5A2020"),
            27,
            10.5,
            FontWeights.Bold,
            new Thickness(0)
        );
    }

    public static PalSkillDisplayRow CreateSkill(
        string displayName,
        long damage,
        int hitCount,
        int castCount,
        double averagePerCast)
    {
        string casts = castCount > 0
            ? $"{castCount:N0}C"
            : "—C";
        string average = castCount > 0
            ? $"{averagePerCast:N0}/C"
            : "—/C";

        return new PalSkillDisplayRow(
            displayName,
            damage.ToString("N0"),
            $"{hitCount:N0}H · {casts}",
            average,
            ThemeResourceHelper.GetBrush(
                "SourceNameBrush",
                "#CDAFAF"),
            ThemeResourceHelper.GetBrush(
                "SourceDamageBrush",
                "#F0CACA"),
            ThemeResourceHelper.GetBrush(
                "SourceDividerBrush",
                "#281111"),
            23,
            9.5,
            FontWeights.SemiBold,
            new Thickness(10, 0, 0, 0)
        );
    }

    public static PalSkillDisplayRow CreatePlaceholder(
        string displayName)
    {
        Brush placeholderBrush = ThemeResourceHelper.GetBrush(
            "PlaceholderBrush",
            "#8C6969");

        return new PalSkillDisplayRow(
            displayName,
            string.Empty,
            string.Empty,
            string.Empty,
            placeholderBrush,
            placeholderBrush,
            ThemeResourceHelper.GetBrush(
                "SourceDividerBrush",
                "#281111"),
            23,
            9.5,
            FontWeights.Normal,
            new Thickness(0)
        );
    }

    public string DisplayName { get; }
    public string DamageText { get; }
    public string HitCastText { get; }
    public string AverageText { get; }
    public Brush NameBrush { get; }
    public Brush DamageBrush { get; }
    public Brush DividerBrush { get; }
    public double RowHeight { get; }
    public double NameFontSize { get; }
    public FontWeight NameFontWeight { get; }
    public Thickness NameMargin { get; }
}

internal static class ThemeResourceHelper
{
    public static Brush GetBrush(
        string resourceKey,
        string fallbackColor)
    {
        object? resource =
            Application.Current?.TryFindResource(resourceKey);

        if (resource is Brush brush)
        {
            return brush.CloneCurrentValue();
        }

        return new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(fallbackColor)
        );
    }
}

internal sealed class DamageSourceDisplayRow
{
    private DamageSourceDisplayRow(
        string displayName,
        string damageText,
        string percentText,
        Brush nameBrush,
        Brush damageBrush,
        Brush dividerBrush,
        double rowHeight,
        double nameFontSize,
        double damageFontSize,
        FontWeight nameFontWeight,
        Thickness nameMargin)
    {
        DisplayName = displayName;
        DamageText = damageText;
        PercentText = percentText;
        NameBrush = nameBrush;
        DamageBrush = damageBrush;
        DividerBrush = dividerBrush;
        RowHeight = rowHeight;
        NameFontSize = nameFontSize;
        DamageFontSize = damageFontSize;
        NameFontWeight = nameFontWeight;
        NameMargin = nameMargin;
    }

    public static DamageSourceDisplayRow CreateCombatant(
        string displayName,
        long damage)
    {
        return new DamageSourceDisplayRow(
            displayName,
            damage.ToString("N0"),
            string.Empty,
            ThemeResourceHelper.GetBrush(
                "SourceCombatantNameBrush",
                "#F2DADA"),
            ThemeResourceHelper.GetBrush(
                "SourceCombatantDamageBrush",
                "#FF5656"),
            ThemeResourceHelper.GetBrush(
                "SourceCombatantDividerBrush",
                "#5A2020"),
            26,
            11,
            11.5,
            FontWeights.Bold,
            new Thickness(0)
        );
    }

    public static DamageSourceDisplayRow CreateSource(
        string displayName,
        long damage,
        double percentage)
    {
        return new DamageSourceDisplayRow(
            displayName,
            damage.ToString("N0"),
            $"{percentage:F0}%",
            ThemeResourceHelper.GetBrush(
                "SourceNameBrush",
                "#CDAFAF"),
            ThemeResourceHelper.GetBrush(
                "SourceDamageBrush",
                "#F0CACA"),
            ThemeResourceHelper.GetBrush(
                "SourceDividerBrush",
                "#281111"),
            22,
            10,
            10,
            FontWeights.SemiBold,
            new Thickness(10, 0, 0, 0)
        );
    }

    public static DamageSourceDisplayRow CreatePlaceholder(
        string displayName)
    {
        Brush placeholderBrush = ThemeResourceHelper.GetBrush(
            "PlaceholderBrush",
            "#8C6969"
        );

        return new DamageSourceDisplayRow(
            displayName,
            string.Empty,
            string.Empty,
            placeholderBrush,
            placeholderBrush,
            ThemeResourceHelper.GetBrush(
                "SourceDividerBrush",
                "#281111"),
            22,
            10,
            9.5,
            FontWeights.Normal,
            new Thickness(0)
        );
    }

    public string DisplayName { get; }
    public string DamageText { get; }
    public string PercentText { get; }
    public Brush NameBrush { get; }
    public Brush DamageBrush { get; }
    public Brush DividerBrush { get; }
    public double RowHeight { get; }
    public double NameFontSize { get; }
    public double DamageFontSize { get; }
    public FontWeight NameFontWeight { get; }
    public Thickness NameMargin { get; }
}

internal sealed class CombatantEntry
{
    public CombatantEntry(
        string actorId,
        string sourceType,
        string displayName,
        int firstSeenOrder)
    {
        ActorId = actorId;
        SourceType = sourceType;
        DisplayName = displayName;
        FirstSeenOrder = firstSeenOrder;
    }

    public string ActorId { get; }
    public string SourceType { get; }
    public string DisplayName { get; set; }
    public string? OwnerActorId { get; set; }
    public string? OwnerDisplayName { get; set; }
    public int FirstSeenOrder { get; }
    public long Damage { get; set; }
    public double DisplayedDps { get; set; }
}

internal sealed class CombatantDisplayRow
{
    private CombatantDisplayRow(
        string displayName,
        double displayedDps,
        double contributionPercent,
        Brush dpsBrush,
        Brush nameBrush,
        Brush indicatorBrush,
        Brush dividerBrush,
        Brush barBrush,
        string indicator,
        double rowHeight,
        double nameFontSize,
        double dpsFontSize,
        FontWeight nameFontWeight,
        Thickness nameMargin,
        double indicatorFontSize)
    {
        DisplayName = displayName;
        DpsText = displayedDps.ToString("N0");
        ContributionPercent = NormalizePercentage(
            contributionPercent
        );
        DpsBrush = dpsBrush;
        NameBrush = nameBrush;
        IndicatorBrush = indicatorBrush;
        DividerBrush = dividerBrush;
        BarBrush = barBrush;
        Indicator = indicator;
        RowHeight = rowHeight;
        NameFontSize = nameFontSize;
        DpsFontSize = dpsFontSize;
        NameFontWeight = nameFontWeight;
        NameMargin = nameMargin;
        IndicatorFontSize = indicatorFontSize;
    }

    private static double NormalizePercentage(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 100);
    }

    public static CombatantDisplayRow CreateOwnerGroup(
        string displayName,
        double displayedDps,
        double contributionPercent,
        bool isLeadingGroup)
    {
        return new CombatantDisplayRow(
            displayName,
            displayedDps,
            contributionPercent,
            ThemeResourceHelper.GetBrush(
                "RowGroupDpsBrush",
                "#FF3838"),
            ThemeResourceHelper.GetBrush(
                "RowGroupNameBrush",
                "#FFF0F0"),
            ThemeResourceHelper.GetBrush(
                "RowGroupIndicatorBrush",
                "#D47474"),
            ThemeResourceHelper.GetBrush(
                "RowGroupDividerBrush",
                "#5A2020"),
            ThemeResourceHelper.GetBrush(
                "ProgressGroupFillBrush",
                "#FF3838"),
            isLeadingGroup ? "★" : "›",
            36,
            12.5,
            18,
            FontWeights.Bold,
            new Thickness(0),
            isLeadingGroup ? 12 : 14
        );
    }

    public static CombatantDisplayRow CreateCombatant(
        string displayName,
        double displayedDps,
        double contributionPercent,
        string sourceType)
    {
        bool isPal = sourceType.Contains(
            "PAL",
            StringComparison.OrdinalIgnoreCase
        );

        return new CombatantDisplayRow(
            displayName,
            displayedDps,
            contributionPercent,
            ThemeResourceHelper.GetBrush(
                isPal
                    ? "RowChildDpsPalBrush"
                    : "RowChildDpsPlayerBrush",
                isPal ? "#FF9292" : "#FF6464"),
            ThemeResourceHelper.GetBrush(
                "RowChildNameBrush",
                "#D9CACA"),
            ThemeResourceHelper.GetBrush(
                "RowChildIndicatorBrush",
                "#7F4B4B"),
            ThemeResourceHelper.GetBrush(
                "RowChildDividerBrush",
                "#2A1111"),
            ThemeResourceHelper.GetBrush(
                isPal
                    ? "ProgressFillPalBrush"
                    : "ProgressFillBrush",
                isPal ? "#FF9292" : "#FF6464"),
            isPal ? "◆" : "•",
            30,
            11,
            14,
            FontWeights.SemiBold,
            new Thickness(3, 0, 0, 0),
            isPal ? 8 : 12
        );
    }

    public string DisplayName { get; }
    public string DpsText { get; }
    public double ContributionPercent { get; }
    public Brush DpsBrush { get; }
    public Brush NameBrush { get; }
    public Brush IndicatorBrush { get; }
    public Brush DividerBrush { get; }
    public Brush BarBrush { get; }
    public string Indicator { get; }
    public double RowHeight { get; }
    public double NameFontSize { get; }
    public double DpsFontSize { get; }
    public FontWeight NameFontWeight { get; }
    public Thickness NameMargin { get; }
    public double IndicatorFontSize { get; }
}

internal sealed class OverlayToggleWindow : Window
{
    private readonly Button _toggleButton;
    private bool _isLocked;

    public event EventHandler? ToggleRequested;

    public OverlayToggleWindow(Window owner)
    {
        Owner = owner;
        Width = 30;
        Height = 30;
        MinWidth = 30;
        MinHeight = 30;
        MaxWidth = 30;
        MaxHeight = 30;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _toggleButton = CreateOverlayButton(
            "🔓",
            "Enable click-through and lock position",
            14
        );

        _toggleButton.Click += (_, _) =>
            ToggleRequested?.Invoke(this, EventArgs.Empty);

        Content = _toggleButton;

        ApplyTheme();
    }

    private static Button CreateOverlayButton(
        string content,
        string toolTip,
        double fontSize)
    {
        return new Button
        {
            Content = content,
            ToolTip = toolTip,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = fontSize,
            Cursor = Cursors.Hand
        };
    }

    public void ApplyTheme()
    {
        _toggleButton.Foreground = ThemeResourceHelper.GetBrush(
            "ToggleForegroundBrush",
            "#F7F2F2"
        );

        _toggleButton.Background = ThemeResourceHelper.GetBrush(
            _isLocked
                ? "ToggleWindowLockedBackgroundBrush"
                : "ToggleWindowBackgroundBrush",
            _isLocked ? "#E8311111" : "#E8170A0A"
        );
        _toggleButton.BorderBrush = ThemeResourceHelper.GetBrush(
            _isLocked
                ? "ToggleWindowLockedBorderBrush"
                : "ToggleWindowBorderBrush",
            _isLocked ? "#D84A4A" : "#8A3030"
        );
    }

    public void SetLocked(bool locked)
    {
        _isLocked = locked;
        _toggleButton.Content = locked ? "🔒" : "🔓";
        _toggleButton.ToolTip = locked
            ? "Disable click-through and unlock position"
            : "Enable click-through and lock position";

        ApplyTheme();
    }
}
