using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.Engines.Script;
using GBX.NET.LZO;

internal sealed class Program
{
    private const string WaypointTimesKey = "Race_AuthorRaceWaypointTimes";
    private const int DefaultGpsThresholdMs = 100;

    private static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);

            Gbx.LZO = new Lzo();

            var manual = !string.IsNullOrWhiteSpace(opts.ManualPath)
                ? LoadManualOverrides(opts.ManualPath!)
                : new Dictionary<string, ManualEntry>(StringComparer.Ordinal);

            var replayIndex = !string.IsNullOrWhiteSpace(opts.ReplaysPath)
                ? BuildReplayIndex(opts.ReplaysPath!, opts.Recursive, opts.Progress, opts.ProgressIntervalSeconds)
                : new Dictionary<string, List<ReplayEntry>>(StringComparer.Ordinal);

            object outputObj;
            if (opts.Mode == RunMode.Single)
            {
                var report = ProcessMapFile(opts.MapPath, opts, manual, replayIndex);
                outputObj = report;
            }
            else
            {
                var mapFiles = EnumerateFiles(opts.MapPath, opts.Recursive).ToList();
                var totalMaps = mapFiles.Count;
                var progress = opts.Progress ? new ProgressReporter(TimeSpan.FromSeconds(opts.ProgressIntervalSeconds)) : null;
                int processed = 0;
                int errorCount = 0;

                var reports = new List<Report>();
                foreach (var file in mapFiles)
                {
                    var report = ProcessMapFile(file, opts, manual, replayIndex);
                    reports.Add(report);

                    processed++;
                    if (!string.IsNullOrWhiteSpace(report.Error))
                        errorCount++;

                    if (progress is not null && progress.TryGetStats(processed, out var mapStats))
                    {
                        var eta = ProgressReporter.GetEta(totalMaps - processed, mapStats.AvgRate);
                        Console.Error.WriteLine(
                            $"Map scan: {processed}/{totalMaps} files, errors={errorCount}, rate={mapStats.AvgRate:F1}/s (last {mapStats.IntervalRate:F1}/s), eta={eta}, elapsed={mapStats.Elapsed}");
                    }
                }
                outputObj = reports;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = opts.Pretty,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(outputObj, jsonOptions);

            if (!string.IsNullOrWhiteSpace(opts.OutputPath))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(opts.OutputPath!));
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(opts.OutputPath!, json);
            }

            Console.WriteLine(json);
            return 0;
        }
        catch (ArgException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fatal error: " + ex);
            return 1;
        }
    }

    // ----------------------------
    // Core processing
    // ----------------------------

    private static Report ProcessMapFile(
        string mapFilePath,
        CliOptions opts,
        Dictionary<string, ManualEntry> manual,
        Dictionary<string, List<ReplayEntry>> replayIndex)
    {
        var report = new Report();

        if (opts.IncludePath)
            report.Path = mapFilePath;

        if (!LooksLikeGbx(mapFilePath))
        {
            report.Error = "not a gbx file";
            return report;
        }

        CGameCtnChallenge? map = null;
        try
        {
            map = Gbx.ParseNode<CGameCtnChallenge>(mapFilePath);
        }
        catch (Exception ex)
        {
            report.Error = "failed to parse map gbx";
            report.Note = $"{ex.GetType().Name}: {ex.Message}";
            return report;
        }

        report.Uid = map.MapUid;
        if (opts.IncludeMapName)
            report.MapName = map.MapName;

        var authorMs = TimeToMs(map.AuthorTime);

        // 1) Manual override
        if (!string.IsNullOrWhiteSpace(report.Uid) &&
            manual.TryGetValue(report.Uid!, out var manualEntry))
        {
            report.Type = "manual";
            report.Validated = manualEntry.Valid ? "Yes" : "Maybe";
            report.Note = manualEntry.Note;
            return report;
        }

        // 2) Validation ghost check
        var valGhost = map.ChallengeParameters?.RaceValidateGhost;
        if (valGhost is not null && authorMs.HasValue)
        {
            var valMs = TimeToMs(valGhost.RaceTime);
            if (valMs.HasValue)
            {
                if (valMs.Value == authorMs.Value)
                {
                    report.Validated = "Yes";
                    report.Type = "validationghost";
                    return report;
                }

                report.Validated = "Unknown";
                report.Type = "validationghost";
                report.Error = "validation ghost time mismatch";
                report.Note = $"authorTimeMs={authorMs.Value}, validationGhostMs={valMs.Value}";
                return report;
            }
        }

        // 3) Replay mapping (external evidence)
        if (!string.IsNullOrWhiteSpace(report.Uid) && authorMs.HasValue &&
            replayIndex.TryGetValue(report.Uid!, out var replayEntries))
        {
            var match = replayEntries.FirstOrDefault(r => r.GhostTimesMs.Contains(authorMs.Value));
            if (match is not null)
            {
                report.Validated = "Yes";
                report.Type = "replay";
                report.Note = "Replay ghost time matched map author time.";
                if (opts.IncludePath)
                    report.ReplayPath = match.Path;
                return report;
            }
        }

        // 4) GPS check (optional)
        if (opts.GpsEnabled && authorMs.HasValue)
        {
            if (HasGpsGhostAtAuthorTime(map, authorMs.Value, opts.MaxDepth, opts.GpsThresholdMs, out var gpsMatch))
            {
                report.Type = "gps";
                report.Validated = opts.StrictGps ? "Yes" : "Maybe";

                var matchNote = gpsMatch is null
                    ? null
                    : $"gpsTimeMs={gpsMatch.GpsTimeMs}, deltaMs={gpsMatch.DeltaMs}, source={gpsMatch.Source}.";

                report.Note = opts.StrictGps
                    ? JoinNonEmpty(
                        $"GPS author time match found within \u00b1{opts.GpsThresholdMs} ms.",
                        "GPS times are stored to the nearest tenth of a second, so small discrepancies are expected.",
                        "Strict mode => validated Yes.",
                        matchNote)
                    : JoinNonEmpty(
                        $"GPS author time match found within \u00b1{opts.GpsThresholdMs} ms.",
                        "GPS times are stored to the nearest tenth of a second, so small discrepancies are expected.",
                        "Still potentially invalid.",
                        matchNote);
                return report;
            }
        }

        // 5) Script metadata check => normal vs plugin
        var wpTimes = ExtractWaypointTimes(map.ScriptMetadata);
        if (!authorMs.HasValue || wpTimes is null || wpTimes.Count == 0)
        {
            report.Validated = "Unknown";
            report.Type = "normal";
            report.Note = "Missing author time or Race_AuthorRaceWaypointTimes metadata.";
            return report;
        }

        var metadataFinish = wpTimes[wpTimes.Count - 1];

        var cpCountLooksWeird = !(map.NbCheckpoints == wpTimes.Count || (map.NbCheckpoints + 1) == wpTimes.Count);
        var finishMatchesAuthor = metadataFinish == authorMs.Value;

        if (finishMatchesAuthor)
        {
            report.Validated = "Yes";
            report.Type = "normal";

            if (cpCountLooksWeird)
            {
                report.Note = $"Finish time matches, but checkpoint count differs (mapNbCheckpoints={map.NbCheckpoints}, metadataWaypoints={wpTimes.Count}).";
            }

            return report;
        }

        report.Validated = "Maybe";
        report.Type = "plugin";
        report.Note = $"AuthorTime differs from metadata finish (authorTimeMs={authorMs.Value}, metadataFinishMs={metadataFinish}, mapNbCheckpoints={map.NbCheckpoints}, metadataWaypoints={wpTimes.Count}).";
        return report;

    }

    // ----------------------------
    // Replay index
    // ----------------------------

    private static Dictionary<string, List<ReplayEntry>> BuildReplayIndex(
        string path,
        bool recursive,
        bool progressEnabled,
        double progressIntervalSeconds)
    {
        var dict = new Dictionary<string, List<ReplayEntry>>(StringComparer.Ordinal);

        var files = EnumerateFiles(path, recursive).ToList();
        var total = files.Count;
        var progress = progressEnabled ? new ProgressReporter(TimeSpan.FromSeconds(progressIntervalSeconds)) : null;
        int scanned = 0;
        int gbxCount = 0;
        int indexed = 0;

        foreach (var file in files)
        {
            scanned++;

            if (!LooksLikeGbx(file))
            {
                if (progress is not null && progress.TryGetStats(scanned, out var replayStatsSkip))
                {
                    var eta = ProgressReporter.GetEta(total - scanned, replayStatsSkip.AvgRate);
                    Console.Error.WriteLine(
                        $"Replay scan: {scanned}/{total} files, gbx={gbxCount}, indexed={indexed}, rate={replayStatsSkip.AvgRate:F1}/s (last {replayStatsSkip.IntervalRate:F1}/s), eta={eta}, elapsed={replayStatsSkip.Elapsed}");
                }
                continue;
            }

            try
            {
                gbxCount++;

                var replay = Gbx.ParseNode<CGameCtnReplayRecord>(file);

                var uid = replay.MapInfo?.Id;

                uid ??= replay.Challenge?.MapUid;
                uid ??= replay.Ghosts?.FirstOrDefault()?.Validate_ChallengeUid;

                if (string.IsNullOrWhiteSpace(uid))
                    continue;

                var times = new HashSet<int>();

                foreach (var ghost in replay.GetGhosts(alsoInClips: true))
                {
                    var t = TimeToMs(ghost.RaceTime);
                    if (t.HasValue)
                        times.Add(t.Value);
                }

                if (times.Count == 0)
                {
                    if (progress is not null && progress.TryGetStats(scanned, out var replayStatsNoTimes))
                    {
                        var eta = ProgressReporter.GetEta(total - scanned, replayStatsNoTimes.AvgRate);
                        Console.Error.WriteLine(
                            $"Replay scan: {scanned}/{total} files, gbx={gbxCount}, indexed={indexed}, rate={replayStatsNoTimes.AvgRate:F1}/s (last {replayStatsNoTimes.IntervalRate:F1}/s), eta={eta}, elapsed={replayStatsNoTimes.Elapsed}");
                    }
                    continue;
                }

                if (!dict.TryGetValue(uid!, out var list))
                {
                    list = new List<ReplayEntry>();
                    dict[uid!] = list;
                }

                list.Add(new ReplayEntry(file, times));
                indexed++;
            }
            catch { }

            if (progress is not null && progress.TryGetStats(scanned, out var replayStatsOk))
            {
                var eta = ProgressReporter.GetEta(total - scanned, replayStatsOk.AvgRate);
                Console.Error.WriteLine(
                    $"Replay scan: {scanned}/{total} files, gbx={gbxCount}, indexed={indexed}, rate={replayStatsOk.AvgRate:F1}/s (last {replayStatsOk.IntervalRate:F1}/s), eta={eta}, elapsed={replayStatsOk.Elapsed}");
            }
        }

        return dict;
    }

    // ----------------------------
    // Manual overrides
    // ----------------------------

    private static Dictionary<string, ManualEntry> LoadManualOverrides(string filePath)
    {
        var dict = new Dictionary<string, ManualEntry>(StringComparer.Ordinal);

        var raw = File.ReadAllText(filePath);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch
        {
            raw = raw.Replace("True", "true").Replace("False", "false");
            doc = JsonDocument.Parse(raw);
        }

        void AddEntry(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object)
                return;

            if (!el.TryGetProperty("uid", out var uidProp))
                return;

            var uid = uidProp.GetString();
            if (string.IsNullOrWhiteSpace(uid))
                return;

            var valid = el.TryGetProperty("valid", out var validProp) && validProp.ValueKind == JsonValueKind.True
                ? true
                : el.TryGetProperty("valid", out validProp) && validProp.ValueKind == JsonValueKind.False
                    ? false
                    : true;

            string? note = null;
            if (el.TryGetProperty("note", out var noteProp) && noteProp.ValueKind == JsonValueKind.String)
                note = noteProp.GetString();

            dict[uid!] = new ManualEntry(valid, note);
        }

        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            AddEntry(doc.RootElement);
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
                AddEntry(el);
        }

        return dict;
    }

    // ----------------------------
    // GPS scan
    // ----------------------------

    private static bool HasGpsGhostAtAuthorTime(
        CGameCtnChallenge map,
        int authorMs,
        int? maxDepth,
        int gpsThresholdMs,
        out GpsMatchInfo? matchInfo)
    {
        matchInfo = null;

        if (map.ClipGroupInGame is null)
            return false;

        foreach (var candidate in EnumerateGpsRecordDataCandidates(map))
        {
            var delta = Math.Abs(candidate.TimeMs - authorMs);
            if (delta <= gpsThresholdMs)
            {
                matchInfo = new GpsMatchInfo(candidate.TimeMs, delta, candidate.Source);
                return true;
            }
        }

        foreach (var block in TraverseForType<CGameCtnMediaBlockGhost>(map.ClipGroupInGame, maxDepth))
        {
            var ghost = block.GhostModel;
            if (ghost is null)
                continue;

            var t = TimeToMs(ghost.RaceTime);
            if (!t.HasValue)
                continue;

            var delta = Math.Abs(t.Value - authorMs);
            if (delta <= gpsThresholdMs)
            {
                matchInfo = new GpsMatchInfo(t.Value, delta, "CGameCtnMediaBlockGhost.GhostModel.RaceTime");
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<GpsCandidate> EnumerateGpsRecordDataCandidates(CGameCtnChallenge map)
    {
        var clipGroup = map.ClipGroupInGame;
        if (clipGroup?.Clips is null || clipGroup.Clips.Count == 0)
            yield break;

        for (int triggerIndex = 0; triggerIndex < clipGroup.Clips.Count; triggerIndex++)
        {
            var trigger = clipGroup.Clips[triggerIndex];
            var clip = trigger.Clip;
            if (clip?.Tracks is null || clip.Tracks.Count == 0)
                continue;

            for (int trackIndex = 0; trackIndex < clip.Tracks.Count; trackIndex++)
            {
                var track = clip.Tracks[trackIndex];
                if (track?.Blocks is null || track.Blocks.Count == 0)
                    continue;

                for (int blockIndex = 0; blockIndex < track.Blocks.Count; blockIndex++)
                {
                    var block = track.Blocks[blockIndex];
                    if (block is not CGameCtnMediaBlockEntity entityBlock)
                        continue;

                    var recordData = entityBlock.RecordData;
                    if (recordData?.EntList is null || recordData.EntList.Count == 0)
                        continue;

                    for (int entIndex = 0; entIndex < recordData.EntList.Count; entIndex++)
                    {
                        var ent = recordData.EntList[entIndex];
                        var basePath =
                            $"ClipGroupInGame.Clips[{triggerIndex}].Clip.Tracks[{trackIndex}].Blocks[{blockIndex}].RecordData.EntList[{entIndex}]";

                        var u03 = ent.U03;
                        if (u03 > 0)
                            yield return new GpsCandidate(u03, $"{basePath}.U03");

                        var samples2 = ent.Samples2;
                        if (samples2 is null || samples2.Count == 0)
                            continue;

                        var lastIndex = samples2.Count - 1;
                        var lastTime = TimeToMs(samples2[lastIndex].Time);
                        if (lastTime.HasValue)
                        {
                            yield return new GpsCandidate(
                                lastTime.Value,
                                $"{basePath}.Samples2[{lastIndex}].Time");
                        }
                    }
                }
            }
        }
    }

    private static string JoinNonEmpty(params string?[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private sealed record GpsCandidate(int TimeMs, string Source);

    private sealed record GpsMatchInfo(int GpsTimeMs, int DeltaMs, string Source);

    private static IEnumerable<T> TraverseForType<T>(object root, int? maxDepth) where T : class
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(object Obj, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (obj, depth) = stack.Pop();
            if (obj is null)
                continue;

            if (!visited.Add(obj))
                continue;

            if (obj is T match)
                yield return match;

            if (maxDepth.HasValue && depth >= maxDepth.Value)
                continue;

            foreach (var child in GetChildren(obj))
                stack.Push((child, depth + 1));
        }
    }

    private static IEnumerable<object> GetChildren(object obj)
    {
        if (obj is IEnumerable enumerable && obj is not string)
        {
            int i = 0;
            foreach (var item in enumerable)
            {
                if (item is null) continue;

                yield return item;

                if (++i > 20000) yield break;
            }
            yield break;
        }

        var type = obj.GetType();

        foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!p.CanRead) continue;
            if (p.GetIndexParameters().Length != 0) continue;

            var pt = p.PropertyType;
            if (pt == typeof(string)) continue;
            if (pt.IsPrimitive || pt.IsEnum) continue;
            if (pt.IsValueType) continue;

            object? value = null;
            try { value = p.GetValue(obj); }
            catch {  }

            if (value is not null)
                yield return value;
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    // ----------------------------
    // Script metadata extraction
    // ----------------------------

    private static List<int>? ExtractWaypointTimes(CScriptTraitsMetadata? metadata)
    {
        if (metadata?.Traits is null)
            return null;

        if (!metadata.Traits.TryGetValue(WaypointTimesKey, out var trait))
            return null;

        if (trait is CScriptTraitsMetadata.ScriptArrayTrait arr)
        {
            var list = new List<int>(arr.Value.Count);
            foreach (var el in arr.Value)
            {
                var v = el.GetValue();
                if (v is int i) list.Add(i);
                else if (v is long l) list.Add(unchecked((int)l));
                else if (v is not null && int.TryParse(v.ToString(), out var p)) list.Add(p);
            }
            return list;
        }

        var value = trait.GetValue();
        if (value is IEnumerable enumerable && value is not string)
        {
            var list = new List<int>();
            foreach (var item in enumerable)
            {
                if (item is CScriptTraitsMetadata.ScriptTrait st)
                {
                    var v = st.GetValue();
                    if (v is int i) list.Add(i);
                    else if (v is long l) list.Add(unchecked((int)l));
                    else if (v is not null && int.TryParse(v.ToString(), out var p)) list.Add(p);
                }
            }
            return list;
        }

        return null;
    }

    // ----------------------------
    // Time parsing helpers
    // ----------------------------

    private static int? TimeToMs(object? timeObj)
    {
        if (timeObj is null)
            return null;

        var t = timeObj.GetType();

        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var hasValue = (bool)(t.GetProperty("HasValue")?.GetValue(timeObj) ?? false);
            if (!hasValue) return null;

            timeObj = t.GetProperty("Value")?.GetValue(timeObj);
            if (timeObj is null) return null;

            t = timeObj.GetType();
        }

        if (timeObj is int i) return i;
        if (timeObj is long l) return checked((int)l);
        if (timeObj is uint ui) return unchecked((int)ui);
        if (timeObj is TimeSpan ts) return (int)Math.Round(ts.TotalMilliseconds);

        object? candidate =
            t.GetProperty("TotalMilliseconds")?.GetValue(timeObj) ??
            t.GetProperty("Milliseconds")?.GetValue(timeObj) ??
            t.GetProperty("Value")?.GetValue(timeObj);

        if (candidate is not null)
        {
            if (!ReferenceEquals(candidate, timeObj))
            {
                var inner = TimeToMs(candidate);
                if (inner.HasValue) return inner.Value;
            }

            if (candidate is double d) return (int)Math.Round(d);
            if (candidate is float f) return (int)Math.Round(f);
            if (int.TryParse(candidate.ToString(), out var pi)) return pi;
        }

        if (TryParseTmTimeToMs(timeObj.ToString(), out var ms))
            return ms;

        return null;
    }

    private static bool TryParseTmTimeToMs(string? s, out int ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(s))
            return false;

        s = s.Trim();

        // Expected formats:
        //   m:ss.mmm   e.g. "1:03.502"
        //   h:mm:ss.mmm
        //   ss.mmm
        var parts = s.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        int hours = 0;
        int minutes = 0;
        string secPart;

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out hours)) return false;
            if (!int.TryParse(parts[1], out minutes)) return false;
            secPart = parts[2];
        }
        else if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out minutes)) return false;
            secPart = parts[1];
        }
        else if (parts.Length == 1)
        {
            secPart = parts[0];
        }
        else
        {
            return false;
        }

        int seconds;
        int millis = 0;

        var secMillis = secPart.Split('.', StringSplitOptions.TrimEntries);
        if (!int.TryParse(secMillis[0], out seconds)) return false;

        if (secMillis.Length > 1)
        {
            var msStr = secMillis[1];
            if (msStr.Length > 3) msStr = msStr.Substring(0, 3);
            if (msStr.Length < 3) msStr = msStr.PadRight(3, '0');
            if (!int.TryParse(msStr, out millis)) return false;
        }

        long total = (long)hours * 3600000L + (long)minutes * 60000L + (long)seconds * 1000L + millis;
        if (total < 0 || total > int.MaxValue) return false;

        ms = (int)total;
        return true;
    }

    // ----------------------------
    // File helpers
    // ----------------------------

    private static bool LooksLikeGbx(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[3];
            var read = fs.Read(b);
            return read == 3 && b[0] == 0x47 && b[1] == 0x42 && b[2] == 0x58; // "GBX"
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string path, bool recursive)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }

        if (!Directory.Exists(path))
            yield break;

        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var file in Directory.EnumerateFiles(path, "*", opt))
            yield return file;
    }

    // ----------------------------
    // CLI parsing
    // ----------------------------

    private static CliOptions ParseArgs(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
            throw new ArgException("No arguments provided.");

        string? single = null;
        string? batch = null;

        string? replays = null;
        string? manual = null;

        bool recursive = false;
        bool pretty = false;
        bool includePath = false;
        bool includeMapName = true;
        bool progress = false;
        double progressIntervalSeconds = 5;

        string? output = null;

        bool gpsEnabled = true;
        bool strictGps = false;
        int gpsThresholdMs = DefaultGpsThresholdMs;

        int? maxDepth = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];

            string Next()
            {
                if (i + 1 >= args.Length)
                    throw new ArgException($"Missing value after {a}");
                return args[++i];
            }

            switch (a)
            {
                case "--single":
                    single = Next();
                    break;

                case "--batch":
                    batch = Next();
                    break;

                case "--replays":
                    replays = Next();
                    break;

                case "--manual":
                    manual = Next();
                    break;

                case "--recursive":
                    recursive = true;
                    break;

                case "--pretty":
                    pretty = true;
                    break;

                case "--include-path":
                    includePath = true;
                    break;

                case "--no-map-name":
                    includeMapName = false;
                    break;

                case "--progress":
                    progress = true;
                    break;

                case "--progress-interval":
                    if (!double.TryParse(Next(), out var interval) || interval <= 0)
                        throw new ArgException("--progress-interval must be a positive number (seconds)");
                    progressIntervalSeconds = interval;
                    break;

                case "--output":
                    output = Next();
                    break;

                case "--strict-gps":
                    strictGps = true;
                    gpsEnabled = true;
                    break;

                case "--no-gps":
                    gpsEnabled = false;
                    strictGps = false;
                    break;

                case "--gps-threshold-ms":
                    if (!int.TryParse(Next(), out var gpsThreshold) || gpsThreshold < 0)
                        throw new ArgException("--gps-threshold-ms must be a non-negative integer");
                    gpsThresholdMs = gpsThreshold;
                    break;

                case "--max-depth":
                    if (!int.TryParse(Next(), out var d) || d < 0)
                        throw new ArgException("--max-depth must be a non-negative integer");
                    maxDepth = d;
                    break;

                case "--help":
                    break;

                default:
                    throw new ArgException($"Unknown flag: {a}");
            }
        }

        if ((single is null) == (batch is null))
            throw new ArgException("You must specify exactly one of --single or --batch.");

        var mode = single is not null ? RunMode.Single : RunMode.Batch;
        var mapPath = single ?? batch!;

        if (mode == RunMode.Single && !File.Exists(mapPath))
            throw new ArgException($"Map file does not exist: {mapPath}");

        if (mode == RunMode.Batch && !Directory.Exists(mapPath))
            throw new ArgException($"Map folder does not exist: {mapPath}");

        if (!string.IsNullOrWhiteSpace(replays))
        {
            if (!File.Exists(replays) && !Directory.Exists(replays))
                throw new ArgException($"Replay path does not exist: {replays}");
        }

        if (!string.IsNullOrWhiteSpace(manual))
        {
            if (!File.Exists(manual))
                throw new ArgException($"Manual JSON file does not exist: {manual}");
        }

        return new CliOptions(
            mode,
            mapPath,
            replays,
            manual,
            recursive,
            pretty,
            includePath,
            includeMapName,
            progress,
            progressIntervalSeconds,
            output,
            gpsEnabled,
            strictGps,
            gpsThresholdMs,
            maxDepth
        );
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
@"Usage:
  MapValidationChecker --single <mapFile> [--replays <replayFileOrFolder>] [flags...]
  MapValidationChecker --batch  <mapFolder> [--replays <replayFolder>] [flags...]

