// GitHubRepoScraper.cs
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

#endregion

namespace DocRAG.Ingestion.Crawling;

/// <summary>
///     Scrapes a GitHub repository by cloning it shallowly with git CLI,
///     walking the working tree, and converting each relevant file to a
///     PageRecord. Captures README files, /docs content, code samples,
///     and changelog files. Skips build artifacts, vendor directories,
///     and binary files.
/// </summary>
public class GitHubRepoScraper
{
    public GitHubRepoScraper(IPageRepository pageRepository, ILogger<GitHubRepoScraper> logger)
    {
        mPageRepository = pageRepository;
        mLogger = logger;
    }

    private readonly ILogger<GitHubRepoScraper> mLogger;

    private readonly IPageRepository mPageRepository;

    /// <summary>
    ///     Try to parse a GitHub URL into (owner, repo).
    ///     Accepts forms like:
    ///     https://github.com/owner/repo
    ///     https://github.com/owner/repo/tree/main/path
    ///     https://github.com/owner/repo/blob/main/file.cs
    /// </summary>
#pragma warning disable STR0010 // out parameters cannot be validated
    public static bool TryParseGitHubUrl(string url, out string owner, out string repo)
#pragma warning restore STR0010
    {
        ArgumentException.ThrowIfNullOrEmpty(url);

        owner = string.Empty;
        repo = string.Empty;
        var result = false;

        var match = Regex.Match(url, @"^https?://(?:www\.)?github\.com/([^/]+)/([^/#?]+)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            owner = match.Groups[groupnum: 1].Value;
            repo = match.Groups[groupnum: 2].Value.Replace(GitExtension, string.Empty);
            result = true;
        }

        return result;
    }

