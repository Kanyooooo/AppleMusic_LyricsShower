using AppleMusicTranslator.Services;

var media = new MediaSessionService();
var finder = new AppleMusicProcessFinder();
var extractor = new ProcessMemoryTtmlExtractor();
var parser = new TtmlLyricsParser();
var matcher = new LyricsMatcher();
var anchors = new AppleMusicLyricAnchorService();

Console.OutputEncoding = System.Text.Encoding.UTF8;

var track = await media.GetCurrentTrackAsync();
Console.WriteLine($"track: {track.Title}");
Console.WriteLine($"artist: {track.Artist}");
Console.WriteLine($"album: {track.Album}");
Console.WriteLine($"position: {track.Position:mm\\:ss\\.fff}");
Console.WriteLine($"duration: {track.Duration:mm\\:ss\\.fff}");
Console.WriteLine($"playing: {track.IsPlaying}");

var pid = finder.FindProcessId();
Console.WriteLine($"apple_music_pid: {(pid is null ? "not found" : pid.Value)}");
if (pid is null)
{
    return 2;
}

if (args.Length > 0 && args[0].Equals("search", StringComparison.OrdinalIgnoreCase))
{
    var queries = args.Skip(1).ToArray();
    if (queries.Length == 0)
    {
        Console.WriteLine("usage: dotnet run --project .\\Diagnostics\\AppleMusicTranslator.Diagnostics.csproj -- search <text> [more-text]");
        return 2;
    }

    using var searchCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var searcher = new MemoryStringSearcher();
    var hits = searcher.Search(pid.Value, queries, searchCts.Token);
    Console.WriteLine($"hits: {hits.Count}");

    foreach (var hit in hits)
    {
        Console.WriteLine($"--- query=\"{hit.Query}\" encoding={hit.EncodingName} addr=0x{hit.Address:X} region=0x{hit.RegionBase:X}+0x{hit.RegionSize:X}");
        Console.WriteLine(hit.Context);
    }

    return 0;
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var visibleAnchors = anchors.FindVisibleAnchors(pid.Value, track);
Console.WriteLine($"visible_anchors: {visibleAnchors.Count}");
foreach (var anchor in visibleAnchors.Take(20))
{
    Console.WriteLine($"  anchor: {anchor}");
}

var rawBlocks = await extractor.ExtractAllAsync(pid.Value, track.CacheKey, cts.Token);
Console.WriteLine($"raw_ttml_blocks: {rawBlocks.Count}");

var candidates = parser.ParseBlocks(rawBlocks);
Console.WriteLine($"parsed_candidates: {candidates.Count}");

var index = 0;
foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Lines.Count).Take(12))
{
    var match = matcher.FindBestMatch([candidate], track, visibleAnchors);
    var score = match?.Score ?? double.NaN;
    var anchorHits = match?.AnchorHits ?? 0;
    Console.WriteLine($"candidate[{index}] source={candidate.Source} lines={candidate.Lines.Count} duration={candidate.Duration:mm\\:ss\\.fff} score={score:0.0} anchor_hits={anchorHits}");

    foreach (var line in candidate.Lines.Take(3))
    {
        Console.WriteLine($"  {line.Begin:mm\\:ss\\.fff}-{line.End:mm\\:ss\\.fff} {line.Text}");
    }

    index++;
}

var best = matcher.FindBestMatch(candidates, track, visibleAnchors);
Console.WriteLine(best is null
    ? "best_match: none"
    : $"best_match: lines={best.Lyrics.Lines.Count} duration={best.Lyrics.Duration:mm\\:ss\\.fff} score={best.Score:0.0} anchor_hits={best.AnchorHits}");

if (rawBlocks.Count == 0)
{
    using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var probe = new MemoryTokenProbe();
    var probeResult = probe.Scan(pid.Value, probeCts.Token);
    Console.WriteLine("token_probe:");
    foreach (var item in probeResult.Counts.Where(item => item.Value > 0).OrderByDescending(item => item.Value))
    {
        Console.WriteLine($"  {item.Key}: {item.Value}");
    }

    Console.WriteLine($"first_tt_context: {probeResult.Context}");
}

return 0;
