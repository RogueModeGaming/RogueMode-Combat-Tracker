using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RogueModeDpsMeter;

public partial class EncounterComparisonWindow : Window
{
    private readonly List<ComparisonEncounterOption> _options;
    private bool _isInitializing = true;

    public EncounterComparisonWindow(
        IEnumerable<EncounterSnapshot> encounters,
        string? preferredEncounterId,
        string currentTheme)
    {
        _options = encounters
            .OrderByDescending(encounter => encounter.EndedAtUtc)
            .Select(encounter => new ComparisonEncounterOption(encounter))
            .ToList();

        InitializeComponent();
        ApplyTheme(currentTheme);

        AEncounterComboBox.ItemsSource = _options;
        BEncounterComboBox.ItemsSource = _options;

        ComparisonEncounterOption encounterA =
            _options.FirstOrDefault(option =>
                option.Snapshot.Id.Equals(
                    preferredEncounterId,
                    StringComparison.Ordinal)) ??
            _options[0];

        ComparisonEncounterOption encounterB =
            FindRecommendedComparison(encounterA) ??
            _options.First(option =>
                !option.Snapshot.Id.Equals(
                    encounterA.Snapshot.Id,
                    StringComparison.Ordinal));

        AEncounterComboBox.SelectedItem = encounterA;
        BEncounterComboBox.SelectedItem = encounterB;

        _isInitializing = false;
        RenderComparison();
    }

    private ComparisonEncounterOption? FindRecommendedComparison(
        ComparisonEncounterOption encounterA)
    {
        return _options.FirstOrDefault(option =>
                   !option.Snapshot.Id.Equals(
                       encounterA.Snapshot.Id,
                       StringComparison.Ordinal) &&
                   option.Snapshot.TargetName.Equals(
                       encounterA.Snapshot.TargetName,
                       StringComparison.OrdinalIgnoreCase)) ??
               _options.FirstOrDefault(option =>
                   !option.Snapshot.Id.Equals(
                       encounterA.Snapshot.Id,
                       StringComparison.Ordinal));
    }

