// CategoryAwareChunker.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DocRAG.Core.Enums;
using DocRAG.Core.Models;
using DocRAG.Ingestion.Symbols;

#endregion

namespace DocRAG.Ingestion.Chunking;

/// <summary>
///     Splits pages into retrieval-sized chunks using category-appropriate strategies.
///     Samples stay whole, HowTos split at headings, ApiReference splits per class, etc.
///     Each chunk is enriched with extracted Symbols[] and a primary QualifiedName via
///     SymbolExtractor.
/// </summary>
public class CategoryAwareChunker
{
    public CategoryAwareChunker(SymbolExtractor symbolExtractor)
    {
        mSymbolExtractor = symbolExtractor;
    }

    private readonly SymbolExtractor mSymbolExtractor;

    /// <summary>
    ///     Chunk a page into one or more DocChunk records (without embeddings).
    ///     If libraryProfile is supplied, identifier-aware extraction runs against it;
    ///     otherwise the extractor falls back to shape-only rules with no LikelySymbols
    ///     boost (still produces useful Symbols[] for declared / structured identifiers).
    /// </summary>
    public IReadOnlyList<DocChunk> Chunk(PageRecord page, LibraryProfile? libraryProfile = null)
    {
        ArgumentNullException.ThrowIfNull(page);

        var initialChunks = page.Category switch
            {
                DocCategory.Sample => ChunkAsSample(page),
                DocCategory.Code => ChunkAsSample(page),
                DocCategory.HowTo => ChunkByHeadings(page),
                DocCategory.ApiReference => ChunkByHeadings(page),
                DocCategory.Overview => ChunkByHeadings(page),
                DocCategory.ChangeLog => ChunkByVersionBoundaries(page),
                var _ => ChunkByHeadings(page)
            };

        var capped = EnforceChunkSizeLimit(initialChunks);
        var profile = libraryProfile ?? smEmptyProfile;
        var enriched = capped.Select(c => EnrichWithSymbols(c, profile)).ToList();
        return enriched;
    }

    private DocChunk EnrichWithSymbols(DocChunk chunk, LibraryProfile profile)
    {
        var extracted = mSymbolExtractor.Extract(chunk.Content, profile);
        var result = chunk with
                         {
                             Symbols = extracted.Symbols,
                             QualifiedName = extracted.PrimaryQualifiedName ?? chunk.QualifiedName,
                             ParserVersion = ParserVersionInfo.Current
                         };
        return result;
    }

