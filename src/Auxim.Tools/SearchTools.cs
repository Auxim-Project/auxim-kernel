using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Auxim.Core.Logging;
using Auxim.Core.Resources;
using Auxim.Core.Tools;
using Auxim.VAFS;

namespace Auxim.Tools;

public static class SearchTools
{
    private const int RipgrepCommandBudget = 12_000;
    private static readonly TimeSpan RipgrepShutdownLimit = TimeSpan.FromSeconds(2);
    private static readonly StringComparer HostPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Register(ToolRegistry registry)
    {
        FileTools.RegisterFileTool(registry, static view => new ToolDefinition(
            "file.search",
            "files",
            "Searches files under /workspace, /tmp, or mounted /volumes paths. Uses ripgrep when available.",
            (args, token) => SearchAsync(view, args, token))
        {
            ParametersSchema = FileTools.ObjectSchema(
                [
                    ("pattern", "string", "Text or regex pattern to search for."),
                    ("path", "string", "Virtual path to search. Defaults to /workspace."),
                    ("maxResults", "integer", "Maximum results to return."),
                ],
                ["pattern"]),
            ResourceAccessResolver = args => [SearchAccess(view, SearchPath(args))],
        });
    }

    private static string SearchPath(IReadOnlyDictionary<string, object?> args) =>
        args.TryGetValue("path", out var value) ? value?.ToString() ?? "/workspace" : "/workspace";

    private static ResourceAccess SearchAccess(VirtualAgentFileSystem? view, string path)
    {
        try
        {
            // The target may be a directory when the handler starts.
            return FileTools.FileAccess(view, path, ResourceAction.Read) with { IncludeDescendants = view is not null };
        }
        catch (Exception error) when (IsPathFailure(error))
        {
            throw PathFailure(path, error);
        }
    }

    private static async Task<string> SearchAsync(
        VirtualAgentFileSystem? view, IReadOnlyDictionary<string, object?> args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pattern = FileTools.Required(args, "pattern");
        var maxResults = args.TryGetValue("maxResults", out var value)
            && int.TryParse(value?.ToString(), out var limit) && limit > 0
                ? Math.Min(limit, 500)
                : 100;
        var path = SearchPath(args);
        (string HostPath, string VirtualPath) root;
        bool recursive;
        try
        {
            view ??= FileTools.Vafs();
            root = (view.ResolveToHostPath(path), view.ResolveVirtualPath(path));
            recursive = File.GetAttributes(root.HostPath).HasFlag(FileAttributes.Directory);
        }
        catch (Exception error) when (IsPathFailure(error))
        {
            throw PathFailure(path, error);
        }

        var warnings = new SearchWarnings();
        var files = recursive ? EnumerateFiles(view, root, warnings, token) : [root];
        var ripgrep = FindRipgrep();
        var matches = new List<string>();
        if (ripgrep is null)
        {
            await SearchManagedAsync(files, pattern, maxResults, matches, recursive, warnings, token);
        }
        else
        {
            var fixedSize = ArgumentCost(ripgrep) + RipgrepArguments(pattern, maxResults).Sum(ArgumentCost);
            foreach (var batch in BatchFiles(files, RipgrepCommandBudget - fixedSize))
            {
                await SearchRipgrepAsync(view, ripgrep, batch, pattern, maxResults, matches, recursive, warnings, token);
                if (matches.Count == maxResults)
                {
                    break;
                }
            }
        }

        token.ThrowIfCancellationRequested();
        var result = matches.Count == 0 ? "No matches." : string.Join(Environment.NewLine, matches);
        return warnings.Count == 0 ? result
            : $"{result}{Environment.NewLine}Search incomplete: {warnings.Count} skipped path(s), including '{warnings.FirstPath}'.";
    }

