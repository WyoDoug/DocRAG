// DiagnosticTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for server diagnostics â€” log access and startup stats.
/// </summary>
[McpServerToolType]
public static class DiagnosticTools
{
    /// <summary>
    ///     Holds the log directory path, registered in DI at startup.
    ///     Declared as a sealed class (not a record) on purpose: the MCP
    ///     parameter marshaller mis-classifies positional records as
    ///     JSON-deserializable arguments, which collides with the schema
    ///     generator's DI-exclusion and breaks any call that supplies an
    ///     argument alongside the DI-injected one.
    /// </summary>
    public sealed class LogConfig
    {
        public LogConfig(string logDirectory)
        {
            ArgumentException.ThrowIfNullOrEmpty(logDirectory);
            LogDirectory = logDirectory;
        }

        public string LogDirectory { get; }
    }

    [McpServerTool(Name = "get_server_logs")]
    [Description("Get the last N lines from the server log file. " +
                 "Useful for diagnosing scrape failures, startup issues, or checking crawl progress. " +
                 "Default 50 lines, max 500."
                )]
    public static string GetServerLogs(LogConfig logConfig,
                                       [Description("Number of log lines to return (default 50, max 500)")]
                                       int lines = DefaultLineCount,
                                       [Description("Filter log lines containing this text (case-insensitive)")]
                                       string? filter = null)
    {
        ArgumentNullException.ThrowIfNull(logConfig);

        int lineCount = Math.Clamp(lines, MinLineCount, MaxLineCount);
        string? logFile = FindLatestLogFile(logConfig.LogDirectory);
        string json;

        if (logFile == null)
            json = JsonSerializer.Serialize(new { Error = NoLogFilesFoundMessage }, smJsonOptions);
        else
        {
            try
            {
                IReadOnlyList<string> allLines = ReadLogFileShared(logFile);

                IEnumerable<string> filtered = allLines;
                if (!string.IsNullOrEmpty(filter))
                {
                    filtered = allLines.Where(l =>
                                                  l.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                             );
                }

                var tail = filtered.TakeLast(lineCount).ToList();

                var response = new
                                   {
                                       LogFile = Path.GetFileName(logFile),
                                       TotalLines = allLines.Count,
                                       ReturnedLines = tail.Count,
                                       Filter = filter,
                                       Lines = tail
                                   };
                json = JsonSerializer.Serialize(response, smJsonOptions);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {
                var error = new
                                {
                                    Error = $"Failed to read log file '{Path.GetFileName(logFile)}': {ex.Message}"
                                };
                json = JsonSerializer.Serialize(error, smJsonOptions);
            }
        }

        return json;
    }

    /// <summary>
    ///     Pick the most recently written log file matching the search pattern.
    ///     Sort by <see cref="FileSystemInfo.LastWriteTimeUtc"/> rather than
    ///     filename so the picker is robust against rotation schemes that
    ///     don't sort lexicographically.
    /// </summary>
    private static string? FindLatestLogFile(string logDirectory)
    {
        string? result = null;
        if (Directory.Exists(logDirectory))
        {
            result = new DirectoryInfo(logDirectory)
                     .EnumerateFiles(LogFileSearchPattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .FirstOrDefault()
                    ?.FullName;
        }

        return result;
    }

    private static IReadOnlyList<string> ReadLogFileShared(string path)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        var line = reader.ReadLine();
        while (line != null)
        {
            lines.Add(line);
            line = reader.ReadLine();
        }

        return lines;
    }

    private const string NoLogFilesFoundMessage = "No log files found.";
    private const string LogFileSearchPattern = "docrag-*.log";
    private const int MinLineCount = 1;
    private const int DefaultLineCount = 50;
    private const int MaxLineCount = 500;

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
