using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal sealed class HighlightClip
{
    public string Id { get; set; } = "";
    public string Film { get; set; } = "";
    public string Title { get; set; } = "halo3";
    public string SourceHash { get; set; } = "";
    public string Map { get; set; } = "";
    public string Recorder { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string Medal { get; set; } = "";
    public int KillCount { get; set; }
    public int TotalTicks { get; set; }
    public int StartTick { get; set; }
    public int EndTick { get; set; }
    public bool ShortLeadIn { get; set; }
    public bool Selected { get; set; } = true;
    public List<FilmEvent> Kills { get; set; } = new();
    public FilmEvent? Award { get; set; }
    public string State { get; set; } = "queued";
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Error { get; set; } = "";
    public JsonElement? AudioDelivery { get; set; }
    public HighlightCaption Caption { get; set; } = new();
    public double DurationSeconds => (EndTick - StartTick) / 60.0;
}
internal sealed class HighlightOptions
{
    public int MinimumKills { get; set; } = 4;
    public bool Exact { get; set; }
    public string Selection { get; set; } = "all";
    public double BeforeSeconds { get; set; } = 3;
    public double AfterSeconds { get; set; } = 3;
    public void Validate()
    {
        if (MinimumKills is < 2 or > 10 || Selection is not ("all" or "first" or "first-per-film") ||
            !double.IsFinite(BeforeSeconds) || BeforeSeconds < 0 || BeforeSeconds > 30 ||
            !double.IsFinite(AfterSeconds) || AfterSeconds < 0 || AfterSeconds > 30)
            throw new System.IO.IOException("Choose 2–10 kills, a supported selection rule, and buffers from 0 to 30 seconds.");
    }
}
internal static class HighlightPlanner
{
    private static readonly string[] Names = { "", "", "Double Kill", "Triple Kill", "Overkill", "Killtacular", "Killtrocity", "Killimanjaro", "Killtastrophe", "Killpocalypse", "Killionaire" };
    public static List<HighlightClip> Plan(FilmIndex index, HighlightOptions options, List<string>? warnings = null)
    {
        options.Validate(); bool halo3 = index.Title == "halo3"; var clips = new List<HighlightClip>(); var chain = new List<FilmEvent>(); FilmEvent? award = null;
        void Finish()
        {
            if (award != null && chain.Count >= options.MinimumKills && (!options.Exact || chain.Count == options.MinimumKills))
            {
                int start = Math.Max(0, chain[0].Update - (int)Math.Round(options.BeforeSeconds * 60));
                int end = Math.Min(index.TotalTicks, chain[^1].Update + 1 + (int)Math.Round(options.AfterSeconds * 60));
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index.Hash + ":" + chain[0].SourceBit + ":" + award.SourceBit)))[..24];
                clips.Add(new() { Id = id, Film = index.Film, Title=index.Title, SourceHash = index.Hash, Map = index.Map, Recorder = chain[0].CauseName,
                    PlayerId = chain[0].CauseId, Medal = Names[Math.Min(10, chain.Count)], KillCount = chain.Count,
                    TotalTicks = index.TotalTicks, StartTick = start, EndTick = end, ShortLeadIn = start == 0 && chain[0].Update < options.BeforeSeconds * 60,
                    Kills = new(chain), Award = award });
            }
            chain.Clear(); award = null;
        }
        foreach (var group in index.Events.GroupBy(e => (e.Segment, e.Update)))
        {
            var first = group.First();
            // Newer titles carry the engine's awarded incident progression.
            // A kill without the next medal already ends a chain below; do not
            // impose Halo 3's four-second rule on another game's native award.
            if (chain.Count > 0 && (chain[^1].Segment != first.Segment || first.Clock < chain[^1].Clock || index.Title=="halo3"&&first.Clock - chain[^1].Clock > 240)) Finish();
            // Halo 3 stores a raw game-event ID here (weapon/incident-specific),
            // rather than the normalized kill ID used by later titles. Only
            // count recorder-caused events with a concrete victim slot.
            var kills = group.Where(e => e.Category == 0 && (halo3 || e.EventId == 0) && e.Recorder && !e.RecorderDied && e.CauseId.Length > 0 && (!halo3 || e.EffectSlot >= 0)).ToList();
            if (kills.Count > 0)
            {
                if (chain.Count > 0 && chain[0].CauseId != kills[0].CauseId) Finish();
                var medals = group.Where(e => e.Category == 1 && e.Recorder && e.EventId is >= 2 and <= 10).ToArray();
                var highest = medals.OrderByDescending(e => e.EventId).FirstOrDefault();
                if (highest == null) { Finish(); chain.AddRange(kills); }
                else
                {
                    // A new Double Kill can follow an old completed chain. Keep
                    // only the immediately preceding unpaired kill in that case.
                    if (highest.EventId == 2 && chain.Count > 1) Finish();
                    bool beyondMedalCap = highest.EventId == 10 && chain.Count >= 10 && award?.EventId == 10;
                    if (!beyondMedalCap && (chain.Count + kills.Count != highest.EventId || chain.Count == 0 && highest.EventId > kills.Count))
                    { warnings?.Add($"Incomplete {Names[highest.EventId]} evidence near playback tick {highest.Update}; skipped."); Finish(); }
                    else { chain.AddRange(kills); award = highest; }
                }
            }
            if (group.Any(e => e.Category == 0 && (halo3 || e.EventId is 0 or 1) && e.RecorderDied)) Finish();
        }
        Finish();
        return options.Selection == "first-per-film" ? clips.Take(1).ToList() : clips;
    }
}