    private static IEnumerable<(string HostPath, string VirtualPath)> EnumerateFiles(
        VirtualAgentFileSystem view, (string HostPath, string VirtualPath) root,
        SearchWarnings warnings, CancellationToken token)
    {
        var pending = new Stack<(string HostPath, string VirtualPath)>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (directory != root && !TryGetDescendant(view, directory.HostPath, directory.VirtualPath, warnings, out _))
            {
                continue;
            }

            foreach (var entry in EnumerateDirectory(directory.HostPath, directory.VirtualPath, directory == root, warnings))
            {
                token.ThrowIfCancellationRequested();
                if (entry.Name == ".git")
                {
                    continue;
                }

                var virtualPath = $"{directory.VirtualPath.TrimEnd('/')}/{entry.Name}";
                if (!TryGetDescendant(view, entry.FullName, virtualPath, warnings, out var attributes))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push((entry.FullName, virtualPath));
                }
                else
                {
                    yield return (entry.FullName, virtualPath);
                }
            }
        }
    }

    private static IEnumerable<FileSystemInfo> EnumerateDirectory(
        string hostPath, string virtualPath, bool isRoot, SearchWarnings warnings)
    {
        IEnumerator<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(hostPath).EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (Exception error) when (IsPathFailure(error))
        {
            RecordPathFailure(virtualPath, error, !isRoot, warnings);
            yield break;
        }

        using (entries)
        {
            var sawEntry = false;
            while (true)
            {
                bool moved;
                try
                {
                    moved = entries.MoveNext();
                }
                catch (Exception error) when (IsPathFailure(error))
                {
                    RecordPathFailure(virtualPath, error, !isRoot || sawEntry, warnings);
                    yield break;
                }

                if (!moved)
                {
                    yield break;
                }

                sawEntry = true;
                yield return entries.Current;
            }
        }
    }

    private static bool TryGetDescendant(VirtualAgentFileSystem view, string hostPath, string virtualPath,
        SearchWarnings warnings, out FileAttributes attributes)
    {
        attributes = default;
        try
        {
            attributes = File.GetAttributes(hostPath);
            return !attributes.HasFlag(FileAttributes.ReparsePoint) && MatchesView(view, hostPath, virtualPath);
        }
        catch (Exception error) when (IsPathFailure(error) || error is VirtualPathException)
        {
            warnings.Record(virtualPath);
            return false;
        }
    }

    private static bool IsPathFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static IOException PathFailure(string virtualPath, Exception error)
    {
        var reason = error switch
        {
            FileNotFoundException or DirectoryNotFoundException => "path not found",
            UnauthorizedAccessException => "access denied",
            _ => "filesystem error",
        };
        return new IOException($"Cannot search '{virtualPath}': {reason}.");
    }

    private static void RecordPathFailure(string virtualPath, Exception error, bool canContinue, SearchWarnings warnings)
    {
        if (!canContinue)
        {
            throw PathFailure(virtualPath, error);
        }

        warnings.Record(virtualPath);
    }

    private static bool MatchesView(VirtualAgentFileSystem view, string hostPath, string virtualPath)
    {
        // The dot segment preserves trailing spaces in the entry name during normalization.
        var resolved = view.ResolveToHostPath(virtualPath + "/.");
        return HostPathComparer.Equals(hostPath, resolved);
    }

    private static int ArgumentCost(string value) =>
        Encoding.UTF8.GetByteCount(value) + value.Count(character => character is '\\' or '"') + 3;

    private static string[] RipgrepArguments(string pattern, int maxResults) =>
        ["--no-config", "--no-follow", "--no-ignore", "--max-depth", "0", "--json",
            "--max-count", maxResults.ToString(CultureInfo.InvariantCulture), "--", pattern];

    private static IEnumerable<IReadOnlyDictionary<string, string>> BatchFiles(
        IEnumerable<(string HostPath, string VirtualPath)> files, int budget)
    {
        if (budget <= 0)
        {
            throw new InvalidOperationException("ripgrep arguments exceed the command-line size budget.");
        }

        var batch = new Dictionary<string, string>(HostPathComparer);
        var size = 0;
        foreach (var (hostPath, virtualPath) in files)
        {
            var cost = ArgumentCost(hostPath);
            if (cost > budget)
            {
                throw new InvalidOperationException("ripgrep pattern and file arguments exceed the command-line size budget.");
            }

            if (batch.Count > 0 && size + cost > budget)
            {
                yield return batch;
                batch = new Dictionary<string, string>(HostPathComparer);
                size = 0;
            }

            batch.Add(hostPath, virtualPath);
            size += cost;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static async Task SearchManagedAsync(
        IEnumerable<(string HostPath, string VirtualPath)> files,
        string pattern, int maxResults, List<string> matches, bool recursive, SearchWarnings warnings, CancellationToken token)
    {
        foreach (var (hostPath, virtualPath) in files)
        {
            token.ThrowIfCancellationRequested();
            var lineNumber = 0;
            try
            {
                await foreach (var line in File.ReadLinesAsync(hostPath, token))
                {
                    token.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add($"{virtualPath}:{lineNumber}:{line}");
                        if (matches.Count == maxResults)
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception error) when (IsPathFailure(error))
            {
                RecordPathFailure(virtualPath, error, recursive, warnings);
            }
        }
    }

    private static async Task SearchRipgrepAsync(
        VirtualAgentFileSystem view, string ripgrep, IReadOnlyDictionary<string, string> files,
        string pattern, int maxResults, List<string> matches, bool recursive, SearchWarnings warnings, CancellationToken token)
    {
        var usable = new Dictionary<string, string>(HostPathComparer);
        foreach (var (hostPath, virtualPath) in files)
        {
            token.ThrowIfCancellationRequested();
            if (recursive)
            {
                if (!TryGetDescendant(view, hostPath, virtualPath, warnings, out var attributes))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    warnings.Record(virtualPath);
                    continue;
                }
            }
            else
            {
                try
                {
                    if (!MatchesView(view, hostPath, virtualPath))
                    {
                        throw new VirtualPathException($"Search path '{virtualPath}' changed before ripgrep started.");
                    }
                }
                catch (Exception error) when (IsPathFailure(error))
                {
                    throw PathFailure(virtualPath, error);
                }
            }

            usable.Add(hostPath, virtualPath);
        }

        if (usable.Count == 0)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ripgrep,
            WorkingDirectory = Path.GetDirectoryName(usable.Keys.First())!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in RipgrepArguments(pattern, maxResults - matches.Count).Concat(usable.Keys))
        {
            startInfo.ArgumentList.Add(argument);
        }

        token.ThrowIfCancellationRequested();
        using var process = StartRipgrep(startInfo);
        using var pipeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stdout = ReadMatchesAsync(process.StandardOutput, usable, maxResults, matches, pipeCancellation.Token);
        var stderr = ReadRipgrepDiagnosticsAsync(process.StandardError, usable, pipeCancellation.Token);
        try
        {
            await DrainRipgrepAsync(process, stdout, stderr, token);
            if (process.ExitCode is not (0 or 1))
            {
                var diagnostics = await stderr;
                if (recursive && process.ExitCode == 2 && diagnostics.IsFileFailure)
                {
                    foreach (var path in diagnostics.FileFailures)
                    {
                        warnings.Record(path);
                    }

                    return;
                }

                var detail = diagnostics.RegexError ? "invalid regular expression."
                    : diagnostics.IsFileFailure ? $"could not read '{diagnostics.FileFailures.First()}'."
                    : "search process reported an error.";
                throw new InvalidOperationException($"ripgrep failed with exit code {process.ExitCode}: {detail}");
            }
        }
        catch (Exception failure)
        {
            await StopRipgrepAsync(process, failure);
            _ = ObservePipeCompletionAsync(Task.WhenAll(stdout, stderr, pipeCancellation.CancelAsync()));
            throw;
        }
    }

    private static Process StartRipgrep(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ripgrep.");
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException($"Failed to start ripgrep (OS error {error.NativeErrorCode}).");
        }
        catch (Exception error) when (IsPathFailure(error))
        {
            throw new InvalidOperationException("Failed to start ripgrep.");
        }
    }

    private static async Task DrainRipgrepAsync(Process process, Task stdout, Task stderr, CancellationToken token)
    {
        var exited = process.WaitForExitAsync(token);
        while (!exited.IsCompleted && (!stdout.IsCompletedSuccessfully || !stderr.IsCompletedSuccessfully))
        {
            var completed = await Task.WhenAny(exited,
                stdout.IsCompletedSuccessfully ? exited : stdout,
                stderr.IsCompletedSuccessfully ? exited : stderr).WaitAsync(token);
            await completed;
        }

        await exited.WaitAsync(token);
        try
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(RipgrepShutdownLimit, token);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("ripgrep exited, but its output streams did not close in time.");
        }
    }

    private static async Task<RipgrepDiagnostics> ReadRipgrepDiagnosticsAsync(
        StreamReader stream, IReadOnlyDictionary<string, string> files, CancellationToken token)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var regexError = false;
        var otherError = false;
        while (await stream.ReadLineAsync(token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var file = files.FirstOrDefault(file => line.StartsWith($"rg: {file.Key}: ", StringComparison.Ordinal));
            if (file.Value is not null)
            {
                paths.Add(file.Value);
            }
            else
            {
                regexError |= line.StartsWith("rg: regex parse error:", StringComparison.Ordinal);
                otherError = true;
            }
        }

        return new RipgrepDiagnostics(paths, regexError, otherError);
    }

    private static async Task ReadMatchesAsync(
        StreamReader output, IReadOnlyDictionary<string, string> files,
        int maxResults, List<string> matches, CancellationToken token)
    {
        while (await output.ReadLineAsync(token) is { } line)
        {
            // Drain the batch even after reaching the result limit so its exit status is still checked.
            if (matches.Count == maxResults)
            {
                continue;
            }

            string path;
            long lineNumber;
            string content;
            try
            {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                if (root.GetProperty("type").GetString() != "match")
                {
                    continue;
                }

                var data = root.GetProperty("data");
                path = ReadText(data.GetProperty("path"));
                lineNumber = data.GetProperty("line_number").GetInt64();
                content = ReadText(data.GetProperty("lines"));
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
            {
                throw new InvalidOperationException("ripgrep returned invalid structured output.");
            }

            if (!files.TryGetValue(path, out var virtualPath))
            {
                throw new InvalidOperationException("ripgrep returned a path outside its file arguments.");
            }

            if (content.EndsWith('\n'))
            {
                content = content[..^1];
                if (content.EndsWith('\r'))
                {
                    content = content[..^1];
                }
            }

            matches.Add($"{virtualPath}:{lineNumber}:{content}");
        }
    }

    private static string ReadText(JsonElement value) =>
        value.TryGetProperty("text", out var text)
            ? text.GetString()!
            : Encoding.UTF8.GetString(value.GetProperty("bytes").GetBytesFromBase64());

    private static async Task StopRipgrepAsync(Process process, Exception failure)
    {
        using var timeout = new CancellationTokenSource(RipgrepShutdownLimit);
        var deadline = timeout.Token;
        var stopping = Task.Run(() => TerminateRipgrepAsync(process, deadline));
        try
        {
            if (await stopping.WaitAsync(deadline))
            {
                ReportCleanupFailure(failure, "Could not confirm ripgrep process tree termination. The direct child exited.");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            ReportCleanupFailure(failure, "Timed out confirming ripgrep termination.");
            _ = ObservePipeCompletionAsync(stopping);
        }
        catch (Exception error)
        {
            ReportCleanupFailure(failure, $"Could not confirm ripgrep termination ({error.GetType().Name}).");
        }
    }

    private static async Task<bool> TerminateRipgrepAsync(Process process, CancellationToken token)
    {
        var treeTerminationUnconfirmed = false;
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception or AggregateException)
            {
                treeTerminationUnconfirmed = true;
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
        }

        await process.WaitForExitAsync(token);
        return treeTerminationUnconfirmed;
    }

    private static void ReportCleanupFailure(Exception failure, string message)
    {
        failure.Data["RipgrepProcessCleanup"] = message;
        AuximLog.Warning(message);
    }

    private static async Task ObservePipeCompletionAsync(Task pipes)
    {
        try
        {
            await pipes;
        }
        catch
        {
            // The original failure already represents this invocation.
        }
    }

    private static string? FindRipgrep()
    {
        var executable = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
            Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in paths)
        {
            var candidate = Path.Combine(directory.Trim('"'), executable);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private sealed class SearchWarnings
    {
        public int Count { get; private set; }
        public string? FirstPath { get; private set; }

        public void Record(string path)
        {
            Count++;
            FirstPath ??= path;
        }
    }

    private sealed record RipgrepDiagnostics(HashSet<string> FileFailures, bool RegexError, bool OtherError)
    {
        public bool IsFileFailure => FileFailures.Count != 0 && !OtherError;
    }
}
