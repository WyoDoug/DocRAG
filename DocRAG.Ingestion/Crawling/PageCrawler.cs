// PageCrawler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DocRAG.Core.Enums;
using DocRAG.Core.Interfaces;
using DocRAG.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

#endregion

namespace DocRAG.Ingestion.Crawling;

/// <summary>
///     Crawls documentation sites using Playwright (headless Chromium).
///     Discovers pages via breadth-first link traversal within allowed URL patterns.
///     Applies a same-scope-unlimited / out-of-scope-depth-limited heuristic
///     so that crawls don't recurse forever into linked GitHub or external sites.
/// </summary>
public class PageCrawler
{
    private record CrawlEntry(string Url, int InScopeDepth, int SameHostDepth, int OffSiteDepth);

    private record RootScope(string Host, string PathPrefix);

    private record FrameStats(int TextLength, int LinkTextLength);

    /// <summary>
    ///     Mutable counters for the dry-run BFS loop.
    ///     Used instead of ref parameters since the helper methods are async.
    /// </summary>
    private class DryRunStats
    {
        public int TotalPages { get; set; }
        public int InScopePages { get; set; }
        public int OutOfScopePages { get; set; }
        public int DepthLimitedSkips { get; set; }
        public int FilteredSkips { get; set; }
        public int FetchErrors { get; set; }
        public bool HitMaxLimit { get; set; }
    }

    public PageCrawler(IPageRepository pageRepository,
                       GitHubRepoScraper gitHubScraper,
                       ILogger<PageCrawler> logger)
    {
        mPageRepository = pageRepository;
        mGitHubScraper = gitHubScraper;
        mLogger = logger;
    }

    private readonly GitHubRepoScraper mGitHubScraper;
    private readonly ILogger<PageCrawler> mLogger;

    private readonly IPageRepository mPageRepository;

    // Per-crawl state: once a site returns 404 on stripped URLs
    // but succeeds with the extension, we stop stripping.
    private string? mSiteExtension;

    /// <summary>
    ///     Dry-run a crawl: actually fetch every page with Playwright,
    ///     resolve all links, but DO NOT store anything to MongoDB or
    ///     clone any GitHub repos. Returns a detailed report so you can
    ///     decide whether the real crawl will produce reasonable results.
    /// </summary>
    public async Task<DryRunReport> DryRunAsync(ScrapeJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var startTime = DateTime.UtcNow;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                                                                            {
                                                                                Headless = true,
                                                                                Args =
                                                                                    [
                                                                                        $"--user-agent={BrowserUserAgent}"
                                                                                    ]
                                                                            }
                                                                       );

        var rootUri = new Uri(job.RootUrl);
        var rootScope = ComputeRootScope(rootUri);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<CrawlEntry>();
        string normalizedRoot = NormalizeUrl(job.RootUrl) ?? job.RootUrl;
        queue.Enqueue(new CrawlEntry(normalizedRoot, InScopeDepth: 0, SameHostDepth: 0, OffSiteDepth: 0));

        var stats = new DryRunStats();
        var depthDist = new Dictionary<int, int>();
        var pagesByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var githubRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var samplePages = new List<DryRunPageEntry>();
        var errors = new List<DryRunFetchError>();

        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            if (job.MaxPages > 0 && stats.TotalPages >= job.MaxPages)
            {
                stats.HitMaxLimit = true;
                break;
            }

            var entry = queue.Dequeue();
            string url = entry.Url;

