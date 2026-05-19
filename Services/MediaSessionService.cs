using AppleMusicTranslator.Models;
using Windows.Media.Control;

namespace AppleMusicTranslator.Services;

public sealed class MediaSessionService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    public async Task<TrackInfo> GetCurrentTrackAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        var session = FindAppleMusicSession(_manager.GetSessions())
            ?? _manager.GetCurrentSession();

        if (session is null)
        {
            return TrackInfo.Empty;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();
            var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            return new TrackInfo(
                Clean(properties.Title),
                Clean(properties.Artist),
                Clean(properties.AlbumTitle),
                timeline.Position < TimeSpan.Zero ? TimeSpan.Zero : timeline.Position,
                timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : TimeSpan.Zero,
                isPlaying);
        }
        catch
        {
            return TrackInfo.Empty;
        }
    }

    private static GlobalSystemMediaTransportControlsSession? FindAppleMusicSession(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        return sessions.FirstOrDefault(session =>
        {
            var id = session.SourceAppUserModelId ?? string.Empty;
            return id.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)
                || id.Contains("AppleInc.AppleMusic", StringComparison.OrdinalIgnoreCase)
                || id.Contains("iTunes", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