Flags:
  --recursive                Recurse into subfolders (batch + replay scanning)
  --pretty                   Pretty-print JSON
  --include-path             Include ""path"" and ""replayPath"" (if matched)
  --no-map-name              Omit ""mapName"" from JSON output
  --progress                 Print periodic scan progress to stderr
  --progress-interval <sec>  Progress update interval in seconds (default: 5)
  --output <file>            Write JSON output to a file (also prints to stdout)
  --manual <file>            Manual overrides JSON (object or array of objects):
                             { ""valid"": true/false, ""uid"": ""..."", ""note"": ""..."" }

  --strict-gps               If GPS ghost matches author time => validated ""Yes"" (default: ""Maybe"")
  --no-gps                   Disable GPS scan
  --gps-threshold-ms <ms>    GPS author time tolerance in milliseconds (default: 100)
  --max-depth <n>            Limit reflection traversal depth for GPS scan (default: unlimited)

Notes:
  - Manual override has highest priority.
  - GPS times are stored to the nearest tenth of a second, so small discrepancies are expected.
  - If a validation ghost exists and its race time != author time, an error is returned."
        );
    }

    // ----------------------------
    // Models
    // ----------------------------

    private enum RunMode { Single, Batch }

    private sealed record CliOptions(
        RunMode Mode,
        string MapPath,
        string? ReplaysPath,
        string? ManualPath,
        bool Recursive,
        bool Pretty,
        bool IncludePath,
        bool IncludeMapName,
        bool Progress,
        double ProgressIntervalSeconds,
        string? OutputPath,
        bool GpsEnabled,
        bool StrictGps,
        int GpsThresholdMs,
        int? MaxDepth
    );

    private sealed record ManualEntry(bool Valid, string? Note);

    private sealed record ReplayEntry(string Path, HashSet<int> GhostTimesMs);

    private sealed class Report
    {
        public string? Uid { get; set; }
        public string? Validated { get; set; }
        public string? Type { get; set; }
        public string? Note { get; set; }
        public string? Path { get; set; }
        public string? MapName { get; set; }
        public string? ReplayPath { get; set; }
        public string? Error { get; set; }
    }

    private sealed class ArgException : Exception
    {
        public ArgException(string message) : base(message) { }
    }

    private sealed class ProgressReporter
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly TimeSpan _interval;
        private TimeSpan _last = TimeSpan.Zero;
        private double _lastReportSeconds;
        private int _lastReportCount;

        public ProgressReporter(TimeSpan interval)
        {
            _interval = interval;
        }

        public bool TryGetStats(int currentCount, out ProgressStats stats)
        {
            var elapsed = _sw.Elapsed;
            if (elapsed - _last < _interval)
            {
                stats = default;
                return false;
            }

            _last = elapsed;

            var elapsedSeconds = elapsed.TotalSeconds;
            var avgRate = GetRate(currentCount, elapsedSeconds);
            var intervalSeconds = elapsedSeconds - _lastReportSeconds;
            var intervalCount = currentCount - _lastReportCount;
            var intervalRate = GetRate(intervalCount, intervalSeconds);

            _lastReportSeconds = elapsedSeconds;
            _lastReportCount = currentCount;

            stats = new ProgressStats(FormatElapsed(elapsed), elapsedSeconds, avgRate, intervalRate);
            return true;
        }

        private static string FormatElapsed(TimeSpan t)
        {
            var minutes = (int)t.TotalMinutes;
            return $"{minutes}m{t.Seconds:D2}s";
        }

        public static double GetRate(int processed, double elapsedSeconds)
        {
            if (processed <= 0 || elapsedSeconds <= 0)
                return 0;
            return processed / elapsedSeconds;
        }

        public static string GetEta(int remaining, double rate)
        {
            if (remaining <= 0)
                return "0m00s";
            if (rate <= 0)
                return "unknown";

            var seconds = (int)Math.Round(remaining / rate);
            if (seconds < 0) seconds = 0;
            return FormatElapsed(TimeSpan.FromSeconds(seconds));
        }
    }

    private readonly record struct ProgressStats(
        string Elapsed,
        double ElapsedSeconds,
        double AvgRate,
        double IntervalRate
    );
}
