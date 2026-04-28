// CancellationTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Enums;
using DocRAG.Ingestion;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

/// <summary>
///     MCP tool for cancelling an in-flight scrape job.
/// </summary>
[McpServerToolType]
public static class CancellationTools
{
    private const string SignalledMessage = "Pipeline cancellation signalled. Job will transition to Cancelled.";
    private const string OrphanCleanedUpMessage = "Job had no active runner; DB row marked Cancelled directly.";
    private const string AlreadyTerminalMessage = "Job is already Completed, Failed, or Cancelled. No action taken.";
    private const string NotFoundMessage = "No scrape job found with that id.";
    private const string UnknownOutcomeMessage = "Unknown outcome.";

    [McpServerTool(Name = "cancel_scrape")]
    [Description("Cancel a running scrape job. Signals the pipeline cancellation token " +
                 "for active jobs, or marks the DB row Cancelled directly for orphaned " +
                 "jobs (process restarted while job was Running). No-op for jobs already " +
                 "Completed/Failed/Cancelled. Partial results are kept — call delete_version " +
                 "to clear them, or submit_url_correction if the cancel was triggered by a wrong " +
                 "URL (that tool clears partial data and re-queues with a corrected URL in one step)."
                )]
    public static async Task<string> CancelScrape(ScrapeJobRunner runner,
                                                  [Description("Job id from list_scrape_jobs or get_scrape_status")]
                                                  string jobId,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var outcome = await runner.CancelAsync(jobId, ct);

        var response = new
                           {
                               JobId = jobId,
                               Outcome = outcome.ToString(),
                               Message = outcome switch
                                             {
                                                 CancelScrapeOutcome.Signalled => SignalledMessage,
                                                 CancelScrapeOutcome.OrphanCleanedUp => OrphanCleanedUpMessage,
                                                 CancelScrapeOutcome.AlreadyTerminal => AlreadyTerminalMessage,
                                                 CancelScrapeOutcome.NotFound => NotFoundMessage,
                                                 _ => UnknownOutcomeMessage
                                             }
                           };
        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