    /// <summary>
    ///     Clone the repo and ingest its contents as PageRecords.
    /// </summary>
    public async Task ScrapeRepositoryAsync(string owner,
                                            string repo,
                                            ScrapeJob job,
                                            ChannelWriter<PageRecord>? output = null,
                                            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        ArgumentException.ThrowIfNullOrEmpty(repo);
        ArgumentNullException.ThrowIfNull(job);

        var repoUrl = $"https://github.com/{owner}/{repo}.git";
        var displayUrl = $"https://github.com/{owner}/{repo}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"docrag-gh-{owner}-{repo}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            mLogger.LogInformation("Cloning {Url} into {Dir}", repoUrl, tempDir);

            var cloneOk = await RunGitCloneAsync(repoUrl, tempDir, ct);
            if (cloneOk)
            {
                int filesIngested = await WalkAndIngestAsync(tempDir,
                                                             owner,
                                                             repo,
                                                             displayUrl,
                                                             job,
                                                             output,
                                                             ct
                                                            );
                mLogger.LogInformation("Ingested {Count} files from {Owner}/{Repo}", filesIngested, owner, repo);
            }
            else
                mLogger.LogWarning("Failed to clone {Url}, skipping repo scrape", repoUrl);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private async Task<bool> RunGitCloneAsync(string repoUrl, string targetDir, CancellationToken ct)
    {
        var result = false;
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
                          {
                              FileName = "git",
                              ArgumentList = { "clone", "--depth", "1", "--single-branch", repoUrl, targetDir },
                              UseShellExecute = false,
                              CreateNoWindow = true,
                              RedirectStandardOutput = true,
                              RedirectStandardError = true
                          };

            process = Process.Start(psi);
            if (process != null)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(CloneTimeoutMs);

                await process.WaitForExitAsync(timeoutCts.Token);
                result = process.ExitCode == 0;

                if (!result)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(ct);
                    mLogger.LogWarning("git clone failed: {Stderr}", stderr);
                }
            }
        }
        catch(Exception ex)
        {
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            mLogger.LogError(ex, "git clone failed for {Url}", repoUrl);
        }
        finally
        {
            process?.Dispose();
        }

        return result;
    }

    private async Task<int> WalkAndIngestAsync(string repoDir,
                                               string owner,
                                               string repo,
                                               string displayUrl,
                                               ScrapeJob job,
                                               ChannelWriter<PageRecord>? output,
                                               CancellationToken ct)
    {
        var count = 0;

        var allFiles = EnumerateFiles(repoDir);
        var eligibleFiles = allFiles.Where(f => smTextExtensions.Contains(Path.GetExtension(f)));

        foreach(var filePath in eligibleFiles.TakeWhile(_ => !ct.IsCancellationRequested))
        {
            var ingested = await IngestFileAsync(filePath,
                                                 repoDir,
                                                 owner,
                                                 repo,
                                                 displayUrl,
                                                 job,
                                                 output,
                                                 ct
                                                );
            if (ingested)
                count++;
        }

        return count;
    }

    private async Task<bool> IngestFileAsync(string filePath,
                                             string repoDir,
                                             string owner,
                                             string repo,
                                             string displayUrl,
                                             ScrapeJob job,
                                             ChannelWriter<PageRecord>? output,
                                             CancellationToken ct)
    {
        var result = false;
        var info = new FileInfo(filePath);

        if (info.Length <= MaxFileSizeBytes)
        {
            string? content = null;
            try
            {
                content = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
            }
            catch(Exception ex)
            {
                mLogger.LogDebug(ex, "Failed to read {File}", filePath);
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                var relativePath = Path.GetRelativePath(repoDir, filePath).Replace(oldChar: '\\', newChar: '/');
                var ext = Path.GetExtension(filePath);
                var fileUrl = $"{displayUrl}/blob/HEAD/{relativePath}";
                var category = ClassifyFile(relativePath, ext);
                var title = $"{owner}/{repo}: {relativePath}";

                var contentHash = ComputeHash(content);
                var urlHash = ComputeHash(fileUrl);

                var pageRecord = new PageRecord
                                     {
                                         Id = $"{job.LibraryId}/{job.Version}/{urlHash[..12]}",
                                         LibraryId = job.LibraryId,
                                         Version = job.Version,
                                         Url = fileUrl,
                                         Title = title,
                                         Category = category,
                                         RawContent = content,
                                         FetchedAt = DateTime.UtcNow,
                                         ContentHash = contentHash
                                     };

                await mPageRepository.UpsertPageAsync(pageRecord, ct);
                if (output != null)
                    await output.WriteAsync(pageRecord, ct);
                result = true;
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            ProcessDirectory(dir, stack);

            foreach(var file in GetFilesSafe(dir))
                yield return file;
        }
    }

    private static void ProcessDirectory(string dir, Stack<string> stack)
    {
        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(dir);
        }
        catch
        {
            subdirs = [];
        }

        foreach(var sub in subdirs.Where(d => !smSkipDirectories.Contains(Path.GetFileName(d))))
            stack.Push(sub);
    }

    private static string[] GetFilesSafe(string dir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch
        {
            files = [];
        }

        return files;
    }

    private static DocCategory ClassifyFile(string relativePath, string extension)
    {
        var path = relativePath.ToLowerInvariant();
        var fileName = Path.GetFileName(path);

        var result = true switch
            {
                var _ when IsChangelogFile(fileName) => DocCategory.ChangeLog,
                var _ when fileName.StartsWith(ReadmePrefix) => DocCategory.Overview,
                var _ when IsSamplePath(path) => DocCategory.Sample,
                var _ when IsDocsPath(path) => DocCategory.HowTo,
                var _ when extension is ".md" or ".markdown" or ".rst" or ".adoc" => DocCategory.Overview,
                var _ => DocCategory.Code
            };

        return result;
    }

    private static bool IsChangelogFile(string fileName)
    {
        bool result = smChangelogPrefixes.Any(p => fileName.StartsWith(p)) ||
                      smChangelogExactNames.Contains(fileName);
        return result;
    }

    private static bool IsSamplePath(string path)
    {
        bool result = smSamplePaths.Any(p => path.Contains(p));
        return result;
    }

    private static bool IsDocsPath(string path)
    {
        bool result = smDocsStartPaths.Any(p => path.StartsWith(p)) ||
                      smDocsContainPaths.Any(p => path.Contains(p));
        return result;
    }

    private static string ComputeHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        var result = Convert.ToHexStringLower(bytes);
        return result;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                ResetFileAttributes(path);
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void ResetFileAttributes(string path)
    {
        foreach(var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
            }
        }
    }

    private const int MaxFileSizeKb = 200;
    private const int MaxFileSizeBytes = MaxFileSizeKb * 1024;
    private const int CloneTimeoutMs = 120000;

    private static readonly HashSet<string> smTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                                                   {
                                                                       // Code
                                                                       ".cs", ".csx", ".vb", ".fs", ".fsx",
                                                                       ".ts", ".tsx", ".js", ".jsx", ".mjs",
                                                                       ".py", ".rb", ".go", ".rs", ".java", ".kt",
                                                                       ".kts", ".scala",
                                                                       ".cpp", ".cxx", ".cc", ".hpp", ".hxx", ".hh",
                                                                       ".h", ".c",
                                                                       ".swift", ".m", ".mm", ".dart",
                                                                       ".php", ".pl", ".lua", ".r", ".jl", ".clj",
                                                                       ".cljs", ".ex", ".exs",
                                                                       ".sh", ".bash", ".zsh", ".ps1", ".bat", ".cmd",
                                                                       // Markup / data / config
                                                                       ".md", ".markdown", ".rst", ".adoc", ".txt",
                                                                       ".xml", ".xaml", ".json", ".yaml", ".yml",
                                                                       ".toml", ".ini", ".cfg",
                                                                       ".html", ".htm", ".css", ".scss", ".sass",
                                                                       ".less",
                                                                       ".sql", ".graphql", ".proto"
                                                                   };

    private static readonly HashSet<string> smSkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                                                    {
                                                                        ".git", ".github", ".vs", ".idea", ".vscode",
                                                                        "node_modules", "bower_components", "vendor",
                                                                        "bin", "obj", "build", "dist", "out", "target",
                                                                        "Debug", "Release",
                                                                        "__pycache__", ".pytest_cache", ".mypy_cache",
                                                                        "packages", ".nuget"
                                                                    };

    private const string GitExtension = ".git";
    private const string ReadmePrefix = "readme";

    private const string ChangelogPrefix = "changelog";
    private const string ChangesPrefix = "changes";
    private const string HistoryPrefix = "history";
    private const string ReleasesPrefix = "releases";
    private const string ReleaseNotesHyphen = "release-notes.md";
    private const string ReleaseNotesUnderscore = "release_notes.md";

    private const string SamplesPath = "/samples/";
    private const string SamplePath = "/sample/";
    private const string ExamplesPath = "/examples/";
    private const string ExamplePath = "/example/";
    private const string DemoPath = "/demo/";
    private const string DemosPath = "/demos/";

    private const string DocsSlash = "docs/";
    private const string DocSlash = "doc/";
    private const string SlashDocsSlash = "/docs/";
    private const string SlashDocSlash = "/doc/";

    private static readonly string[] smChangelogPrefixes = [ChangelogPrefix, ChangesPrefix, HistoryPrefix, ReleasesPrefix];
    private static readonly string[] smChangelogExactNames = [ReleaseNotesHyphen, ReleaseNotesUnderscore];
    private static readonly string[] smSamplePaths = [SamplesPath, SamplePath, ExamplesPath, ExamplePath, DemoPath, DemosPath];
    private static readonly string[] smDocsStartPaths = [DocsSlash, DocSlash];
    private static readonly string[] smDocsContainPaths = [SlashDocsSlash, SlashDocSlash];
}
