using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.StorageHelpers;

/// <summary>
/// A list kept as JSON in a file of its own, such as Finly's profile levels and reported problems. A file that can't
/// be read is set aside, never written over, so nothing in it is lost.
/// </summary>
/// <typeparam name="T">The type of the items.</typeparam>
public sealed class JsonListFile<T>
{
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    // Set when an unreadable file couldn't be moved aside, so writing would lose it
    private bool _keepUnreadableFile;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonListFile{T}"/> class.
    /// </summary>
    /// <param name="path">The path of the file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock for naming a set aside file, the system clock when not given.</param>
    public JsonListFile(string path, ILogger logger, TimeProvider? timeProvider = null)
    {
        Path = path;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the path of the file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Reads the list. A file that can't be read or parsed is renamed to <c>&lt;name&gt;.bad-&lt;yyyyMMddHHmmss&gt;</c>
    /// (UTC) and an empty list is returned, so the next write starts a new file.
    /// </summary>
    /// <returns>The list, or <c>null</c> when there is no file yet.</returns>
    public List<T>? Read()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(Path), JsonDefaults.Options) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            var badPath = Path + ".bad-" + _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(Path, badPath, true);
                _logger.LogError(ex, "Unable to read {Path}, it was moved to {BadPath} and an empty list is used", Path, badPath);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Unable to read {Path}, and unable to move it aside ({Reason}); an empty list is used and nothing is saved until it is fixed", Path, moveEx.Message);
                _keepUnreadableFile = true;
            }

            return [];
        }
    }

    /// <summary>
    /// Writes the list, through a temporary file so a crash can't leave half a file.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <exception cref="IOException">The file couldn't be read or moved aside, and would be lost.</exception>
    public void Write(List<T> items)
    {
        if (_keepUnreadableFile && File.Exists(Path))
        {
            throw new IOException($"{Path} can't be read and couldn't be moved aside. Fix or remove it before saving.");
        }

        _keepUnreadableFile = false;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, JsonDefaults.Options));
        File.Move(temp, Path, true);
    }
}