    private void ApplyTheme(string themeName)
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
                    UriKind.Relative)
            };

            Resources.MergedDictionaries[0] = themeDictionary;
        }
        catch
        {
            // Retain the default RM theme if another skin is unavailable.
        }
    }

    private void EncounterComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            RenderComparison();
        }
    }

    private void RenderComparison()
    {
        if (AEncounterComboBox.SelectedItem is not ComparisonEncounterOption optionA ||
            BEncounterComboBox.SelectedItem is not ComparisonEncounterOption optionB)
        {
            CopyComparisonButton.IsEnabled = false;
            return;
        }

        EncounterSnapshot encounterA = optionA.Snapshot;
        EncounterSnapshot encounterB = optionB.Snapshot;

        AEncounterDetailText.Text = BuildEncounterDetail(encounterA);
        BEncounterDetailText.Text = BuildEncounterDetail(encounterB);

        bool sameEncounter = encounterA.Id.Equals(
            encounterB.Id,
            StringComparison.Ordinal);
        bool sameTarget = encounterA.TargetName.Equals(
            encounterB.TargetName,
            StringComparison.OrdinalIgnoreCase);

        if (sameEncounter)
        {
            ComparisonWarningBorder.Visibility = Visibility.Visible;
            ComparisonWarningText.Text =
                "Encounter A and Encounter B are the same saved fight. " +
                "Choose a different encounter to produce a meaningful comparison.";
        }
        else if (!sameTarget)
        {
            ComparisonWarningBorder.Visibility = Visibility.Visible;
            ComparisonWarningText.Text =
                $"Different targets selected: {encounterA.TargetName} versus " +
                $"{encounterB.TargetName}. Damage and duration may not be directly comparable.";
        }
        else
        {
            ComparisonWarningBorder.Visibility = Visibility.Collapsed;
            ComparisonWarningText.Text = string.Empty;
        }

        SummaryRowsControl.ItemsSource = BuildSummaryRows(
            encounterA,
            encounterB);
        ContributorComparisonRowsControl.ItemsSource =
            BuildContributorRows(encounterA, encounterB);
        DamageSourceComparisonRowsControl.ItemsSource =
            BuildDamageSourceRows(encounterA, encounterB);
        PalSkillComparisonRowsControl.ItemsSource =
            BuildPalSkillRows(encounterA, encounterB);

        CopyComparisonButton.IsEnabled = !sameEncounter;
        CopyComparisonButton.Content = "COPY COMPARISON";
    }

    private static string BuildEncounterDetail(EncounterSnapshot encounter)
    {
        return $"{encounter.TargetName} · {encounter.EncounterDateText}\n" +
               $"{encounter.DurationSeconds:F1} sec · {encounter.TeamDps:N0} DPS · " +
               $"{encounter.EndReason}";
    }

    private static List<ComparisonRow> BuildSummaryRows(
        EncounterSnapshot encounterA,
        EncounterSnapshot encounterB)
    {
        int contributorsA = encounterA.Combatants?
            .Count(combatant => combatant.Damage > 0) ?? 0;
        int contributorsB = encounterB.Combatants?
            .Count(combatant => combatant.Damage > 0) ?? 0;

        return new List<ComparisonRow>
        {
            ComparisonRow.FromNumber(
                "Team DPS",
                encounterA.TeamDps,
                encounterB.TeamDps,
                value => $"{value:N0} DPS",
                value => $"{value:+#,##0;-#,##0;0} DPS"),
            ComparisonRow.FromNumber(
                "Player DPS",
                encounterA.PlayerDps,
                encounterB.PlayerDps,
                value => $"{value:N0} DPS",
                value => $"{value:+#,##0;-#,##0;0} DPS"),
            ComparisonRow.FromNumber(
                "Pal DPS",
                encounterA.PalDps,
                encounterB.PalDps,
                value => $"{value:N0} DPS",
                value => $"{value:+#,##0;-#,##0;0} DPS"),
            ComparisonRow.FromNumber(
                "Total Damage",
                encounterA.TotalDamage,
                encounterB.TotalDamage,
                value => $"{value:N0}",
                value => $"{value:+#,##0;-#,##0;0}"),
            ComparisonRow.FromNumber(
                "Duration",
                encounterA.DurationSeconds,
                encounterB.DurationSeconds,
                value => $"{value:F1} sec",
                value => $"{value:+0.0;-0.0;0.0} sec"),
            ComparisonRow.FromNumber(
                "Contributors",
                contributorsA,
                contributorsB,
                value => $"{value:N0}",
                value => $"{value:+0;-0;0}")
        };
    }

    private static List<ComparisonRow> BuildContributorRows(
        EncounterSnapshot encounterA,
        EncounterSnapshot encounterB)
    {
        Dictionary<string, ComparisonAggregate> a =
            AggregateCombatants(encounterA);
        Dictionary<string, ComparisonAggregate> b =
            AggregateCombatants(encounterB);

        List<string> keys = a.Keys
            .Union(b.Keys, StringComparer.Ordinal)
            .OrderByDescending(key =>
                Math.Max(
                    a.TryGetValue(key, out ComparisonAggregate? left)
                        ? left.Damage
                        : 0,
                    b.TryGetValue(key, out ComparisonAggregate? right)
                        ? right.Damage
                        : 0))
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
        {
            return new List<ComparisonRow>
            {
                new("No contributor data", "—", "—", "—")
            };
        }

        return keys.Select(key =>
        {
            a.TryGetValue(key, out ComparisonAggregate? left);
            b.TryGetValue(key, out ComparisonAggregate? right);

            double damageA = left?.Damage ?? 0;
            double damageB = right?.Damage ?? 0;
            double dpsA = left?.Dps ?? 0;
            double dpsB = right?.Dps ?? 0;
            string label = left?.Label ?? right?.Label ?? "Unknown";

            return ComparisonRow.FromValues(
                label,
                $"{damageA:N0} / {dpsA:N0}",
                $"{damageB:N0} / {dpsB:N0}",
                damageA,
                damageB,
                value => $"{value:+#,##0;-#,##0;0}");
        }).ToList();
    }

    private static Dictionary<string, ComparisonAggregate> AggregateCombatants(
        EncounterSnapshot encounter)
    {
        return (encounter.Combatants ?? new List<EncounterCombatantSnapshot>())
            .Where(combatant => combatant.Damage > 0)
            .GroupBy(combatant => BuildCombatantKey(combatant), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new ComparisonAggregate
                {
                    Label = BuildCombatantLabel(group.First()),
                    Damage = group.Sum(combatant => (double)combatant.Damage),
                    Dps = group.Sum(combatant => combatant.Dps)
                },
                StringComparer.Ordinal);
    }

    private static string BuildCombatantKey(
        EncounterCombatantSnapshot combatant)
    {
        return string.Join(
            "|",
            combatant.SourceType ?? string.Empty,
            combatant.OwnerDisplayName ?? string.Empty,
            combatant.DisplayName ?? string.Empty);
    }

    private static string BuildCombatantLabel(
        EncounterCombatantSnapshot combatant)
    {
        if (string.Equals(
                combatant.SourceType,
                "RAID_PAL",
                StringComparison.OrdinalIgnoreCase))
        {
            return $"Raid Team · {combatant.DisplayName}";
        }

        if (string.Equals(
                combatant.SourceType,
                "PAL",
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(combatant.OwnerDisplayName))
        {
            return $"{combatant.OwnerDisplayName} · {combatant.DisplayName}";
        }

        return string.IsNullOrWhiteSpace(combatant.DisplayName)
            ? "Unknown Contributor"
            : combatant.DisplayName;
    }

    private static List<ComparisonRow> BuildPalSkillRows(
        EncounterSnapshot encounterA,
        EncounterSnapshot encounterB)
    {
        Dictionary<string, ComparisonAggregate> a =
            AggregatePalSkills(encounterA);
        Dictionary<string, ComparisonAggregate> b =
            AggregatePalSkills(encounterB);

        List<string> keys = a.Keys
            .Union(b.Keys, StringComparer.Ordinal)
            .OrderByDescending(key =>
                Math.Max(
                    a.TryGetValue(key, out ComparisonAggregate? left)
                        ? left.Damage
                        : 0,
                    b.TryGetValue(key, out ComparisonAggregate? right)
                        ? right.Damage
                        : 0))
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
        {
            return new List<ComparisonRow>
            {
                new("No Pal skill attribution data", "—", "—", "—")
            };
        }

        return keys.Select(key =>
        {
            a.TryGetValue(key, out ComparisonAggregate? left);
            b.TryGetValue(key, out ComparisonAggregate? right);

            double damageA = left?.Damage ?? 0;
            double damageB = right?.Damage ?? 0;
            int hitsA = left?.HitCount ?? 0;
            int hitsB = right?.HitCount ?? 0;
            int castsA = left?.CastCount ?? 0;
            int castsB = right?.CastCount ?? 0;
            double averageA = castsA > 0 ? damageA / castsA : 0;
            double averageB = castsB > 0 ? damageB / castsB : 0;
            string label = left?.Label ?? right?.Label ?? "Unknown Pal Skill";

            string valueA = castsA > 0
                ? $"{damageA:N0} · {hitsA:N0}H · {castsA:N0}C · {averageA:N0}/C"
                : $"{damageA:N0} · {hitsA:N0}H · —C";
            string valueB = castsB > 0
                ? $"{damageB:N0} · {hitsB:N0}H · {castsB:N0}C · {averageB:N0}/C"
                : $"{damageB:N0} · {hitsB:N0}H · —C";

            return ComparisonRow.FromValues(
                label,
                valueA,
                valueB,
                damageA,
                damageB,
                value => $"{value:+#,##0;-#,##0;0}");
        }).ToList();
    }

    private static Dictionary<string, ComparisonAggregate> AggregatePalSkills(
        EncounterSnapshot encounter)
    {
        return (encounter.PalSkills ?? new List<EncounterPalSkillSnapshot>())
            .Where(skill => skill.Damage > 0)
            .GroupBy(
                skill => string.Join(
                    "|",
                    skill.PalName ?? string.Empty,
                    skill.SkillName ?? string.Empty),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new ComparisonAggregate
                {
                    Label = $"{group.First().PalName} · {group.First().SkillName}",
                    Damage = group.Sum(skill => (double)skill.Damage),
                    HitCount = group.Sum(skill => skill.HitCount),
                    CastCount = group.Sum(skill => skill.CastCount)
                },
                StringComparer.Ordinal);
    }

    private static List<ComparisonRow> BuildDamageSourceRows(
        EncounterSnapshot encounterA,
        EncounterSnapshot encounterB)
    {
        Dictionary<string, ComparisonAggregate> a =
            AggregateDamageSources(encounterA);
        Dictionary<string, ComparisonAggregate> b =
            AggregateDamageSources(encounterB);

        List<string> keys = a.Keys
            .Union(b.Keys, StringComparer.Ordinal)
            .OrderByDescending(key =>
                Math.Max(
                    a.TryGetValue(key, out ComparisonAggregate? left)
                        ? left.Damage
                        : 0,
                    b.TryGetValue(key, out ComparisonAggregate? right)
                        ? right.Damage
                        : 0))
            .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
        {
            return new List<ComparisonRow>
            {
                new("No exact source data", "—", "—", "—")
            };
        }

        return keys.Select(key =>
        {
            a.TryGetValue(key, out ComparisonAggregate? left);
            b.TryGetValue(key, out ComparisonAggregate? right);

            double damageA = left?.Damage ?? 0;
            double damageB = right?.Damage ?? 0;
            int hitsA = left?.HitCount ?? 0;
            int hitsB = right?.HitCount ?? 0;
            string label = left?.Label ?? right?.Label ?? "Unknown Source";

            return ComparisonRow.FromValues(
                label,
                $"{damageA:N0} / {hitsA:N0}",
                $"{damageB:N0} / {hitsB:N0}",
                damageA,
                damageB,
                value => $"{value:+#,##0;-#,##0;0}");
        }).ToList();
    }

    private static Dictionary<string, ComparisonAggregate> AggregateDamageSources(
        EncounterSnapshot encounter)
    {
        return (encounter.DamageSources ?? new List<EncounterDamageSourceSnapshot>())
            .Where(source => source.Damage > 0)
            .GroupBy(source => BuildDamageSourceKey(source), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new ComparisonAggregate
                {
                    Label = BuildDamageSourceLabel(group.First()),
                    Damage = group.Sum(source => (double)source.Damage),
                    HitCount = group.Sum(source => source.HitCount)
                },
                StringComparer.Ordinal);
    }

    private static string BuildDamageSourceKey(
        EncounterDamageSourceSnapshot source)
    {
        return string.Join(
            "|",
            source.SourceName ?? string.Empty,
            source.SourceLabel ?? string.Empty);
    }

    private static string BuildDamageSourceLabel(
        EncounterDamageSourceSnapshot source)
    {
        bool hasSourceName = !string.IsNullOrWhiteSpace(source.SourceName) &&
            !source.SourceName.Equals(
                "unknown",
                StringComparison.OrdinalIgnoreCase);
        string sourceLabel = string.IsNullOrWhiteSpace(source.SourceLabel)
            ? "Unclassified"
            : source.SourceLabel;

        return hasSourceName
            ? $"{source.SourceName} · {sourceLabel}"
            : sourceLabel;
    }

    private void SwapButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        object? selectedA = AEncounterComboBox.SelectedItem;
        object? selectedB = BEncounterComboBox.SelectedItem;

        _isInitializing = true;
        AEncounterComboBox.SelectedItem = selectedB;
        BEncounterComboBox.SelectedItem = selectedA;
        _isInitializing = false;

        RenderComparison();
    }

    private void CopyComparisonButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (AEncounterComboBox.SelectedItem is not ComparisonEncounterOption optionA ||
            BEncounterComboBox.SelectedItem is not ComparisonEncounterOption optionB ||
            optionA.Snapshot.Id.Equals(
                optionB.Snapshot.Id,
                StringComparison.Ordinal))
        {
            return;
        }

        StringBuilder summary = new();
        summary.AppendLine("RogueMode Combat Tracker — Encounter Comparison");
        summary.AppendLine($"A: {optionA.Snapshot.DisplayTitle} — {BuildEncounterDetail(optionA.Snapshot).Replace("\n", " · ")}");
        summary.AppendLine($"B: {optionB.Snapshot.DisplayTitle} — {BuildEncounterDetail(optionB.Snapshot).Replace("\n", " · ")}");
        summary.AppendLine();

        foreach (ComparisonRow row in BuildSummaryRows(
                     optionA.Snapshot,
                     optionB.Snapshot))
        {
            summary.AppendLine(
                $"{row.Label}: A {row.AValue} | B {row.BValue} | {row.Difference}");
        }

        List<ComparisonRow> palSkillRows = BuildPalSkillRows(
            optionA.Snapshot,
            optionB.Snapshot);

        if (palSkillRows.Count > 0 &&
            !palSkillRows[0].Label.Equals(
                "No Pal skill attribution data",
                StringComparison.Ordinal))
        {
            summary.AppendLine();
            summary.AppendLine("Pal skill attribution:");

            foreach (ComparisonRow row in palSkillRows)
            {
                summary.AppendLine(
                    $"{row.Label}: A {row.AValue} | B {row.BValue} | {row.Difference}");
            }
        }

        Clipboard.SetText(summary.ToString().TrimEnd());
        CopyComparisonButton.Content = "COPIED";
    }

    private void Window_KeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CopyComparisonButton_Click(this, new RoutedEventArgs());
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

