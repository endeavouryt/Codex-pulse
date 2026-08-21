using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexPulse.Services;

internal sealed class CodexDesktopFocusTracker
{
    private const int MaxTailBytes = 256 * 1024;
    private static readonly TimeSpan MaxLogAge = TimeSpan.FromDays(2);
    private static readonly Regex ProcessIdPattern = new(
        @"-(?<pid>\d+)-t\d+-i\d+-",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly TimeSpan UserSelectionLogWindow = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UserSelectionLeadWindow = TimeSpan.FromSeconds(1.5);
    private FocusedSelection? _lastSelection;
    private DateTimeOffset? _lastProcessedUserInputUtc;

    public string? ReadFocusedSessionId(
        int? foregroundProcessId,
        DateTimeOffset? userInputAtUtc = null)
    {
        var selections = new List<FocusedSelection>();
        foreach (var file in FindCandidateLogs(foregroundProcessId))
        {
            selections.AddRange(ReadSelections(file));
        }

        if (selections.Count == 0)
        {
            return _lastSelection?.SessionId;
        }

        var latest = selections
            .OrderByDescending(item => item.Timestamp)
            .ThenByDescending(item => item.Rank)
            .First();

        if (_lastSelection is null)
        {
            // A route/resume signal is a better initial hint than a background
            // stream update, but both remain valid fallbacks at startup.
            _lastSelection = selections
                .Where(item => item.IsDirectSelection)
                .OrderByDescending(item => item.Timestamp)
                .ThenByDescending(item => item.Rank)
                .FirstOrDefault() ?? latest;
        }
        else if (userInputAtUtc.HasValue &&
                 (!_lastProcessedUserInputUtc.HasValue ||
                  userInputAtUtc.Value > _lastProcessedUserInputUtc.Value))
        {
            _lastProcessedUserInputUtc = userInputAtUtc;

            // A single global input timestamp is much more precise than the
            // old "any event in the last N seconds" rule. The desktop log also
            // contains background subagent activity, so accept the first direct
            // route/resume signal around this input and ignore later background
            // stream updates until the next user input.
            var lowerBound = userInputAtUtc.Value - UserSelectionLeadWindow;
            var upperBound = userInputAtUtc.Value + UserSelectionLogWindow;
            var userSelection = selections
                .Where(item => item.IsDirectSelection &&
                               item.Timestamp >= lowerBound &&
                               item.Timestamp <= upperBound)
                .OrderBy(item => item.Timestamp < userInputAtUtc.Value)
                .ThenBy(item => item.Timestamp)
                .ThenByDescending(item => item.Rank)
                .FirstOrDefault();
            if (userSelection is not null)
            {
                _lastSelection = userSelection;
            }
        }

        return _lastSelection?.SessionId;
    }

    private static IEnumerable<FileInfo> FindCandidateLogs(int? foregroundProcessId)
    {
        var roots = FindLogRoots().ToArray();
        var files = new List<FileInfo>();
        foreach (var root in roots)
        {
            try
            {
                files.AddRange(new DirectoryInfo(root)
                    .EnumerateFiles("codex-desktop-*.log", SearchOption.AllDirectories)
                    .Where(file => DateTime.UtcNow - file.LastWriteTimeUtc <= MaxLogAge));
            }
            catch (IOException)
            {
                // Logs can rotate while they are being enumerated.
            }
            catch (UnauthorizedAccessException)
            {
                // The monitor remains usable when the desktop log is unavailable.
            }
        }

        var distinctFiles = files
            .GroupBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        if (foregroundProcessId.HasValue)
        {
            var processLogs = distinctFiles
                .Where(file => GetProcessId(file.Name) == foregroundProcessId.Value)
                .Take(4)
                .ToArray();
            if (processLogs.Length > 0)
            {
                return processLogs;
            }
        }

        return distinctFiles.Take(8);
    }

    private static IEnumerable<FocusedSelection> ReadSelections(FileInfo file)
    {
        foreach (var line in ReadTailLines(file.FullName))
        {
            var isResume = line.Contains("maybe_resume_started", StringComparison.OrdinalIgnoreCase);
            var isResumeComplete = line.Contains("maybe_resume_success", StringComparison.OrdinalIgnoreCase);
            var isThreadResume = line.Contains("method=thread/resume", StringComparison.OrdinalIgnoreCase);
            var isActiveStreamView = line.Contains(
                                         "thread_stream_view_activity_changed",
                                         StringComparison.OrdinalIgnoreCase) &&
                                     line.Contains("active=true", StringComparison.OrdinalIgnoreCase);
            var isOwnerRoute = line.Contains(
                                   "IAB_LIFECYCLE received browser sidebar owner sync",
                                   StringComparison.OrdinalIgnoreCase) &&
                               line.Contains("ownerRoutePath=/local/", StringComparison.OrdinalIgnoreCase);
            var hasFocusedVisibleWindow = line.Contains(
                                              "rendererWindowFocused=true",
                                              StringComparison.OrdinalIgnoreCase) &&
                                          line.Contains(
                                              "rendererWindowVisible=true",
                                              StringComparison.OrdinalIgnoreCase);
            if ((!isResume && !isResumeComplete && !isThreadResume && !isActiveStreamView && !isOwnerRoute) ||
                !hasFocusedVisibleWindow && !isOwnerRoute)
            {
                continue;
            }

            var sessionId = isOwnerRoute
                ? ReadField(line, "ownerRoutePath")?.TrimStart('/').Replace("local/", string.Empty, StringComparison.OrdinalIgnoreCase)
                : ReadField(line, "conversationId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                continue;
            }

            const string clientThreadPrefix = "client-new-thread:";
            if (sessionId.StartsWith(clientThreadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                sessionId = sessionId[clientThreadPrefix.Length..];
            }

            if (!Guid.TryParse(sessionId, out _))
            {
                continue;
            }

            var timestamp = ReadTimestamp(line) ?? file.LastWriteTimeUtc;
            var isResumeSignal = isResume || isResumeComplete || isThreadResume;
            var rank = isOwnerRoute ? 4 : isActiveStreamView ? 3 : isResumeComplete ? 2 : 1;
            yield return new FocusedSelection(
                sessionId,
                timestamp,
                rank,
                isResumeSignal,
                isOwnerRoute || isResumeSignal);
        }
    }

    private static IEnumerable<string> FindLogRoots()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("CODEX_PULSE_CODEX_LOG_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            yield return configuredRoot;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packagesRoot = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            IEnumerable<string> packageDirectories;
            try
            {
                packageDirectories = Directory.EnumerateDirectories(packagesRoot, "OpenAI.Codex_*");
            }
            catch (IOException)
            {
                packageDirectories = Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                packageDirectories = Array.Empty<string>();
            }

            foreach (var packageDirectory in packageDirectories)
            {
                yield return Path.Combine(packageDirectory, "LocalCache", "Local", "Codex", "Logs");
            }
        }
    }

    private static int? GetProcessId(string fileName)
    {
        var match = ProcessIdPattern.Match(fileName);
        return match.Success && int.TryParse(match.Groups["pid"].Value, out var processId)
            ? processId
            : null;
    }

    private static string? ReadField(string line, string fieldName)
    {
        var marker = fieldName + "=";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = line.IndexOf(' ', start);
        return (end < 0 ? line[start..] : line[start..end]).Trim();
    }

    private static DateTimeOffset? ReadTimestamp(string line)
    {
        var end = line.IndexOf(' ');
        if (end <= 0)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            line[..end],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static IEnumerable<string> ReadTailLines(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0L, stream.Length - MaxTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var length = checked((int)(stream.Length - start));
            var bytes = new byte[length];
            var read = 0;
            while (read < bytes.Length)
            {
                var current = stream.Read(bytes, read, bytes.Length - read);
                if (current == 0)
                {
                    break;
                }

                read += current;
            }

            var content = Encoding.UTF8.GetString(bytes, 0, read);
            if (start > 0)
            {
                var firstNewline = content.IndexOf('\n');
                content = firstNewline >= 0 ? content[(firstNewline + 1)..] : string.Empty;
            }

            return content.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private sealed record FocusedSelection(
        string SessionId,
        DateTimeOffset Timestamp,
        int Rank,
        bool IsResumeSignal,
        bool IsDirectSelection);
}
