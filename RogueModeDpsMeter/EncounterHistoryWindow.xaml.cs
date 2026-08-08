using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RogueModeDpsMeter;

public partial class EncounterHistoryWindow : Window
{
    private const string RaidTeamKey = "__RAID_TEAM__";
    private const string UnassignedTeamKey = "__UNASSIGNED_PALS__";

    private readonly IList<EncounterSnapshot> _encounters;
    private readonly Action _historyChanged;
    private readonly string _currentSessionId;
    private readonly string _currentTheme;
    private List<EncounterSnapshot> _filteredEncounters = new();
    private bool _isEditingMetadata;
    private bool _statsVisible;

    public EncounterHistoryWindow(
        IList<EncounterSnapshot> encounters,
        Action historyChanged,
        string currentTheme,
        string currentSessionId)
    {
        _encounters = encounters;
        _historyChanged = historyChanged;
        _currentSessionId = currentSessionId;
        _currentTheme = currentTheme;
        InitializeComponent();
        ApplyTheme(currentTheme);
        RefreshEncounters(selectNewest: true);
    }

    public void ApplyTheme(string themeName)
    {
        string normalizedTheme = themeName switch
        {
            "Classic" => "Classic",
            "Potatoe" => "Potatoe",
            "BelleNoire" => "BelleNoire",
            "Solenne" => "Solenne",
            "JormuntideIgnis" => "JormuntideIgnis",
            "Sekhmet" => "Sekhmet",
            "Palworld" => "Palworld",
            _ => "RM"
        };

        try
        {
            ResourceDictionary themeDictionary = new()
            {
                Source = new Uri(
                    $"Themes/{normalizedTheme}.xaml",
                    UriKind.Relative
                )
            };

            Resources.MergedDictionaries[0] = themeDictionary;
        }
        catch
        {
            // Keep the already-loaded RM theme if another skin is unavailable.
        }
    }

    public void RefreshEncounters(bool selectNewest)
    {
        string? selectedId =
            (EncounterList.SelectedItem as EncounterSnapshot)?.Id;

        ApplyFilter(selectedId, selectNewest);
    }

    private void SearchTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        string? selectedId =
            (EncounterList.SelectedItem as EncounterSnapshot)?.Id;

