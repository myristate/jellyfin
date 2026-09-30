using System;
using System.Globalization;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Keeps only the last half hour of a live channel's HLS segments (Finly).
/// </summary>
/// <remarks>
/// A live channel is served as live.m3u8, which ffmpeg writes as it goes and the server hands out as it is; segments are
/// served straight from the files it lists. It used to be an EVENT playlist that listed, and kept on disk, every segment
/// since the channel started: about 7 GB an hour at a high bitrate. It is now a sliding live playlist of the last 30
/// minutes, and ffmpeg deletes each segment once it drops out of it. Players only ask for segments the playlist lists,
/// and a player paused for longer than the window finds its position gone from the playlist and jumps to live, as it
/// does for any live stream.
/// </remarks>
public static class LiveHlsWindow
{
    /// <summary>
    /// How much of a live channel is kept.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many segments that have dropped out of the playlist are kept before being deleted, for a player that fetched
    /// the playlist just before and is still downloading them.
    /// </summary>
    public const int DeleteThreshold = 5;

    /// <summary>
    /// Gets ffmpeg's HLS playlist arguments for a live channel.
    /// </summary>
    /// <param name="isLivePlaylist">Whether the playlist is live.m3u8, which grows as ffmpeg writes it.</param>
    /// <param name="isInfiniteStream">Whether the source is a live channel, with no end.</param>
    /// <param name="segmentLength">The segment length in seconds.</param>
    /// <returns>The arguments, or <c>null</c> for a stream that keeps upstream's playlist.</returns>
    public static string? GetPlaylistArguments(bool isLivePlaylist, bool isInfiniteStream, int segmentLength)
    {
        if (!isLivePlaylist || !isInfiniteStream)
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "-hls_list_size {0} -hls_delete_threshold {1} -hls_flags delete_segments",
            GetSegmentCount(segmentLength),
            DeleteThreshold);
    }

    /// <summary>
    /// Gets how many segments of the given length make up the window.
    /// </summary>
    /// <param name="segmentLength">The segment length in seconds.</param>
    /// <returns>The number of segments.</returns>
    public static int GetSegmentCount(int segmentLength)
        => (int)Math.Ceiling(Window.TotalSeconds / Math.Max(1, segmentLength));
}