internal sealed class ComparisonEncounterOption
{
    public ComparisonEncounterOption(EncounterSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public EncounterSnapshot Snapshot { get; }
    public string Title => Snapshot.DisplayTitle;
    public string Context => Snapshot.IsRenamed
        ? $"{Snapshot.TargetName} · {Snapshot.EncounterTimeText}"
        : $"{Snapshot.EncounterTimeText} · {Snapshot.TeamDps:N0} DPS";
}

internal sealed class ComparisonAggregate
{
    public string Label { get; set; } = string.Empty;
    public double Damage { get; set; }
    public double Dps { get; set; }
    public int HitCount { get; set; }
    public int CastCount { get; set; }
}

internal sealed class ComparisonRow
{
    public ComparisonRow(
        string label,
        string aValue,
        string bValue,
        string difference)
    {
        Label = label;
        AValue = aValue;
        BValue = bValue;
        Difference = difference;
    }

    public string Label { get; }
    public string AValue { get; }
    public string BValue { get; }
    public string Difference { get; }

    public static ComparisonRow FromValues(
        string label,
        string aValue,
        string bValue,
        double numericA,
        double numericB,
        Func<double, string> differenceFormatter)
    {
        return new ComparisonRow(
            label,
            aValue,
            bValue,
            BuildDifference(numericA, numericB, differenceFormatter));
    }

    public static ComparisonRow FromNumber(
        string label,
        double valueA,
        double valueB,
        Func<double, string> valueFormatter,
        Func<double, string> differenceFormatter)
    {
        return new ComparisonRow(
            label,
            valueFormatter(valueA),
            valueFormatter(valueB),
            BuildDifference(valueA, valueB, differenceFormatter));
    }

    private static string BuildDifference(
        double valueA,
        double valueB,
        Func<double, string> differenceFormatter)
    {
        double difference = valueB - valueA;
        string percentage;

        if (Math.Abs(valueA) < 0.0001)
        {
            percentage = Math.Abs(valueB) < 0.0001 ? "0.0%" : "new";
        }
        else
        {
            double percentChange = difference * 100.0 / Math.Abs(valueA);
            percentage = $"{percentChange:+0.0;-0.0;0.0}%";
        }

        return $"{differenceFormatter(difference)} ({percentage})";
    }
}