        ApplyFilter(selectedId, selectNewest: false);
    }

    private void ApplyFilter(
        string? preferredEncounterId,
        bool selectNewest)
    {
        string query = SearchTextBox.Text.Trim();

        RefreshAnalytics();

        _filteredEncounters = _encounters
            .Where(encounter => MatchesSearch(encounter, query))
            .OrderByDescending(encounter => encounter.EndedAtUtc)
            .ToList();

        EncounterList.ItemsSource = null;
        EncounterList.ItemsSource = _filteredEncounters;

        UpdateHistoryCountText(query);

        if (_filteredEncounters.Count == 0)
        {
            EncounterList.SelectedIndex = -1;
            ShowEmptyState();
            return;
        }

        int desiredIndex = 0;

        if (!selectNewest && !string.IsNullOrWhiteSpace(preferredEncounterId))
        {
            int preferredIndex = _filteredEncounters.FindIndex(encounter =>
                encounter.Id.Equals(
                    preferredEncounterId,
                    StringComparison.Ordinal
                )
            );

            if (preferredIndex >= 0)
            {
                desiredIndex = preferredIndex;
            }
        }

        EncounterList.SelectedIndex = desiredIndex;
        EncounterList.ScrollIntoView(EncounterList.SelectedItem);
        RenderSelectedEncounter();
    }

    private static bool MatchesSearch(
        EncounterSnapshot encounter,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return encounter.DisplayTitle.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) ||
               encounter.TargetName.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) ||
               encounter.Notes.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) ||
               encounter.EndReason.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase) ||
               encounter.EncounterDateText.Contains(
                   query,
                   StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateHistoryCountText(string query)
    {
        HistoryHeaderCountText.Text = _encounters.Count == 1
            ? "1 SAVED ENCOUNTER"
            : $"{_encounters.Count} SAVED ENCOUNTERS";

        SearchResultCountText.Text = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : $"{_filteredEncounters.Count} / {_encounters.Count}";
    }

    private void EncounterList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        CancelMetadataEdit();
        RenderSelectedEncounter();
    }

    private void RenderSelectedEncounter()
    {
        if (EncounterList.SelectedItem is not EncounterSnapshot encounter)
        {
            ShowEmptyState();
            return;
        }

        EmptyHistoryText.Visibility = Visibility.Collapsed;
        EncounterDetailScrollViewer.Visibility = Visibility.Visible;

        DetailTargetText.Text = encounter.DisplayTitle;
        DetailOriginalTargetText.Text = $"Target: {encounter.TargetName}";
        DetailOriginalTargetText.Visibility = encounter.IsRenamed
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailDateText.Text = encounter.EncounterDateText;
        DetailEndReasonText.Text = encounter.EndReason.ToUpperInvariant();
        DetailDurationText.Text = encounter.DurationText;
        DetailDpsText.Text = encounter.TeamDps.ToString("N0");
        DetailDamageText.Text = encounter.TotalDamageText;
        DetailContributorCountText.Text =
            (encounter.Combatants?.Count ?? 0).ToString("N0");
        DetailNotesText.Text = string.IsNullOrWhiteSpace(encounter.Notes)
            ? "No notes added."
            : encounter.Notes;

        ContributorRowsControl.ItemsSource =
            BuildContributorRows(encounter);
        DamageSourceRowsControl.ItemsSource =
            BuildDamageSourceRows(encounter);
        PalSkillRowsControl.ItemsSource =
            BuildPalSkillRows(encounter);

        int selectedIndex = EncounterList.SelectedIndex;
        HistoryPositionText.Text =
            $"Encounter {selectedIndex + 1} of {_filteredEncounters.Count}";

        PreviousEncounterButton.IsEnabled = selectedIndex > 0;
        NextEncounterButton.IsEnabled =
            selectedIndex >= 0 &&
            selectedIndex < _filteredEncounters.Count - 1;
        PinEncounterButton.IsEnabled = true;
        PinEncounterButton.Content = encounter.IsPinned ? "UNPIN" : "PIN";
        EditEncounterButton.IsEnabled = true;
        CopySummaryButton.IsEnabled = true;
        CopySummaryButton.Content = "COPY";
        DeleteEncounterButton.IsEnabled = true;
        ClearHistoryButton.IsEnabled = _encounters.Count > 0;
        ExportSelectedButton.IsEnabled = true;

        EncounterDetailScrollViewer.ScrollToTop();
    }

    private void ShowEmptyState()
    {
        EmptyHistoryText.Text = _encounters.Count == 0
            ? "No saved encounters yet."
            : "No encounters match your search.";
        EmptyHistoryText.Visibility = Visibility.Visible;
        EncounterDetailScrollViewer.Visibility = Visibility.Collapsed;
        HistoryPositionText.Text = "Encounter 0 of 0";
        PreviousEncounterButton.IsEnabled = false;
        NextEncounterButton.IsEnabled = false;
        PinEncounterButton.IsEnabled = false;
        EditEncounterButton.IsEnabled = false;
        CopySummaryButton.IsEnabled = false;
        DeleteEncounterButton.IsEnabled = false;
        ClearHistoryButton.IsEnabled = _encounters.Count > 0;
        ExportSelectedButton.IsEnabled = false;
        CancelMetadataEdit();
    }

    private static List<HistoryContributorRow> BuildContributorRows(
        EncounterSnapshot encounter)
    {
        List<EncounterCombatantSnapshot> combatants =
            encounter.Combatants ?? new List<EncounterCombatantSnapshot>();

        var groups = combatants
            .Where(combatant => combatant.Damage > 0)
            .GroupBy(
                combatant =>
                    combatant.SourceType.Equals(
                        "RAID_PAL",
                        StringComparison.OrdinalIgnoreCase)
                        ? RaidTeamKey
                        : string.IsNullOrWhiteSpace(combatant.OwnerActorId)
                            ? UnassignedTeamKey
                            : combatant.OwnerActorId!,
                StringComparer.Ordinal
            )
            .Select(group => new
            {
                Key = group.Key,
                DisplayName = group.Key == RaidTeamKey
                    ? "Raid Team"
                    : group.Key == UnassignedTeamKey
                        ? "Unassigned Pals"
                        : $"Team {group
                            .Select(combatant => combatant.OwnerDisplayName)
                            .FirstOrDefault(name =>
                                !string.IsNullOrWhiteSpace(name)) ?? "Unknown"}",
                Damage = group.Sum(combatant => combatant.Damage),
                Dps = group.Sum(combatant => combatant.Dps),
                FirstSeenOrder = group.Min(
                    combatant => combatant.FirstSeenOrder),
                Combatants = group
                    .OrderByDescending(combatant => combatant.Damage)
                    .ThenBy(combatant => combatant.FirstSeenOrder)
                    .ToList()
            })
            .OrderByDescending(group => group.Damage)
            .ThenBy(group => group.FirstSeenOrder)
            .ToList();

        double totalDps = Math.Max(
            groups.Sum(group => group.Dps),
            0.01
        );

        List<HistoryContributorRow> rows = new();

        foreach (var group in groups)
        {
            rows.Add(HistoryContributorRow.CreateGroup(
                group.DisplayName,
                group.Damage,
                group.Dps,
                group.Dps * 100.0 / totalDps
            ));

            foreach (EncounterCombatantSnapshot combatant in group.Combatants)
            {
                rows.Add(HistoryContributorRow.CreateCombatant(
                    combatant.DisplayName,
                    combatant.Damage,
                    combatant.Dps,
                    combatant.Dps * 100.0 / totalDps
                ));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(HistoryContributorRow.CreatePlaceholder(
                "No contributor data"
            ));
        }

        return rows;
    }

    private static List<HistoryDamageSourceRow> BuildDamageSourceRows(
        EncounterSnapshot encounter)
    {
        List<EncounterDamageSourceSnapshot> sources =
            encounter.DamageSources ?? new List<EncounterDamageSourceSnapshot>();

        var groups = sources
            .Where(source => source.Damage > 0)
            .GroupBy(source => source.ActorId, StringComparer.Ordinal)
            .Select(group => new
            {
                DisplayName = ResolveDamageSourceActorName(group),
                Damage = group.Sum(source => source.Damage),
                FirstSeenOrder = group.Min(source => source.FirstSeenOrder),
                Sources = group
                    .OrderByDescending(source => source.Damage)
                    .ThenBy(source => source.FirstSeenOrder)
                    .ToList()
            })
            .OrderByDescending(group => group.Damage)
            .ThenBy(group => group.FirstSeenOrder)
            .ToList();

        List<HistoryDamageSourceRow> rows = new();

        foreach (var group in groups)
        {
            rows.Add(HistoryDamageSourceRow.CreateGroup(
                group.DisplayName,
                group.Damage
            ));

            foreach (EncounterDamageSourceSnapshot source in group.Sources)
            {
                double percentage = group.Damage > 0
                    ? source.Damage * 100.0 / group.Damage
                    : 0;

                rows.Add(HistoryDamageSourceRow.CreateSource(
                    source.SourceLabel,
                    source.Damage,
                    percentage
                ));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(HistoryDamageSourceRow.CreatePlaceholder(
                "No exact source data"
            ));
        }

        return rows;
    }

    private static List<HistoryPalSkillRow> BuildPalSkillRows(
        EncounterSnapshot encounter)
    {
        List<EncounterPalSkillSnapshot> skills =
            encounter.PalSkills ?? new List<EncounterPalSkillSnapshot>();

        var groups = skills
            .Where(skill => skill.Damage > 0)
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

        List<HistoryPalSkillRow> rows = new();

        foreach (var group in groups)
        {
            rows.Add(HistoryPalSkillRow.CreateGroup(
                group.PalName,
                group.Damage));

            foreach (EncounterPalSkillSnapshot skill in group.Skills)
            {
                rows.Add(HistoryPalSkillRow.CreateSkill(
                    skill.SkillName,
                    skill.Damage,
                    skill.HitCount,
                    skill.CastCount,
                    skill.AverageDamagePerCast));
            }
        }

        if (rows.Count == 0)
        {
            rows.Add(HistoryPalSkillRow.CreatePlaceholder(
                "No Pal skill attribution recorded"));
        }

        return rows;
    }

    private static string ResolveDamageSourceActorName(
        IEnumerable<EncounterDamageSourceSnapshot> sources)
    {
        EncounterDamageSourceSnapshot first = sources.First();

        if (!string.IsNullOrWhiteSpace(first.SourceName) &&
            !first.SourceName.Equals(
                "unknown",
                StringComparison.OrdinalIgnoreCase))
        {
            return first.SourceName;
        }

        return string.IsNullOrWhiteSpace(first.ActorId)
            ? "Unknown Source"
            : first.ActorId;
    }

    private void StatsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _statsVisible = !_statsVisible;
        StatsPanelBorder.Visibility = _statsVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatsButton.Content = _statsVisible ? "HIDE STATS" : "STATS";
        StatsButton.Width = _statsVisible ? 82 : 64;

        if (_statsVisible)
        {
            RefreshAnalytics();
        }
    }

    private void RefreshAnalytics()
    {
        foreach (EncounterSnapshot encounter in _encounters)
        {
            encounter.IsRecord = false;
            encounter.RecordSummary = string.Empty;
        }

        Dictionary<EncounterSnapshot, List<string>> recordLabels = new();

        MarkMaximumRecords(
            _encounters,
            encounter => encounter.TeamDps,
            "Highest Team DPS",
            recordLabels
        );
        MarkMaximumRecords(
            _encounters,
            encounter => encounter.PlayerDps,
            "Highest Player DPS",
            recordLabels
        );
        MarkMaximumRecords(
            _encounters,
            encounter => encounter.PalDps,
            "Highest Pal DPS",
            recordLabels
        );
        MarkMaximumRecords(
            _encounters,
            encounter => encounter.TotalDamage,
            "Highest Total Damage",
            recordLabels
        );

        foreach (IGrouping<string, EncounterSnapshot> targetGroup in
                 GetDefeatedEncounters(_encounters)
                     .GroupBy(
                         encounter => encounter.TargetName.Trim(),
                         StringComparer.OrdinalIgnoreCase))
        {
            double fastestDuration = targetGroup.Min(
                encounter => encounter.DurationSeconds
            );

            foreach (EncounterSnapshot encounter in targetGroup.Where(
                         encounter => NearlyEqual(
                             encounter.DurationSeconds,
                             fastestDuration)))
            {
                AddRecordLabel(
                    recordLabels,
                    encounter,
                    $"Fastest {encounter.TargetName} kill"
                );
            }
        }

        foreach (KeyValuePair<EncounterSnapshot, List<string>> pair in
                 recordLabels)
        {
            pair.Key.IsRecord = true;
            pair.Key.RecordSummary = string.Join(" · ", pair.Value);
        }

        SessionStatsControl.ItemsSource = BuildSessionStats();
        AllTimeStatsControl.ItemsSource = BuildAllTimeStats();
        ExportAllButton.IsEnabled = _encounters.Count > 0;
        CompareButton.IsEnabled = _encounters.Count >= 2;
    }

    private static void MarkMaximumRecords(
        IEnumerable<EncounterSnapshot> encounters,
        Func<EncounterSnapshot, double> valueSelector,
        string label,
        IDictionary<EncounterSnapshot, List<string>> recordLabels)
    {
        List<EncounterSnapshot> valid = encounters
            .Where(encounter => valueSelector(encounter) > 0)
            .ToList();

        if (valid.Count == 0)
        {
            return;
        }

        double maximum = valid.Max(valueSelector);

        foreach (EncounterSnapshot encounter in valid.Where(
                     encounter => NearlyEqual(
                         valueSelector(encounter),
                         maximum)))
        {
            AddRecordLabel(recordLabels, encounter, label);
        }
    }

    private static void AddRecordLabel(
        IDictionary<EncounterSnapshot, List<string>> recordLabels,
        EncounterSnapshot encounter,
        string label)
    {
        if (!recordLabels.TryGetValue(encounter, out List<string>? labels))
        {
            labels = new List<string>();
            recordLabels[encounter] = labels;
        }

        if (!labels.Contains(label, StringComparer.Ordinal))
        {
            labels.Add(label);
        }
    }

    private static bool NearlyEqual(double left, double right)
    {
        double tolerance = Math.Max(
            0.005,
            Math.Max(Math.Abs(left), Math.Abs(right)) * 0.000000001
        );

        return Math.Abs(left - right) <= tolerance;
    }

    private List<HistoryStatCard> BuildSessionStats()
    {
        List<EncounterSnapshot> sessionEncounters = _encounters
            .Where(encounter => encounter.SessionId.Equals(
                _currentSessionId,
                StringComparison.Ordinal))
            .OrderByDescending(encounter => encounter.EndedAtUtc)
            .ToList();

        long totalDamage = sessionEncounters.Sum(
            encounter => encounter.TotalDamage
        );
        double averageDps = sessionEncounters.Count > 0
            ? sessionEncounters.Average(encounter => encounter.TeamDps)
            : 0;
        EncounterSnapshot? bestDps = sessionEncounters
            .OrderByDescending(encounter => encounter.TeamDps)
            .FirstOrDefault();
        EncounterSnapshot? fastest = GetDefeatedEncounters(sessionEncounters)
            .OrderBy(encounter => encounter.DurationSeconds)
            .FirstOrDefault();

        var mostUsedPal = sessionEncounters
            .SelectMany(encounter =>
                encounter.Combatants ??
                new List<EncounterCombatantSnapshot>())
            .Where(combatant => combatant.SourceType.Equals(
                "PAL",
                StringComparison.OrdinalIgnoreCase))
            .Where(combatant => !string.IsNullOrWhiteSpace(
                combatant.DisplayName))
            .GroupBy(
                combatant => combatant.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.First().DisplayName,
                Appearances = group.Count(),
                Damage = group.Sum(combatant => combatant.Damage)
            })
            .OrderByDescending(group => group.Appearances)
            .ThenByDescending(group => group.Damage)
            .FirstOrDefault();

        return new List<HistoryStatCard>
        {
            new(
                "ENCOUNTERS",
                sessionEncounters.Count.ToString("N0"),
                "Saved this launch"
            ),
            new(
                "TOTAL DAMAGE",
                totalDamage > 0 ? totalDamage.ToString("N0") : "—",
                "Across this session"
            ),
            new(
                "AVERAGE TEAM DPS",
                averageDps > 0 ? $"{averageDps:N0}" : "—",
                sessionEncounters.Count > 0
                    ? $"{sessionEncounters.Count:N0} encounter average"
                    : "No session encounters"
            ),
            new(
                "BEST TEAM DPS",
                bestDps is null ? "—" : $"{bestDps.TeamDps:N0}",
                bestDps?.DisplayTitle ?? "No session encounters"
            ),
            new(
                "FASTEST KILL",
                fastest is null
                    ? "—"
                    : $"{fastest.DurationSeconds:F1} sec",
                fastest?.DisplayTitle ?? "No confirmed kills"
            ),
            new(
                "MOST USED PAL",
                mostUsedPal?.Name ?? "—",
                mostUsedPal is null
                    ? "No Pal contributors"
                    : $"{mostUsedPal.Appearances:N0} appearance" +
                      (mostUsedPal.Appearances == 1 ? string.Empty : "s")
            )
        };
    }

    private List<HistoryStatCard> BuildAllTimeStats()
    {
        EncounterSnapshot? teamDpsRecord = _encounters
            .Where(encounter => encounter.TeamDps > 0)
            .OrderByDescending(encounter => encounter.TeamDps)
            .FirstOrDefault();
        EncounterSnapshot? playerDpsRecord = _encounters
            .Where(encounter => encounter.PlayerDps > 0)
            .OrderByDescending(encounter => encounter.PlayerDps)
            .FirstOrDefault();
        EncounterSnapshot? palDpsRecord = _encounters
            .Where(encounter => encounter.PalDps > 0)
            .OrderByDescending(encounter => encounter.PalDps)
            .FirstOrDefault();
        EncounterSnapshot? damageRecord = _encounters
            .Where(encounter => encounter.TotalDamage > 0)
            .OrderByDescending(encounter => encounter.TotalDamage)
            .FirstOrDefault();
        EncounterSnapshot? fastestKill = GetDefeatedEncounters(_encounters)
            .OrderBy(encounter => encounter.DurationSeconds)
            .FirstOrDefault();

        return new List<HistoryStatCard>
        {
            new(
                "TEAM DPS",
                teamDpsRecord is null
                    ? "—"
                    : $"{teamDpsRecord.TeamDps:N0}",
                teamDpsRecord?.DisplayTitle ?? "No saved data"
            ),
            new(
                "PLAYER DPS",
                playerDpsRecord is null
                    ? "—"
                    : $"{playerDpsRecord.PlayerDps:N0}",
                playerDpsRecord?.DisplayTitle ?? "No player damage"
            ),
            new(
                "PAL DPS",
                palDpsRecord is null
                    ? "—"
                    : $"{palDpsRecord.PalDps:N0}",
                palDpsRecord?.DisplayTitle ?? "No Pal damage"
            ),
            new(
                "TOTAL DAMAGE",
                damageRecord is null
                    ? "—"
                    : damageRecord.TotalDamage.ToString("N0"),
                damageRecord?.DisplayTitle ?? "No saved data"
            ),
            new(
                "FASTEST KILL",
                fastestKill is null
                    ? "—"
                    : $"{fastestKill.DurationSeconds:F1} sec",
                fastestKill?.DisplayTitle ?? "No confirmed kills"
            ),
            new(
                "SAVED HISTORY",
                _encounters.Count.ToString("N0"),
                $"{_encounters.Count(encounter => encounter.IsPinned):N0} pinned"
            )
        };
    }

    private static IEnumerable<EncounterSnapshot> GetDefeatedEncounters(
        IEnumerable<EncounterSnapshot> encounters)
    {
        return encounters.Where(encounter =>
            encounter.TargetConfirmedDead ||
            encounter.EndReason.Equals(
                EncounterEndReasons.TargetDefeated,
                StringComparison.OrdinalIgnoreCase));
    }

    private void CompareButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_encounters.Count < 2)
        {
            return;
        }

        string? selectedId =
            (EncounterList.SelectedItem as EncounterSnapshot)?.Id;

        EncounterComparisonWindow comparisonWindow = new(
            _encounters,
            selectedId,
            _currentTheme
        )
        {
            Owner = this
        };

        comparisonWindow.ShowDialog();
    }

    private void ExportSelectedButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (EncounterList.SelectedItem is not EncounterSnapshot selected)
        {
            return;
        }

        ExportEncounters(
            new List<EncounterSnapshot> { selected },
            exportSingleObject: true
        );
    }

    private void ExportAllButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_encounters.Count == 0)
        {
            return;
        }

        ExportEncounters(
            _encounters
                .OrderByDescending(encounter => encounter.EndedAtUtc)
                .ToList(),
            exportSingleObject: false
        );
    }

    private void ExportEncounters(
        IReadOnlyList<EncounterSnapshot> encounters,
        bool exportSingleObject)
    {
        string baseName = exportSingleObject
            ? $"RMCT_{MakeSafeFileName(encounters[0].DisplayTitle)}_" +
              encounters[0].EndedAtUtc.ToLocalTime().ToString(
                  "yyyyMMdd_HHmmss",
                  CultureInfo.InvariantCulture)
            : $"RMCT_Encounter_History_{DateTime.Now:yyyyMMdd_HHmmss}";

        SaveFileDialog dialog = new()
        {
            Title = exportSingleObject
                ? "Export Encounter"
                : "Export Encounter History",
            Filter = "CSV file (*.csv)|*.csv|JSON file (*.json)|*.json",
            FilterIndex = 1,
            AddExtension = true,
            DefaultExt = "csv",
            FileName = baseName,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            bool exportJson = dialog.FilterIndex == 2;
            string expectedExtension = exportJson ? ".json" : ".csv";
            string outputPath = Path.GetExtension(dialog.FileName).Equals(
                expectedExtension,
                StringComparison.OrdinalIgnoreCase)
                ? dialog.FileName
                : Path.ChangeExtension(dialog.FileName, expectedExtension);

            string content = exportJson
                ? BuildJsonExport(encounters, exportSingleObject)
                : BuildCsvExport(encounters);

            File.WriteAllText(
                outputPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            );

            MessageBox.Show(
                this,
                $"Exported {encounters.Count:N0} encounter" +
                (encounters.Count == 1 ? string.Empty : "s") +
                $" to:\n{outputPath}",
                "Encounter Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "The encounter export could not be written.\n\n" +
                exception.Message,
                "Encounter Export Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private static string BuildJsonExport(
        IReadOnlyList<EncounterSnapshot> encounters,
        bool exportSingleObject)
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true
        };

        if (exportSingleObject)
        {
            return JsonSerializer.Serialize(
                EncounterExportRecord.FromSnapshot(encounters[0]),
                options
            );
        }

        List<EncounterExportRecord> records = encounters
            .Select(EncounterExportRecord.FromSnapshot)
            .ToList();

        return JsonSerializer.Serialize(records, options);
    }

    private static string BuildCsvExport(
        IReadOnlyList<EncounterSnapshot> encounters)
    {
        StringBuilder builder = new();
        builder.AppendLine(string.Join(",", new[]
        {
            "Id",
            "CustomName",
            "TargetName",
            "StartedAtLocal",
            "EndedAtLocal",
            "DurationSeconds",
            "EndReason",
            "TargetConfirmedDead",
            "TotalDamage",
            "TeamDps",
            "PlayerDamage",
            "PlayerDps",
            "PalDamage",
            "PalDps",
            "IsPinned",
            "Notes",
            "Contributors",
            "DamageSources",
            "PalSkills"
        }));

        foreach (EncounterSnapshot encounter in encounters)
        {
            string contributors = string.Join(
                "; ",
                (encounter.Combatants ??
                 new List<EncounterCombatantSnapshot>())
                    .Where(combatant => combatant.Damage > 0)
                    .OrderByDescending(combatant => combatant.Damage)
                    .Select(combatant =>
                        $"{combatant.DisplayName}|{combatant.SourceType}|" +
                        $"{combatant.Damage}|{combatant.Dps:F3}")
            );
            string damageSources = string.Join(
                "; ",
                (encounter.DamageSources ??
                 new List<EncounterDamageSourceSnapshot>())
                    .Where(source => source.Damage > 0)
                    .OrderByDescending(source => source.Damage)
                    .Select(source =>
                        $"{source.SourceName}|{source.SourceLabel}|" +
                        $"{source.Damage}|{source.HitCount}")
            );
            string palSkills = string.Join(
                "; ",
                (encounter.PalSkills ??
                 new List<EncounterPalSkillSnapshot>())
                    .Where(skill => skill.Damage > 0)
                    .OrderByDescending(skill => skill.Damage)
                    .Select(skill =>
                        $"{skill.PalName}|{skill.SkillName}|" +
                        $"{skill.Damage}|{skill.HitCount}|" +
                        $"{skill.CastCount}|{skill.AverageDamagePerCast:F3}")
            );

            string[] values =
            {
                encounter.Id,
                encounter.CustomName,
                encounter.TargetName,
                encounter.StartedAtUtc.ToLocalTime().ToString("O"),
                encounter.EndedAtUtc.ToLocalTime().ToString("O"),
                encounter.DurationSeconds.ToString(
                    "F3",
                    CultureInfo.InvariantCulture),
                encounter.EndReason,
                encounter.TargetConfirmedDead.ToString(),
                encounter.TotalDamage.ToString(CultureInfo.InvariantCulture),
                encounter.TeamDps.ToString(
                    "F3",
                    CultureInfo.InvariantCulture),
                encounter.PlayerDamage.ToString(CultureInfo.InvariantCulture),
                encounter.PlayerDps.ToString(
                    "F3",
                    CultureInfo.InvariantCulture),
                encounter.PalDamage.ToString(CultureInfo.InvariantCulture),
                encounter.PalDps.ToString(
                    "F3",
                    CultureInfo.InvariantCulture),
                encounter.IsPinned.ToString(),
                encounter.Notes,
                contributors,
                damageSources,
                palSkills
            };

            builder.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }

        return builder.ToString();
    }

    private static string EscapeCsv(string? value)
    {
        string text = value ?? string.Empty;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string MakeSafeFileName(string value)
    {
        string result = value.Trim();

        foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalidCharacter, '_');
        }

        result = result.Replace(' ', '_');

        if (result.Length > 60)
        {
            result = result[..60];
        }

        return string.IsNullOrWhiteSpace(result)
            ? "Encounter"
            : result;
    }

    private void PinEncounterButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (EncounterList.SelectedItem is not EncounterSnapshot selected)
        {
            return;
        }

        string selectedId = selected.Id;
        selected.IsPinned = !selected.IsPinned;
        _historyChanged();
        ApplyFilter(selectedId, selectNewest: false);
    }

    private void EditEncounterButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (EncounterList.SelectedItem is not EncounterSnapshot selected)
        {
            return;
        }

        _isEditingMetadata = true;
        CustomNameTextBox.Text = selected.CustomName;
        NotesTextBox.Text = selected.Notes;
        MetadataEditorBorder.Visibility = Visibility.Visible;
        CustomNameTextBox.Focus();
        CustomNameTextBox.SelectAll();
    }

    private void SaveMetadataButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (EncounterList.SelectedItem is not EncounterSnapshot selected)
        {
            CancelMetadataEdit();
            return;
        }

        string selectedId = selected.Id;
        selected.CustomName = EncounterHistoryStore.SanitizeCustomName(
            CustomNameTextBox.Text
        );
        selected.Notes = EncounterHistoryStore.SanitizeNotes(
            NotesTextBox.Text
        );

        _historyChanged();
        CancelMetadataEdit();
        ApplyFilter(selectedId, selectNewest: false);
    }

    private void CancelMetadataButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CancelMetadataEdit();
    }

    private void CancelMetadataEdit()
    {
        _isEditingMetadata = false;
        MetadataEditorBorder.Visibility = Visibility.Collapsed;
        CustomNameTextBox.Text = string.Empty;
        NotesTextBox.Text = string.Empty;
    }

    private void CopySummaryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (EncounterList.SelectedItem is not EncounterSnapshot selected)
        {
            return;
        }

        try
        {
            Clipboard.SetText(BuildClipboardSummary(selected));
            CopySummaryButton.Content = "COPIED";
        }
        catch
        {
            MessageBox.Show(
                this,
                "The encounter summary could not be copied to the clipboard.",
                "Copy Encounter Summary",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private static string BuildClipboardSummary(EncounterSnapshot encounter)
    {
        StringBuilder builder = new();
        builder.AppendLine(encounter.DisplayTitle);

        if (encounter.IsRenamed)
        {
            builder.AppendLine($"Target: {encounter.TargetName}");
        }

        builder.AppendLine($"Date: {encounter.EncounterDateText}");
        builder.AppendLine($"End reason: {encounter.EndReason}");
        builder.AppendLine($"Duration: {encounter.DurationText}");
        builder.AppendLine($"Team DPS: {encounter.TeamDps:N0}");
        builder.AppendLine($"Total damage: {encounter.TotalDamage:N0}");

        if (!string.IsNullOrWhiteSpace(encounter.Notes))
        {
            builder.AppendLine();
            builder.AppendLine("Notes:");
            builder.AppendLine(encounter.Notes);
        }

        List<EncounterCombatantSnapshot> combatants =
            encounter.Combatants?
                .Where(combatant => combatant.Damage > 0)
                .OrderByDescending(combatant => combatant.Damage)
                .ThenBy(combatant => combatant.FirstSeenOrder)
                .ToList() ?? new List<EncounterCombatantSnapshot>();

        if (combatants.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Contributors:");

            foreach (EncounterCombatantSnapshot combatant in combatants)
            {
                builder.AppendLine(
                    $"- {combatant.DisplayName}: " +
                    $"{combatant.Damage:N0} damage, " +
                    $"{combatant.Dps:N0} DPS"
                );
            }
        }

        List<EncounterDamageSourceSnapshot> sources =
            encounter.DamageSources?
                .Where(source => source.Damage > 0)
                .OrderByDescending(source => source.Damage)
                .ThenBy(source => source.FirstSeenOrder)
                .ToList() ?? new List<EncounterDamageSourceSnapshot>();

        if (sources.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Exact damage sources:");

            foreach (EncounterDamageSourceSnapshot source in sources)
            {
                builder.AppendLine(
                    $"- {source.SourceName} / {source.SourceLabel}: " +
                    $"{source.Damage:N0}"
                );
            }
        }

        List<EncounterPalSkillSnapshot> palSkills =
            encounter.PalSkills?
                .Where(skill => skill.Damage > 0)
                .OrderByDescending(skill => skill.Damage)
                .ThenBy(skill => skill.FirstSeenOrder)
                .ToList() ?? new List<EncounterPalSkillSnapshot>();

        if (palSkills.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Pal skill attribution:");

            foreach (EncounterPalSkillSnapshot skill in palSkills)
            {
                string casts = skill.CastCount > 0
                    ? $"{skill.CastCount:N0} casts, " +
                      $"{skill.AverageDamagePerCast:N0} avg/cast"
                    : "cast count unavailable";

                builder.AppendLine(
                    $"- {skill.PalName} / {skill.SkillName}: " +
                    $"{skill.Damage:N0} damage, " +
                    $"{skill.HitCount:N0} hits, {casts}"
                );
            }
        }

        return builder.ToString().TrimEnd();
    }

    private void PreviousEncounterButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SelectEncounter(EncounterList.SelectedIndex - 1);
    }

    private void NextEncounterButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        SelectEncounter(EncounterList.SelectedIndex + 1);
    }

    private void SelectEncounter(int index)
    {
        if (index < 0 || index >= _filteredEncounters.Count)
        {
            return;
        }

        EncounterList.SelectedIndex = index;
        EncounterList.ScrollIntoView(EncounterList.SelectedItem);
    }

    private void DeleteEncounterButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (EncounterList.SelectedItem is not EncounterSnapshot selected)
        {
            return;
        }

        string pinWarning = selected.IsPinned
            ? " This encounter is pinned."
            : string.Empty;

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Delete the saved encounter '{selected.DisplayTitle}'?" +
            pinWarning,
            "Delete Encounter",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        int selectedIndex = EncounterList.SelectedIndex;
        _encounters.Remove(selected);
        _historyChanged();

        ApplyFilter(preferredEncounterId: null, selectNewest: false);

        if (_filteredEncounters.Count > 0)
        {
            SelectEncounter(Math.Min(
                selectedIndex,
                _filteredEncounters.Count - 1
            ));
        }
    }

    private void ClearHistoryButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_encounters.Count == 0)
        {
            return;
        }

        int pinnedCount = _encounters.Count(encounter => encounter.IsPinned);
        string pinnedWarning = pinnedCount > 0
            ? $" This also deletes {pinnedCount} pinned encounter" +
              (pinnedCount == 1 ? "." : "s.")
            : string.Empty;

        MessageBoxResult result = MessageBox.Show(
            this,
            "Delete all saved encounters? This cannot be undone." +
            pinnedWarning,
            "Clear Encounter History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning
        );

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _encounters.Clear();
        _historyChanged();
        RefreshEncounters(selectNewest: false);
    }

    private void Window_KeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.F &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (_isEditingMetadata)
        {
            if (e.Key == Key.Escape)
            {
                CancelMetadataEdit();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.C &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CompareButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.E &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ExportSelectedButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F2)
        {
            EditEncounterButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.P &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            PinEncounterButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox)
        {
            return;
        }

        if (e.Key == Key.Left)
        {
            SelectEncounter(EncounterList.SelectedIndex - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            SelectEncounter(EncounterList.SelectedIndex + 1);
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
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
}

internal sealed class HistoryStatCard
{
    public HistoryStatCard(string label, string value, string detail)
    {
        Label = label;
        Value = value;
        Detail = detail;
    }

    public string Label { get; }
    public string Value { get; }
    public string Detail { get; }
}

internal sealed class EncounterExportRecord
{
    public string Id { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string CustomName { get; init; } = string.Empty;
    public string TargetActorId { get; init; } = string.Empty;
    public string TargetName { get; init; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset EndedAtUtc { get; init; }
    public double DurationSeconds { get; init; }
    public string EndReason { get; init; } = string.Empty;
    public bool TargetConfirmedDead { get; init; }
    public bool IsPinned { get; init; }
    public string Notes { get; init; } = string.Empty;
    public long TotalDamage { get; init; }
    public double TeamDps { get; init; }
    public long PlayerDamage { get; init; }
    public double PlayerDps { get; init; }
    public long PalDamage { get; init; }
    public double PalDps { get; init; }
    public List<EncounterCombatantSnapshot> Combatants { get; init; } = new();
    public List<EncounterDamageSourceSnapshot> DamageSources { get; init; } = new();
    public List<EncounterPalSkillSnapshot> PalSkills { get; init; } = new();

    public static EncounterExportRecord FromSnapshot(
        EncounterSnapshot encounter)
    {
        return new EncounterExportRecord
        {
            Id = encounter.Id,
            SessionId = encounter.SessionId,
            CustomName = encounter.CustomName,
            TargetActorId = encounter.TargetActorId,
            TargetName = encounter.TargetName,
            StartedAtUtc = encounter.StartedAtUtc,
            EndedAtUtc = encounter.EndedAtUtc,
            DurationSeconds = encounter.DurationSeconds,
            EndReason = encounter.EndReason,
            TargetConfirmedDead = encounter.TargetConfirmedDead,
            IsPinned = encounter.IsPinned,
            Notes = encounter.Notes,
            TotalDamage = encounter.TotalDamage,
            TeamDps = encounter.TeamDps,
            PlayerDamage = encounter.PlayerDamage,
            PlayerDps = encounter.PlayerDps,
            PalDamage = encounter.PalDamage,
            PalDps = encounter.PalDps,
            Combatants = encounter.Combatants?.ToList() ?? new(),
            DamageSources = encounter.DamageSources?.ToList() ?? new(),
            PalSkills = encounter.PalSkills?.ToList() ?? new()
        };
    }
}

internal sealed class HistoryContributorRow
{
    private HistoryContributorRow(
        string displayName,
        string damageText,
        string dpsText,
        string percentText,
        double rowHeight,
        double nameFontSize,
        FontWeight nameFontWeight,
        Thickness nameMargin)
    {
        DisplayName = displayName;
        DamageText = damageText;
        DpsText = dpsText;
        PercentText = percentText;
        RowHeight = rowHeight;
        NameFontSize = nameFontSize;
        NameFontWeight = nameFontWeight;
        NameMargin = nameMargin;
    }

    public static HistoryContributorRow CreateGroup(
        string displayName,
        long damage,
        double dps,
        double percentage)
    {
        return new HistoryContributorRow(
            displayName,
            damage.ToString("N0"),
            $"{dps:N0} DPS",
            $"{percentage:F0}%",
            28,
            10.5,
            FontWeights.Bold,
            new Thickness(0)
        );
    }

    public static HistoryContributorRow CreateCombatant(
        string displayName,
        long damage,
        double dps,
        double percentage)
    {
        return new HistoryContributorRow(
            displayName,
            damage.ToString("N0"),
            $"{dps:N0} DPS",
            $"{percentage:F0}%",
            23,
            9.5,
            FontWeights.SemiBold,
            new Thickness(12, 0, 0, 0)
        );
    }

    public static HistoryContributorRow CreatePlaceholder(
        string displayName)
    {
        return new HistoryContributorRow(
            displayName,
            string.Empty,
            string.Empty,
            string.Empty,
            24,
            9.5,
            FontWeights.Normal,
            new Thickness(0)
        );
    }

    public string DisplayName { get; }
    public string DamageText { get; }
    public string DpsText { get; }
    public string PercentText { get; }
    public double RowHeight { get; }
    public double NameFontSize { get; }
    public FontWeight NameFontWeight { get; }
    public Thickness NameMargin { get; }
}

internal sealed class HistoryPalSkillRow
{
    private HistoryPalSkillRow(
        string displayName,
        string damageText,
        string hitCastText,
        string averageText,
        double rowHeight,
        double nameFontSize,
        FontWeight nameFontWeight,
        Thickness nameMargin)
    {
        DisplayName = displayName;
        DamageText = damageText;
        HitCastText = hitCastText;
        AverageText = averageText;
        RowHeight = rowHeight;
        NameFontSize = nameFontSize;
        NameFontWeight = nameFontWeight;
        NameMargin = nameMargin;
    }

    public static HistoryPalSkillRow CreateGroup(
        string displayName,
        long damage)
    {
        return new HistoryPalSkillRow(
            displayName,
            damage.ToString("N0"),
            string.Empty,
            string.Empty,
            27,
            10.5,
            FontWeights.Bold,
            new Thickness(0)
        );
    }

    public static HistoryPalSkillRow CreateSkill(
        string displayName,
        long damage,
        int hitCount,
        int castCount,
        double averagePerCast)
    {
        string casts = castCount > 0
            ? $"{castCount:N0} casts"
            : "casts —";
        string average = castCount > 0
            ? $"{averagePerCast:N0}/cast"
            : "—/cast";

        return new HistoryPalSkillRow(
            displayName,
            damage.ToString("N0"),
            $"{hitCount:N0} hits · {casts}",
            average,
            23,
            9.5,
            FontWeights.SemiBold,
            new Thickness(12, 0, 0, 0)
        );
    }

    public static HistoryPalSkillRow CreatePlaceholder(
        string displayName)
    {
        return new HistoryPalSkillRow(
            displayName,
            string.Empty,
            string.Empty,
            string.Empty,
            24,
            9.5,
            FontWeights.Normal,
            new Thickness(0)
        );
    }

    public string DisplayName { get; }
    public string DamageText { get; }
    public string HitCastText { get; }
    public string AverageText { get; }
    public double RowHeight { get; }
    public double NameFontSize { get; }
    public FontWeight NameFontWeight { get; }
    public Thickness NameMargin { get; }
}

internal sealed class HistoryDamageSourceRow
{
    private HistoryDamageSourceRow(
        string displayName,
        string damageText,
        string percentText,
        double rowHeight,
        double nameFontSize,
        FontWeight nameFontWeight,
        Thickness nameMargin)
    {
        DisplayName = displayName;
        DamageText = damageText;
        PercentText = percentText;
        RowHeight = rowHeight;
        NameFontSize = nameFontSize;
        NameFontWeight = nameFontWeight;
        NameMargin = nameMargin;
    }

    public static HistoryDamageSourceRow CreateGroup(
        string displayName,
        long damage)
    {
        return new HistoryDamageSourceRow(
            displayName,
            damage.ToString("N0"),
            string.Empty,
            27,
            10.5,
            FontWeights.Bold,
            new Thickness(0)
        );
    }

    public static HistoryDamageSourceRow CreateSource(
        string displayName,
        long damage,
        double percentage)
    {
        return new HistoryDamageSourceRow(
            displayName,
            damage.ToString("N0"),
            $"{percentage:F0}%",
            22,
            9.5,
            FontWeights.SemiBold,
            new Thickness(12, 0, 0, 0)
        );
    }

    public static HistoryDamageSourceRow CreatePlaceholder(
        string displayName)
    {
        return new HistoryDamageSourceRow(
            displayName,
            string.Empty,
            string.Empty,
            24,
            9.5,
            FontWeights.Normal,
            new Thickness(0)
        );
    }

    public string DisplayName { get; }
    public string DamageText { get; }
    public string PercentText { get; }
    public double RowHeight { get; }
    public double NameFontSize { get; }
    public FontWeight NameFontWeight { get; }
    public Thickness NameMargin { get; }
}