            if (visited.Add(url))
            {
                await ProcessDryRunEntryAsync(url,
                                              entry,
                                              job,
                                              rootScope,
                                              browser,
                                              visited,
                                              queue,
                                              githubRepos,
                                              samplePages,
                                              errors,
                                              depthDist,
                                              pagesByHost,
                                              stats,
                                              ct
                                             );
            }
        }

        var report = new DryRunReport
                         {
                             TotalPages = stats.TotalPages,
                             InScopePages = stats.InScopePages,
                             OutOfScopePages = stats.OutOfScopePages,
                             DepthLimitedSkips = stats.DepthLimitedSkips,
                             FilteredSkips = stats.FilteredSkips,
                             FetchErrors = stats.FetchErrors,
                             DepthDistribution = depthDist,
                             PagesByHost = pagesByHost,
                             GitHubReposToClone = githubRepos.OrderBy(r => r).ToList(),
                             SamplePages = samplePages,
                             Errors = errors,
                             ElapsedTime = DateTime.UtcNow - startTime,
                             HitMaxPagesLimit = stats.HitMaxLimit,
                             PagesRemainingInQueue = queue.Count,
                             SamplePendingUrls = queue.Take(SamplePendingUrlCount).Select(e => e.Url).ToList()
                         };

        return report;
    }

    private async Task ProcessDryRunEntryAsync(string url,
                                               CrawlEntry entry,
                                               ScrapeJob job,
                                               RootScope rootScope,
                                               IBrowser browser,
                                               HashSet<string> visited,
                                               Queue<CrawlEntry> queue,
                                               HashSet<string> githubRepos,
                                               List<DryRunPageEntry> samplePages,
                                               List<DryRunFetchError> errors,
                                               Dictionary<int, int> depthDist,
                                               Dictionary<string, int> pagesByHost,
                                               DryRunStats stats,
                                               CancellationToken ct)
    {
        switch(true)
        {
            case true when !IsAllowed(url, job):
                stats.FilteredSkips++;
                break;
            case true when GitHubRepoScraper.TryParseGitHubUrl(url, out string owner, out string repo):
                githubRepos.Add($"{owner}/{repo}");
                break;
            default:
                await ProcessInScopeEntryAsync(url,
                                               entry,
                                               job,
                                               rootScope,
                                               browser,
                                               visited,
                                               queue,
                                               samplePages,
                                               errors,
                                               depthDist,
                                               pagesByHost,
                                               stats,
                                               ct
                                              );
                break;
        }
    }

    private async Task ProcessInScopeEntryAsync(string url,
                                                CrawlEntry entry,
                                                ScrapeJob job,
                                                RootScope rootScope,
                                                IBrowser browser,
                                                HashSet<string> visited,
                                                Queue<CrawlEntry> queue,
                                                List<DryRunPageEntry> samplePages,
                                                List<DryRunFetchError> errors,
                                                Dictionary<int, int> depthDist,
                                                Dictionary<string, int> pagesByHost,
                                                DryRunStats stats,
                                                CancellationToken ct)
    {
        bool inScope = IsInRootScope(url, rootScope);
        bool sameHost = !inScope && IsSameHost(url, rootScope);
        bool depthExceeded = inScope  ? job.InScopeDepth > 0 && entry.InScopeDepth >= job.InScopeDepth :
                             sameHost ? entry.SameHostDepth >= job.SameHostDepth :
                                        entry.OffSiteDepth >= job.OffSiteDepth;

        if (depthExceeded)
            stats.DepthLimitedSkips++;
        else
        {
            await FetchDryRunPageAsync(url,
                                       entry,
                                       job,
                                       rootScope,
                                       browser,
                                       visited,
                                       queue,
                                       samplePages,
                                       errors,
                                       depthDist,
                                       pagesByHost,
                                       inScope,
                                       stats,
                                       ct
                                      );
        }
    }

    private async Task FetchDryRunPageAsync(string url,
                                            CrawlEntry entry,
                                            ScrapeJob job,
                                            RootScope rootScope,
                                            IBrowser browser,
                                            HashSet<string> visited,
                                            Queue<CrawlEntry> queue,
                                            List<DryRunPageEntry> samplePages,
                                            List<DryRunFetchError> errors,
                                            Dictionary<int, int> depthDist,
                                            Dictionary<string, int> pagesByHost,
                                            bool inScope,
                                            DryRunStats stats,
                                            CancellationToken ct)
    {
        mLogger.LogDebug("[dry-run] Fetching {Url}", url);

        var page = await browser.NewPageAsync();
        try
        {
            var response = await NavigateAndPreparePageAsync(page, url, ct);

            switch(response)
            {
                case null:
                    stats.FetchErrors++;
                    errors.Add(new DryRunFetchError
                                   {
                                       Url = url,
                                       HttpStatus = 0,
                                       ErrorKind = "NoResponse",
                                       Message = "Playwright returned null response"
                                   }
                              );
                    break;
                case { Ok: false }:
                    stats.FetchErrors++;
                    errors.Add(new DryRunFetchError
                                   {
                                       Url = url,
                                       HttpStatus = response.Status,
                                       ErrorKind = $"Http{response.Status}",
                                       Message = response.StatusText
                                   }
                              );
                    break;
                default:
                    await ProcessSuccessfulDryRunResponseAsync(page,
                                                               url,
                                                               entry,
                                                               job,
                                                               rootScope,
                                                               inScope,
                                                               visited,
                                                               queue,
                                                               samplePages,
                                                               depthDist,
                                                               pagesByHost,
                                                               stats,
                                                               ct
                                                              );
                    break;
            }
        }
        catch(Exception ex)
        {
            stats.FetchErrors++;
            errors.Add(new DryRunFetchError
                           {
                               Url = url,
                               HttpStatus = 0,
                               ErrorKind = ex.GetType().Name,
                               Message = ex.Message.Length > ErrorMessageMaxLength
                                             ? ex.Message[..ErrorMessageMaxLength]
                                             : ex.Message
                           }
                      );
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task ProcessSuccessfulDryRunResponseAsync(IPage page,
                                                            string url,
                                                            CrawlEntry entry,
                                                            ScrapeJob job,
                                                            RootScope rootScope,
                                                            bool inScope,
                                                            HashSet<string> visited,
                                                            Queue<CrawlEntry> queue,
                                                            List<DryRunPageEntry> samplePages,
                                                            Dictionary<int, int> depthDist,
                                                            Dictionary<string, int> pagesByHost,
                                                            DryRunStats stats,
                                                            CancellationToken ct)
    {
        await ExpandCollapsibleNavigationAsync(page);
        string content = await ExtractMainContentAsync(page);
        var links = await ExtractLinksAsync(page);

        stats.TotalPages++;
        if (inScope)
            stats.InScopePages++;
        else
            stats.OutOfScopePages++;

        bool urlSameHost = !inScope && IsSameHost(url, rootScope);
        int effectiveDepth = inScope     ? entry.InScopeDepth :
                             urlSameHost ? entry.SameHostDepth : entry.OffSiteDepth;

        depthDist.TryGetValue(effectiveDepth, out int existing);
        depthDist[effectiveDepth] = existing + 1;

        string host = new Uri(url).Host;
        pagesByHost.TryGetValue(host, out int hostCount);
        pagesByHost[host] = hostCount + 1;

        if (samplePages.Count < SamplePageLimit)
        {
            samplePages.Add(new DryRunPageEntry
                                {
                                    Url = url,
                                    OutOfScopeDepth = effectiveDepth,
                                    InScope = inScope,
                                    ContentBytes = content.Length,
                                    LinksFound = links.Count
                                }
                           );
        }

        EnqueueDiscoveredLinks(links,
                               visited,
                               job,
                               rootScope,
                               entry,
                               queue
                              );

        if (job.FetchDelayMs > 0)
            await Task.Delay(job.FetchDelayMs, ct);
    }

    /// <summary>
    ///     Crawl a documentation library starting from the root URL.
    /// </summary>
    public async Task CrawlAsync(ScrapeJob job,
                                 ChannelWriter<PageRecord> output,
                                 IReadOnlySet<string>? resumeUrls = null,
                                 Action<int>? onPageFetched = null,
                                 Action<int>? onQueued = null,
                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                                                                            {
                                                                                Headless = true,
                                                                                Args =
                                                                                    [
                                                                                        $"--user-agent={BrowserUserAgent}"
                                                                                    ]
                                                                            }
                                                                       );

        var rootUri = new Uri(job.RootUrl);
        var rootScope = ComputeRootScope(rootUri);

        mSiteExtension = null;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resumeUrls != null)
        {
            foreach(string resumeUrl in resumeUrls)
                visited.Add(resumeUrl);
            mLogger.LogInformation("Resume: seeded visited set with {Count} existing URLs", resumeUrls.Count);
        }

        var clonedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<CrawlEntry>();
        string normalizedRoot = NormalizeUrl(job.RootUrl) ?? job.RootUrl;
        queue.Enqueue(new CrawlEntry(normalizedRoot, InScopeDepth: 0, SameHostDepth: 0, OffSiteDepth: 0));
        var pageCount = 0;

        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            if (job.MaxPages > 0 && pageCount >= job.MaxPages)
                break;

            var entry = queue.Dequeue();
            string url = entry.Url;

            if (visited.Add(url))
            {
                int previousCount = pageCount;
                pageCount = await ProcessCrawlEntryAsync(url,
                                                         entry,
                                                         job,
                                                         rootScope,
                                                         browser,
                                                         visited,
                                                         clonedRepos,
                                                         queue,
                                                         pageCount,
                                                         output,
                                                         ct
                                                        );

                if (pageCount > previousCount)
                    onPageFetched?.Invoke(pageCount);
                onQueued?.Invoke(queue.Count);
            }
        }

        mLogger.LogInformation("Crawl complete for {LibraryId} v{Version}: {Count} pages",
                               job.LibraryId,
                               job.Version,
                               pageCount
                              );
        output.Complete();
    }

    private async Task<int> ProcessCrawlEntryAsync(string url,
                                                   CrawlEntry entry,
                                                   ScrapeJob job,
                                                   RootScope rootScope,
                                                   IBrowser browser,
                                                   HashSet<string> visited,
                                                   HashSet<string> clonedRepos,
                                                   Queue<CrawlEntry> queue,
                                                   int pageCount,
                                                   ChannelWriter<PageRecord> output,
                                                   CancellationToken ct)
    {
        int result = pageCount;

        switch(true)
        {
            case true when !IsAllowed(url, job):
                break;
            case true when GitHubRepoScraper.TryParseGitHubUrl(url, out string owner, out string repo):
                var repoKey = $"{owner}/{repo}";
                if (clonedRepos.Add(repoKey))
                {
                    mLogger.LogInformation("Delegating to GitHub scraper for {Repo}", repoKey);
                    await mGitHubScraper.ScrapeRepositoryAsync(owner, repo, job, output, ct);
                }

                break;
            default:
                result = await ProcessCrawlScopeAsync(url,
                                                      entry,
                                                      job,
                                                      rootScope,
                                                      browser,
                                                      visited,
                                                      queue,
                                                      pageCount,
                                                      output,
                                                      ct
                                                     );
                break;
        }

        return result;
    }

    private async Task<int> ProcessCrawlScopeAsync(string url,
                                                   CrawlEntry entry,
                                                   ScrapeJob job,
                                                   RootScope rootScope,
                                                   IBrowser browser,
                                                   HashSet<string> visited,
                                                   Queue<CrawlEntry> queue,
                                                   int pageCount,
                                                   ChannelWriter<PageRecord> output,
                                                   CancellationToken ct)
    {
        bool inScope = IsInRootScope(url, rootScope);
        bool sameHost = !inScope && IsSameHost(url, rootScope);
        bool depthExceeded = inScope  ? job.InScopeDepth > 0 && entry.InScopeDepth >= job.InScopeDepth :
                             sameHost ? entry.SameHostDepth >= job.SameHostDepth :
                                        entry.OffSiteDepth >= job.OffSiteDepth;

        int result = pageCount;
        if (depthExceeded)
        {
            int displayDepth = inScope  ? entry.InScopeDepth :
                               sameHost ? entry.SameHostDepth : entry.OffSiteDepth;
            mLogger.LogDebug("Skipping {Url} â€” depth {Depth} exceeded", url, displayDepth);
        }
        else
            result = await FetchCrawlPageAsync(url,
                                               entry,
                                               job,
                                               rootScope,
                                               browser,
                                               visited,
                                               queue,
                                               pageCount,
                                               inScope,
                                               output,
                                               ct
                                              );

        return result;
    }

    private async Task<int> FetchCrawlPageAsync(string url,
                                                CrawlEntry entry,
                                                ScrapeJob job,
                                                RootScope rootScope,
                                                IBrowser browser,
                                                HashSet<string> visited,
                                                Queue<CrawlEntry> queue,
                                                int pageCount,
                                                bool inScope,
                                                ChannelWriter<PageRecord> output,
                                                CancellationToken ct)
    {
        int result = pageCount;

        bool fetchSameHost = !inScope && IsSameHost(url, rootScope);
        int fetchDepth = fetchSameHost ? entry.SameHostDepth : entry.OffSiteDepth;
        mLogger.LogInformation("Fetching ({Count}) [{Scope} d={Depth}]: {Url}",
                               pageCount + 1,
                               inScope ? InScopeLabel : OutOfScopeLabel,
                               fetchDepth,
                               url
                              );

        var page = await browser.NewPageAsync();
        try
        {
            string fetchUrl = url;
            var response = await NavigateAndPreparePageAsync(page, fetchUrl, ct);

            // Retry with known extensions if 404 - site may require .html, .htm, etc.
            if (response != null && response.Status == HttpNotFound && mSiteExtension == null)
                (page, response, fetchUrl) = await RetryWithExtensionsAsync(url, page, browser, ct);

            if (response != null && response.Ok)
            {
                string title = await page.TitleAsync();
                await ExpandCollapsibleNavigationAsync(page);
                string content = await ExtractMainContentAsync(page);
                var links = await ExtractLinksAsync(page);

                string contentHash = ComputeHash(content);
                string urlHash = ComputeHash(fetchUrl);

                var pageRecord = new PageRecord
                                     {
                                         Id = $"{job.LibraryId}/{job.Version}/{urlHash[..12]}",
                                         LibraryId = job.LibraryId,
                                         Version = job.Version,
                                         Url = fetchUrl,
                                         Title = title,
                                         Category = DocCategory.Unclassified,
                                         RawContent = content,
                                         FetchedAt = DateTime.UtcNow,
                                         ContentHash = contentHash
                                     };

                await mPageRepository.UpsertPageAsync(pageRecord, ct);
                result = pageCount + 1;
                await output.WriteAsync(pageRecord, ct);

                EnqueueDiscoveredLinks(links,
                                       visited,
                                       job,
                                       rootScope,
                                       entry,
                                       queue,
                                       mSiteExtension != null
                                      );

                if (job.FetchDelayMs > 0)
                    await Task.Delay(job.FetchDelayMs, ct);
            }
            else
                mLogger.LogWarning("Failed to fetch {Url}: {Status}", url, response?.Status);
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Error fetching {Url}", url);
        }
        finally
        {
            await page.CloseAsync();
        }

        return result;
    }

    private async Task<IResponse?> NavigateAndPreparePageAsync(IPage page,
                                                               string url,
                                                               CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(url);

        ct.ThrowIfCancellationRequested();

        var result = await page.GotoAsync(url,
                                          new PageGotoOptions
                                              {
                                                  WaitUntil = WaitUntilState.DOMContentLoaded,
                                                  Timeout = PageTimeoutMs
                                              }
                                         );

        if (result != null && result.Ok)
            await WaitForPageAndFramesAsync(page, url, ct);

        return result;
    }

    private async Task WaitForPageAndFramesAsync(IPage page,
                                                 string url,
                                                 CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(url);

        await WaitForLoadStateWithinTimeoutAsync(() => page.WaitForLoadStateAsync(LoadState.Load),
                                                 MainPageLoadLabel,
                                                 url,
                                                 ct
                                                );

        bool hasFrameElements = await HasFrameElementsAsync(page);
        if (hasFrameElements)
        {
            await WaitForFrameCollectionToStabilizeAsync(page, ct);

            var childFrames = page.Frames.Where(f => f != page.MainFrame).ToList();
            mLogger.LogDebug("Waiting for {Count} child frames on {Url}", childFrames.Count, url);

            foreach(var frame in childFrames)
            {
                string frameDescription = DescribeFrame(frame);
                await WaitForLoadStateWithinTimeoutAsync(() => frame.WaitForLoadStateAsync(LoadState.DOMContentLoaded),
                                                         $"frame {frameDescription}",
                                                         url,
                                                         ct
                                                        );
            }
        }
    }

    private async Task WaitForLoadStateWithinTimeoutAsync(Func<Task> waitOperation,
                                                          string targetDescription,
                                                          string url,
                                                          CancellationToken ct)
    {
        try
        {
            var waitTask = waitOperation();
            var timeoutTask = Task.Delay(LoadStateTimeoutMs, ct);
            var completedTask = await Task.WhenAny(waitTask, timeoutTask);

            if (completedTask == waitTask)
                await waitTask;
            else
            {
                ct.ThrowIfCancellationRequested();
                mLogger.LogDebug("Timed out waiting for {Target} on {Url} after {Timeout}ms",
                                 targetDescription,
                                 url,
                                 LoadStateTimeoutMs
                                );
            }
        }
        catch(PlaywrightException ex)
        {
            mLogger.LogDebug(ex, "Failed waiting for {Target} on {Url}", targetDescription, url);
        }
    }

    private static async Task<bool> HasFrameElementsAsync(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        bool result = page.Frames.Count > 1;

        if (!result)
        {
            try
            {
                var frameElementCount =
                    await page.EvaluateAsync<int>("() => document.querySelectorAll('frame,iframe').length");
                result = frameElementCount > 0;
            }
            catch(PlaywrightException)
            {
                result = page.Frames.Count > 1;
            }
        }

        return result;
    }

    private static async Task WaitForFrameCollectionToStabilizeAsync(IPage page,
                                                                     CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var stopwatch = Stopwatch.StartNew();
        int lastFrameCount = page.Frames.Count;
        var stablePollCount = 0;

        while (stopwatch.ElapsedMilliseconds < FrameDiscoveryTimeoutMs && stablePollCount < StableFramePollCount)
        {
            await Task.Delay(FrameDiscoveryPollDelayMs, ct);

            int currentFrameCount = page.Frames.Count;
            if (currentFrameCount == lastFrameCount)
                stablePollCount++;
            else
            {
                stablePollCount = 0;
                lastFrameCount = currentFrameCount;
            }
        }
    }

    private static string DescribeFrame(IFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        string result = frame.Name;

        if (string.IsNullOrWhiteSpace(result))
            result = frame.Url;

        if (string.IsNullOrWhiteSpace(result))
            result = "(unnamed frame)";

        return result;
    }

    /// <summary>
    ///     Normalize and enqueue discovered links from a fetched page.
    ///     Applies 3-tier depth assignment:
    ///     in root scope â†’ both depths reset to 0;
    ///     same host, different path â†’ increment SameHostDepth;
    ///     different host â†’ increment OffSiteDepth.
    /// </summary>
    private static void EnqueueDiscoveredLinks(IReadOnlyList<string> links,
                                               HashSet<string> visited,
                                               ScrapeJob job,
                                               RootScope rootScope,
                                               CrawlEntry parentEntry,
                                               Queue<CrawlEntry> queue,
                                               bool keepExtension = false)
    {
        foreach(string normalized in links
                                     .Select(u => NormalizeUrl(u, keepExtension))
                                     .OfType<string>()
                                     .Where(n => !visited.Contains(n) && IsAllowed(n, job)))
        {
            bool linkInScope = IsInRootScope(normalized, rootScope);
            bool linkSameHost = !linkInScope && IsSameHost(normalized, rootScope);

            var child = linkInScope switch
                {
                    true => new CrawlEntry(normalized, parentEntry.InScopeDepth + 1, SameHostDepth: 0, OffSiteDepth: 0),
                    false => linkSameHost
                                 ? new CrawlEntry(normalized,
                                                  parentEntry.InScopeDepth,
                                                  parentEntry.SameHostDepth + 1,
                                                  parentEntry.OffSiteDepth
                                                 )
                                 : new CrawlEntry(normalized,
                                                  parentEntry.InScopeDepth,
                                                  parentEntry.SameHostDepth,
                                                  parentEntry.OffSiteDepth + 1
                                                 )
                };

            queue.Enqueue(child);
        }
    }

    /// <summary>
    ///     Compute the "scope" of the root URL â€” same domain plus path-prefix.
    ///     A page is considered in-scope if its host matches AND its path
    ///     starts with the root's parent path.
    ///     Example: root https://docs.foo.com/v2/intro/quick-start.html
    ///     scope (foo.com, /v2/intro/)
    ///     in scope: /v2/intro/getting-started.html, /v2/intro/sub/page.html
    ///     out of scope: /v2/api/X.html, /blog/y.html
    /// </summary>
    private static RootScope ComputeRootScope(Uri rootUri)
    {
        string path = rootUri.AbsolutePath;
        int lastSlash = path.LastIndexOf(value: '/');
        string scopePath = lastSlash >= 0 ? path[..(lastSlash + 1)] : "/";

        var result = new RootScope(rootUri.Host, scopePath);
        return result;
    }

    private static bool IsInRootScope(string url, RootScope scope)
    {
        var result = false;
        try
        {
            var uri = new Uri(url);
            result = string.Equals(uri.Host, scope.Host, StringComparison.OrdinalIgnoreCase) &&
                     uri.AbsolutePath.StartsWith(scope.PathPrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Malformed URL â€” treat as out of scope
        }

        return result;
    }

    /// <summary>
    ///     Returns true when the URL is on the same host as the root scope
    ///     but does NOT fall within the root path prefix.
    /// </summary>
    private static bool IsSameHost(string url, RootScope scope)
    {
        var result = false;
        try
        {
            var uri = new Uri(url);
            result = string.Equals(uri.Host, scope.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Malformed URL â€” treat as off-site
        }

        return result;
    }

    private static async Task<string> ExtractMainContentAsync(IPage page)
    {
        // First try the main page with CSS selectors
        string result = await ExtractContentFromFrameAsync(page);

        // If the main page has little content, check iframes for a content frame
        if (result.Length < ContentFrameMinChars && page.Frames.Count > 1)
        {
            var bestFrame = await FindContentFrameAsync(page);
            if (bestFrame != null)
            {
                string frameContent = await ExtractTextFromFrameAsync(bestFrame);
                if (frameContent.Length > result.Length)
                    result = frameContent;
            }
        }

        return result;
    }

    private static async Task<string> ExtractContentFromFrameAsync(IPage page)
    {
        var contentSelectors = new[]
                                   {
                                       "main", "article", "[role='main']",
                                       ".content", ".doc-content", ".documentation",
                                       "#content", "#main-content"
                                   };

        var result = string.Empty;

        foreach(string selector in contentSelectors.Where(_ => result == string.Empty))
        {
            var element = await page.QuerySelectorAsync(selector);
            if (element != null)
            {
                string text = await element.InnerTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                    result = text.Trim();
            }
        }

        if (result == string.Empty)
        {
            var body = await page.QuerySelectorAsync(BodySelector);
            string bodyText = body != null ? await body.InnerTextAsync() : string.Empty;
            result = bodyText.Trim();
        }

        return result;
    }

    /// <summary>
    ///     Identify the content frame in a frameset page.
    ///     Scores each child frame by text-to-link ratio and content length.
    ///     Navigation frames are mostly links; content frames are mostly text.
    /// </summary>
    private static async Task<IFrame?> FindContentFrameAsync(IPage page)
    {
        IFrame? bestFrame = null;
        var bestScore = 0f;

        // Skip frame[0] â€” it's the main page itself
        foreach(var frame in page.Frames.Where(f => f != page.MainFrame))
        {
            float score = await ScoreFrameAsync(frame);
            if (score > bestScore)
            {
                bestScore = score;
                bestFrame = frame;
            }
        }

        return bestFrame;
    }

    /// <summary>
    ///     Score a frame as a content frame candidate.
    ///     Higher = more likely to be the content frame.
    ///     Uses text length, text-to-link ratio, and name hints.
    /// </summary>
    private static async Task<float> ScoreFrameAsync(IFrame frame)
    {
        var score = 0f;
        try
        {
            var stats = await frame.EvaluateAsync<FrameStats?>(expression: """
                                                                           (() => {
                                                                               const body = document.body;
                                                                               if (!body) return null;
                                                                               const text = body.innerText || '';
                                                                               const links = document.querySelectorAll('a[href]');
                                                                               const linkText = Array.from(links).map(a => a.innerText || '').join('');
                                                                               return {
                                                                                   textLength: text.length,
                                                                                   linkTextLength: linkText.length
                                                                               };
                                                                           })()
                                                                           """
                                                              );

            if (stats != null && stats.TextLength > 0)
            {
                // Text length score (more text = more likely content)
                float textScore = Math.Min(stats.TextLength / 1000f, ContentFrameMaxTextScore);

                // Text-to-link ratio (high ratio = content, low ratio = nav)
                float nonLinkTextLength = stats.TextLength - stats.LinkTextLength;
                float ratioScore = stats.TextLength > 0
                                       ? (nonLinkTextLength / stats.TextLength) * ContentFrameMaxRatioScore
                                       : 0f;

                // Name hints bonus
                var nameBonus = 0f;
                string frameName = frame.Name.ToLowerInvariant();
                string frameUrl = frame.Url.ToLowerInvariant();
                if (smContentFrameHints.Any(h => frameName.Contains(h) || frameUrl.Contains(h)))
                    nameBonus = ContentFrameNameBonus;

                score = textScore + ratioScore + nameBonus;
            }
        }
        catch
        {
            // Frame may be cross-origin or unavailable
        }

        return score;
    }

    private static async Task<string> ExtractTextFromFrameAsync(IFrame frame)
    {
        var result = string.Empty;
        try
        {
            var text = await frame.EvaluateAsync<string>(expression: """
                                                                     (() => {
                                                                         const selectors = ['main', 'article', '[role="main"]',
                                                                             '.content', '.doc-content', '.documentation',
                                                                             '#content', '#main-content'];
                                                                         for (const sel of selectors) {
                                                                             const el = document.querySelector(sel);
                                                                             if (el && el.innerText && el.innerText.trim().length > 100)
                                                                                 return el.innerText.trim();
                                                                         }
                                                                         return document.body ? document.body.innerText.trim() : '';
                                                                     })()
                                                                     """
                                                        );
            result = text;
        }
        catch
        {
            // Frame may be cross-origin
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> ExtractLinksAsync(IPage page)
    {
        var allLinks = new List<string>();

        // Collect links from the main page
        var mainLinks = await ExtractLinksFromFrameAsync(page.MainFrame);
        allLinks.AddRange(mainLinks);

        // Collect links from all child frames (nav frames have the TOC links)
        foreach(var frame in page.Frames.Where(f => f != page.MainFrame))
        {
            var frameLinks = await ExtractLinksFromFrameAsync(frame);
            allLinks.AddRange(frameLinks);
        }

        var result = allLinks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    private static async Task<IReadOnlyList<string>> ExtractLinksFromFrameAsync(IFrame frame)
    {
        IReadOnlyList<string> result = [];
        try
        {
            string[] links = await frame.EvaluateAsync<string[]>(expression: """
                                                                             Array.from(document.querySelectorAll('a[href]'))
                                                                                 .map(a => a.href)
                                                                                 .filter(h => h.startsWith('http'))
                                                                             """
                                                                );
            result = links;
        }
        catch
        {
            // Frame may be cross-origin or detached
        }

        return result;
    }

    /// <summary>
    ///     Expand every collapsible TOC/sidebar node in the page so that
    ///     all nested links become discoverable. Runs multiple iterations
    ///     because expanding a node may reveal more collapsed nodes.
    ///     Handles common patterns: ARIA (aria-expanded="false"), generic
    ///     .collapsed class, and Infragistics ig-tree (ui-igtree-parentnode
    ///     without ui-igtree-expanded).
    /// </summary>
    private static async Task ExpandCollapsibleNavigationAsync(IPage page)
    {
        const int MaxIterations = 10;

        try
        {
            for(var iteration = 0; iteration < MaxIterations; iteration++)
            {
                var expandedCount = await page.EvaluateAsync<int>(expression: """
                                                                              (() => {
                                                                                  const selectors = [
                                                                                      'li.ui-igtree-parentnode:not(.ui-igtree-expanded) > .ui-igtree-expander',
                                                                                      '[aria-expanded="false"]',
                                                                                      '.toc-item.collapsed > .toggle, .tree-node.collapsed > .toggle'
                                                                                  ];
                                                                                  let clicked = 0;
                                                                                  for (const sel of selectors) {
                                                                                      const els = document.querySelectorAll(sel);
                                                                                      els.forEach(el => {
                                                                                          try {
                                                                                              el.click();
                                                                                              clicked++;
                                                                                          } catch (e) {}
                                                                                      });
                                                                                  }
                                                                                  return clicked;
                                                                              })()
                                                                              """
                                                                 );

                if (expandedCount == 0)
                    break;

                await Task.Delay(CollapsibleExpansionDelayMs);
            }
        }
        catch
        {
            // Best effort â€” if expansion fails, the crawl still proceeds with
            // whatever links are currently visible.
        }
    }

    private static bool IsAllowed(string url, ScrapeJob job)
    {
        var regexTimeout = TimeSpan.FromMilliseconds(RegexTimeoutMs);

        bool isBinary = smBinaryExtensionPatterns.Any(pattern =>
                                                          SafeRegexIsMatch(url, pattern, regexTimeout)
                                                     );

        bool result = !isBinary;

        if (result)
        {
            bool allowed = job.AllowedUrlPatterns.Any(pattern =>
                                                          SafeRegexIsMatch(url, pattern, regexTimeout)
                                                     );
            result = allowed;
        }

        if (result)
        {
            bool excluded = job.ExcludedUrlPatterns.Any(pattern =>
                                                            SafeRegexIsMatch(url, pattern, regexTimeout)
                                                       );
            result = !excluded;
        }

        return result;
    }

    /// <summary>
    ///     Regex.IsMatch with a timeout, treating RegexMatchTimeoutException as non-match.
    /// </summary>
    private static bool SafeRegexIsMatch(string input, string pattern, TimeSpan timeout)
    {
        bool result;
        try
        {
            result = Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, timeout);
        }
        catch(RegexMatchTimeoutException)
        {
            result = false;
        }

        return result;
    }

    /// <summary>
    ///     Try each known extension (.html, .htm, .aspx) on a 404 URL.
    ///     Returns updated page, response, and fetch URL.
    /// </summary>
    private async Task<(IPage Page, IResponse? Response, string FetchUrl)>
        RetryWithExtensionsAsync(string url, IPage page, IBrowser browser, CancellationToken ct)
    {
        string fetchUrl = url;
        IResponse? response = null;

        foreach (string ext in smExtensionsToStrip)
        {
            string retryUrl = url + ext;
            mLogger.LogDebug("Got 404 for {Url}, retrying with {Ext}: {RetryUrl}", url, ext, retryUrl);
            await page.CloseAsync();
            page = await browser.NewPageAsync();
            response = await NavigateAndPreparePageAsync(page, retryUrl, ct);

            if (response != null && response.Ok)
            {
                mSiteExtension = ext;
                fetchUrl = retryUrl;
                mLogger.LogInformation("Site requires {Ext} extensions, switching to extension-preserving mode", ext);
                break;
            }
        }

        return (page, response, fetchUrl);
    }

    private static string? NormalizeUrl(string url, bool keepExtension = false)
    {
        string? result;
        try
        {
            var uri = new Uri(url);
            string path = uri.AbsolutePath.TrimEnd(trimChar: '/');

            if (!keepExtension)
            {
                string? strippedExtension = smExtensionsToStrip
                    .FirstOrDefault(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

                if (strippedExtension != null)
                    path = path[..^strippedExtension.Length];
            }

            var normalized = $"{uri.Scheme}://{uri.Host}{path}";
            if (!string.IsNullOrEmpty(uri.Query))
                normalized += uri.Query;
            result = normalized;
        }
        catch(UriFormatException)
        {
            result = null;
        }

        return result;
    }

    private static string ComputeHash(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        string result = Convert.ToHexStringLower(bytes);
        return result;
    }

    private const int PageTimeoutMs = 30000;
    private const int LoadStateTimeoutMs = 5000;
    private const int FrameDiscoveryTimeoutMs = 2000;
    private const int FrameDiscoveryPollDelayMs = 100;
    private const int StableFramePollCount = 2;
    private const int CollapsibleExpansionDelayMs = 200;
    private const int SamplePendingUrlCount = 30;
    private const int SamplePageLimit = 50;
    private const int RegexTimeoutMs = 100;
    private const int ErrorMessageMaxLength = 200;
    private const int HttpNotFound = 404;

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private const int ContentFrameMinChars = 200;
    private const float ContentFrameMaxTextScore = 5f;
    private const float ContentFrameMaxRatioScore = 5f;
    private const float ContentFrameNameBonus = 3f;

    private static readonly string[] smContentFrameHints =
            [FrameHintContent, FrameHintMain, FrameHintTopic, FrameHintBody, FrameHintDetail, FrameHintArticle];

    /// <summary>
    ///     File extensions that aren't web pages. Playwright can't render them
    ///     and the content extractor would get nothing useful anyway.
    ///     Applied automatically in addition to ScrapeJob.ExcludedUrlPatterns.
    /// </summary>
    private static readonly string[] smBinaryExtensionPatterns =
        [
            @"\.pdf(\?|$)", @"\.zip(\?|$)", @"\.tar(\?|$)", @"\.gz(\?|$)", @"\.7z(\?|$)",
            @"\.exe(\?|$)", @"\.dmg(\?|$)", @"\.msi(\?|$)",
            @"\.png(\?|$)", @"\.jpg(\?|$)", @"\.jpeg(\?|$)", @"\.gif(\?|$)",
            @"\.svg(\?|$)", @"\.webp(\?|$)", @"\.ico(\?|$)", @"\.bmp(\?|$)",
            @"\.mp3(\?|$)", @"\.mp4(\?|$)", @"\.webm(\?|$)", @"\.mov(\?|$)",
            @"\.woff(\?|$)", @"\.woff2(\?|$)", @"\.ttf(\?|$)", @"\.eot(\?|$)",
            @"\.css(\?|$)", @"\.js(\?|$)", @"\.map(\?|$)"
        ];

    private static readonly string[] smExtensionsToStrip = [ExtensionHtml, ExtensionHtm, ExtensionAspx];
    private const string InScopeLabel = "scope";
    private const string OutOfScopeLabel = "out";
    private const string MainPageLoadLabel = "main page load";
    private const string BodySelector = "body";

    private const string FrameHintContent = "content";
    private const string FrameHintMain = "main";
    private const string FrameHintTopic = "topic";
    private const string FrameHintBody = "body";
    private const string FrameHintDetail = "detail";
    private const string FrameHintArticle = "article";

    private const string ExtensionHtml = ".html";
    private const string ExtensionHtm = ".htm";
    private const string ExtensionAspx = ".aspx";

}
