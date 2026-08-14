using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RogueModeDpsMeter;

internal static class EncounterEndReasons
{
    public const string TargetDefeated = "Target Defeated";
    public const string ManualStop = "Manual Stop";
    public const string InactivityTimeout = "Inactivity Timeout";
    public const string TargetChanged = "Target Changed";
}

internal static class EncounterHistoryStore
{
    public const int MaximumEncounterCount = 50;
    public const int MaximumCustomNameLength = 80;
    public const int MaximumNotesLength = 1000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string HistoryFilePath => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "RogueModeCombatTracker",
        "encounter-history.json"
    );

    public static List<EncounterSnapshot> Load()
    {
        try
        {
            if (!File.Exists(HistoryFilePath))
            {
                return new List<EncounterSnapshot>();
            }

            string json = File.ReadAllText(HistoryFilePath);
            List<EncounterSnapshot>? encounters =
                JsonSerializer.Deserialize<List<EncounterSnapshot>>(
                    json,
                    SerializerOptions
                );

            if (encounters is null)
            {
                return new List<EncounterSnapshot>();
            }

            foreach (EncounterSnapshot encounter in encounters)
            {
                if (string.IsNullOrWhiteSpace(encounter.Id))
                {
                    encounter.Id = Guid.NewGuid().ToString("N");
                }

                encounter.Combatants ??= new List<EncounterCombatantSnapshot>();
                encounter.DamageSources ??= new List<EncounterDamageSourceSnapshot>();
                encounter.PalSkills ??= new List<EncounterPalSkillSnapshot>();
                encounter.TargetName = string.IsNullOrWhiteSpace(encounter.TargetName)
                    ? "Unknown Target"
                    : encounter.TargetName.Trim();
                encounter.SessionId = (encounter.SessionId ?? string.Empty).Trim();
                encounter.CustomName = SanitizeCustomName(encounter.CustomName);
                encounter.Notes = SanitizeNotes(encounter.Notes);
            }

            return BuildRetainedList(encounters);
        }
        catch
        {
            // Corrupt or inaccessible history must never prevent the tracker
            // from starting. A later successful save replaces the bad file.
            return new List<EncounterSnapshot>();
        }
    }

    public static bool Save(IEnumerable<EncounterSnapshot> encounters)
    {
        try
        {
            List<EncounterSnapshot> retained = BuildRetainedList(encounters);
            string? directory = Path.GetDirectoryName(HistoryFilePath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = HistoryFilePath + ".tmp";
            string json = JsonSerializer.Serialize(
                retained,
                SerializerOptions
            );

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, HistoryFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void NormalizeInPlace(IList<EncounterSnapshot> encounters)
    {
        List<EncounterSnapshot> retained = BuildRetainedList(encounters);

        encounters.Clear();

        foreach (EncounterSnapshot encounter in retained)
        {
            encounters.Add(encounter);
        }
    }

    public static string SanitizeCustomName(string? value)
    {
        string result = (value ?? string.Empty).Trim();

        if (result.Length > MaximumCustomNameLength)
        {
            result = result[..MaximumCustomNameLength];
        }

        return result;
    }

    public static string SanitizeNotes(string? value)
    {
        string result = (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (result.Length > MaximumNotesLength)
        {
            result = result[..MaximumNotesLength];
        }

        return result;
    }

    private static List<EncounterSnapshot> BuildRetainedList(
        IEnumerable<EncounterSnapshot> encounters)
    {
        List<EncounterSnapshot> candidates = encounters
            .Where(encounter =>
                encounter is not null &&
                encounter.TotalDamage > 0 &&
                encounter.DurationSeconds > 0)
            .ToList();

        foreach (EncounterSnapshot encounter in candidates)
        {
            if (string.IsNullOrWhiteSpace(encounter.Id))
            {
                encounter.Id = Guid.NewGuid().ToString("N");
            }

            encounter.SessionId = (encounter.SessionId ?? string.Empty).Trim();
            encounter.CustomName = SanitizeCustomName(encounter.CustomName);
            encounter.Notes = SanitizeNotes(encounter.Notes);
        }

        List<EncounterSnapshot> ordered = candidates
            .GroupBy(encounter => encounter.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderByDescending(encounter => encounter.EndedAtUtc)
            .ToList();

        List<EncounterSnapshot> pinned = ordered
            .Where(encounter => encounter.IsPinned)
            .ToList();

        int availableUnpinnedSlots = Math.Max(
            0,
            MaximumEncounterCount - pinned.Count
        );

        HashSet<string> pinnedIds = pinned
            .Select(encounter => encounter.Id)
            .ToHashSet(StringComparer.Ordinal);

        List<EncounterSnapshot> retained = new(pinned);
        retained.AddRange(
            ordered
                .Where(encounter => !pinnedIds.Contains(encounter.Id))
                .Take(availableUnpinnedSlots)
        );

        // Pinned encounters are protected from automatic cleanup, but the
        // visible list remains chronological rather than jumping when pinned.
        return retained
            .OrderByDescending(encounter => encounter.EndedAtUtc)
            .ToList();
    }
}

public sealed class EncounterSnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetActorId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string TargetName { get; set; } = "Unknown Target";
    public string CustomName { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public string EndReason { get; set; } = EncounterEndReasons.ManualStop;
    public bool TargetConfirmedDead { get; set; }
    public long TotalDamage { get; set; }
    public long PlayerDamage { get; set; }
    public long PalDamage { get; set; }
    public double TeamDps { get; set; }
    public List<EncounterCombatantSnapshot>? Combatants { get; set; } = new();
    public List<EncounterDamageSourceSnapshot>? DamageSources { get; set; } = new();
    public List<EncounterPalSkillSnapshot>? PalSkills { get; set; } = new();

    [JsonIgnore]
    public bool IsRenamed => !string.IsNullOrWhiteSpace(CustomName);

    [JsonIgnore]
    public string DisplayTitle => IsRenamed ? CustomName : TargetName;

    [JsonIgnore]
    public bool IsRecord { get; set; }

    [JsonIgnore]
    public string RecordSummary { get; set; } = string.Empty;

    [JsonIgnore]
    public string PinGlyph => IsPinned ? "★" : string.Empty;

    [JsonIgnore]
    public string RecordGlyph => IsRecord ? "🏆" : string.Empty;

    [JsonIgnore]
    public string ListContextText => IsRenamed
        ? $"{TargetName} · {EncounterTimeText}"
        : EncounterTimeText;

    [JsonIgnore]
    public string EncounterTimeText =>
        EndedAtUtc.ToLocalTime().ToString("MMM d · h:mm tt");

    [JsonIgnore]
    public string EncounterDateText =>
        EndedAtUtc.ToLocalTime().ToString("MMMM d, yyyy · h:mm tt");

    [JsonIgnore]
    public string DurationText => $"{DurationSeconds:F1} sec";

    [JsonIgnore]
    public double PlayerDps => PlayerDamage / Math.Max(DurationSeconds, 0.01);

    [JsonIgnore]
    public double PalDps => PalDamage / Math.Max(DurationSeconds, 0.01);

    [JsonIgnore]
    public string TeamDpsText => $"{TeamDps:N0} DPS";

    [JsonIgnore]
    public string TotalDamageText => TotalDamage.ToString("N0");
}

public sealed class EncounterCombatantSnapshot
{
    public string ActorId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Unknown";
    public string? OwnerActorId { get; set; }
    public string? OwnerDisplayName { get; set; }
    public int FirstSeenOrder { get; set; }
    public long Damage { get; set; }
    public double Dps { get; set; }
}


public sealed class EncounterPalSkillSnapshot
{
    public string ActorId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string PalName { get; set; } = "Unknown Pal";
    public string SkillId { get; set; } = string.Empty;
    public string SkillName { get; set; } = "Unknown Skill";
    public int FirstSeenOrder { get; set; }
    public long Damage { get; set; }
    public int HitCount { get; set; }
    public int CastCount { get; set; }

    [JsonIgnore]
    public double AverageDamagePerHit =>
        HitCount > 0 ? Damage / (double)HitCount : 0;

    [JsonIgnore]
    public double AverageDamagePerCast =>
        CastCount > 0 ? Damage / (double)CastCount : 0;
}

public sealed class EncounterDamageSourceSnapshot
{
    public string ActorId { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = "Unclassified";
    public int FirstSeenOrder { get; set; }
    public long Damage { get; set; }
    public int HitCount { get; set; }
    public int WeakHitCount { get; set; }
    public int StrongHitCount { get; set; }
}
