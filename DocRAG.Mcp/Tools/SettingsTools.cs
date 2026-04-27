// SettingsTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Ingestion.Embedding;
using ModelContextProtocol.Server;
using Serilog.Core;
using Serilog.Events;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tools for runtime configuration changes.
///     Enables the LLM to toggle features like re-ranking
///     and logging without restarting the service.
/// </summary>
[McpServerToolType]
public static class SettingsTools
{
    [McpServerTool(Name = "toggle_reranking")]
    [Description("Enable or disable LLM-based re-ranking of search results. " +
                 "When enabled, search results are re-scored by the configured strategy " +
                 "(Off / Llm / CrossEncoder — see RankingSettings.ReRankerStrategy). " +
                 "Identifier-shaped queries (CamelCase, dotted, callable) skip re-ranking " +
                 "even when enabled — hybrid scoring already wins on them. " +
                 "Returns the current state plus the strategy that will actually dispatch."
                )]
    public static string ToggleReRanking(ToggleableReRanker reRanker,
                                         [Description("true to enable, false to disable, omit to just check current state")]
                                         bool? enabled = null)
    {
        ArgumentNullException.ThrowIfNull(reRanker);

        if (enabled.HasValue)
            reRanker.Enabled = enabled.Value;

        var response = new
                           {
                               ReRankingEnabled = reRanker.Enabled,
                               ActiveStrategy = reRanker.ActiveStrategy.ToString()
                           };
        var result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    [McpServerTool(Name = "toggle_logging")]
    [Description("Change the minimum log level at runtime. " +
                 "Use 'Warning' or 'Error' for quiet production operation. " +
                 "Use 'Information' or 'Debug' for troubleshooting. " +
                 "Returns the current level.")]
    public static string ToggleLogging(LoggingLevelSwitch levelSwitch,
                                       [Description("Minimum log level: Verbose, Debug, Information, Warning, Error, Fatal. Omit to check current level.")]
                                       string? level = null)
    {
        ArgumentNullException.ThrowIfNull(levelSwitch);

        if (!string.IsNullOrEmpty(level) && Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed))
            levelSwitch.MinimumLevel = parsed;

        var response = new { MinimumLogLevel = levelSwitch.MinimumLevel.ToString() };
        var result = JsonSerializer.Serialize(response, smJsonOptions);
        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