    private static IReadOnlyList<DocChunk> EnforceChunkSizeLimit(IReadOnlyList<DocChunk> chunks)
    {
        var result = new List<DocChunk>();
        foreach(var chunk in chunks)
        {
            if (chunk.Content.Length <= MaxChunkChars)
                result.Add(chunk);
            else
            {
                var pieces = SplitToCharLimit(chunk.Content, MaxChunkChars);
                var subIndex = 0;
                foreach(var piece in pieces)
                {
                    result.Add(chunk with
                                   {
                                       Id = $"{chunk.Id}-{subIndex}",
                                       Content = piece
                                   }
                              );
                    subIndex++;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> SplitToCharLimit(string content, int maxChars)
    {
        var pieces = new List<string>();
        var pos = 0;

        while (pos < content.Length)
        {
            int remaining = content.Length - pos;
            int take = Math.Min(maxChars, remaining);

            if (take < remaining)
            {
                int breakPos = FindSentenceBreak(content, pos, take, maxChars);
                if (breakPos > pos + (maxChars / 2))
                    take = breakPos - pos + 1;
            }

            pieces.Add(content.Substring(pos, take).Trim());
            pos += take;
        }

        return pieces;
    }

    /// <summary>
    ///     Find the latest sentence-end character in <c>[pos, pos+take-1]</c>.
    ///     A '.' only counts as a sentence end when followed by whitespace
    ///     or end-of-content — otherwise it could be the dotted-identifier
    ///     separator in <c>AxisFault.Disabled</c>, splitting the identifier
    ///     and corrupting the symbol extractor's view of the chunk.
    ///     '\n', '!', '?' are unconditional sentence ends.
    /// </summary>
    private static int FindSentenceBreak(string content, int pos, int take, int maxChars)
    {
        var minBreak = pos + (maxChars / 2);
        var result = -1;
        for (int j = pos + take - 1; j > minBreak && result < 0; j--)
        {
            var c = content[j];
            var isHardBreak = c == '\n' || c == '!' || c == '?';
            var isSentencePeriod = c == '.'
                                && (j + 1 >= content.Length || char.IsWhiteSpace(content[j + 1]));
            if (isHardBreak || isSentencePeriod)
                result = j;
        }
        return result;
    }

    private IReadOnlyList<DocChunk> ChunkAsSample(PageRecord page)
    {
        var chunks = new List<DocChunk>();
        if (EstimateTokens(page.RawContent) <= MaxChunkTokenEstimate)
            chunks.Add(CreateChunk(page, page.RawContent, page.Title));
        else
        {
            var sections = SplitLargeContent(page.RawContent);
            var index = 0;
            foreach(var section in sections)
            {
                chunks.Add(CreateChunk(page, section, $"{page.Title} (part {index + 1})", index));
                index++;
            }
        }

        return chunks;
    }

    private IReadOnlyList<DocChunk> ChunkByHeadings(PageRecord page)
    {
        var sections = SplitAtHeadings(page.RawContent);
        var chunks = new List<DocChunk>();
        var index = 0;

        foreach((var heading, var content) in sections.Where(s => !string.IsNullOrWhiteSpace(s.Content)))
        {
            var sectionPath = string.IsNullOrEmpty(heading) ? page.Title : $"{page.Title} > {heading}";
            chunks.Add(CreateChunk(page, content, sectionPath, index));
            index++;
        }

        if (chunks.Count == 0)
            chunks.Add(CreateChunk(page, page.RawContent, page.Title));

        return chunks;
    }

    private IReadOnlyList<DocChunk> ChunkByVersionBoundaries(PageRecord page)
    {
        var versionPattern = new Regex(@"^#{1,3}\s+[vV](?:ersion)?\s*\d", RegexOptions.Multiline);
        var matches = versionPattern.Matches(page.RawContent);

        IReadOnlyList<DocChunk> result;
        if (matches.Count == 0)
            result = ChunkByHeadings(page);
        else
        {
            var chunks = new List<DocChunk>();
            for(var i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : page.RawContent.Length;
                var section = page.RawContent[start..end].Trim();
                if (!string.IsNullOrWhiteSpace(section))
                    chunks.Add(CreateChunk(page, section, $"{page.Title} > {matches[i].Value.Trim()}", i));
            }

            result = chunks;
        }

        return result;
    }

    private static DocChunk CreateChunk(PageRecord page, string content, string sectionPath, int index = 0)
    {
        var urlHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(page.Url)))[..12];

        var chunk = new DocChunk
                        {
                            Id = $"{page.LibraryId}/{page.Version}/{urlHash}/{index}",
                            LibraryId = page.LibraryId,
                            Version = page.Version,
                            PageUrl = page.Url,
                            PageTitle = page.Title,
                            Category = page.Category,
                            Content = content.Trim(),
                            SectionPath = sectionPath,
                            ParserVersion = ParserVersionInfo.Current
                        };
        return chunk;
    }

    private static IReadOnlyList<(string Heading, string Content)> SplitAtHeadings(string text)
    {
        var headingPattern = new Regex(@"^(#{1,3})\s+(.+)$", RegexOptions.Multiline);
        var matches = headingPattern.Matches(text);

        IReadOnlyList<(string Heading, string Content)> result;
        if (matches.Count == 0)
            result = [(string.Empty, text)];
        else
        {
            var sections = new List<(string Heading, string Content)>();

            if (matches[i: 0].Index > 0)
            {
                var preamble = text[..matches[i: 0].Index].Trim();
                if (!string.IsNullOrWhiteSpace(preamble))
                    sections.Add((string.Empty, preamble));
            }

            for(var i = 0; i < matches.Count; i++)
            {
                var heading = matches[i].Groups[groupnum: 2].Value.Trim();
                int contentStart = matches[i].Index + matches[i].Length;
                int contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
                var content = text[contentStart..contentEnd].Trim();
                sections.Add((heading, content));
            }

            result = sections;
        }

        return result;
    }

    private static IReadOnlyList<string> SplitLargeContent(string content)
    {
        var paragraphs = content.Split([DoubleNewline, DoubleCrLf], StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach(var paragraph in paragraphs)
        {
            if (EstimateTokens(current + paragraph) > MaxChunkTokenEstimate && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            current.AppendLine(paragraph);
            current.AppendLine();
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().Trim());

        return chunks;
    }

    private static int EstimateTokens(string text)
    {
        const int CharsPerToken = 4;
        var estimate = text.Length / CharsPerToken;
        return estimate;
    }

    private const int MaxChunkTokenEstimate = 800;
    private const int CharsPerToken = 3;
    private const int MaxChunkChars = MaxChunkTokenEstimate * CharsPerToken;

    private const string DoubleNewline = "\n\n";
    private const string DoubleCrLf = "\r\n\r\n";

    // Empty profile used when caller does not supply one. The extractor still
    // produces useful Symbols[] via shape-based rules (declared form, internal
    // structure, callable shape) — just without the LikelySymbols boost.
    private static readonly LibraryProfile smEmptyProfile = new()
                                                                {
                                                                    Id = "default/default",
                                                                    LibraryId = "default",
                                                                    Version = "default",
                                                                    Source = "empty"
                                                                };
}
