// -----------------------------------------------------------------------
// <copyright file="OutputWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Chat.Helpers;

using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

/// <summary>
/// Splits a markdown response into sections and writes each to a separate file
/// under PromptOutput/{modelName}/{questionSlug}/.
/// </summary>
public static partial class OutputWriter
{
    /// <summary>
    /// Splits the response by top-level markdown headers (##) and writes each section
    /// to a numbered .md file under PromptOutput/{modelName}/{questionSlug}/.
    /// </summary>
    /// <param name="response">The full markdown response text.</param>
    /// <param name="modelName">The model ID used for the response.</param>
    /// <param name="questionSlug">The question slug (e.g., "02-feature-inventory").</param>
    public static void SaveQuestionOutput(string response, string modelName, string questionSlug)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return;
        }

        var safeModel = SanitizePathSegment(modelName);
        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "PromptOutput", safeModel, questionSlug);
        Directory.CreateDirectory(outputDir);

        var sections = SplitIntoSections(response);

        if (sections.Count == 0)
        {
            return;
        }

        // If only one section (no ## headers found), write the whole response as a single file
        if (sections.Count == 1)
        {
            var fileName = $"01-{questionSlug}.md";
            var filePath = Path.Combine(outputDir, fileName);
            File.WriteAllText(filePath, sections[0].Content.Trim());
            AnsiConsole.MarkupLine($"[green]📄 Saved:[/] {Markup.Escape(filePath)}");
            return;
        }

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var number = (i + 1).ToString("D2");
            var slug = ToSlug(section.Title);
            var fileName = $"{number}-{slug}.md";
            var filePath = Path.Combine(outputDir, fileName);

            File.WriteAllText(filePath, section.Content.Trim());
        }

        AnsiConsole.MarkupLine($"[green]📂 Saved {sections.Count} files to:[/] {Markup.Escape(outputDir)}");
    }

    /// <summary>
    /// Splits markdown text into sections based on ## headers.
    /// Content before the first ## header becomes a section titled "overview".
    /// </summary>
    /// <param name="markdown">The markdown text to split.</param>
    /// <returns>A list of sections with titles and content.</returns>
    internal static List<Section> SplitIntoSections(string markdown)
    {
        var sections = new List<Section>();
        var lines = markdown.Split('\n');
        var currentTitle = string.Empty;
        var currentContent = new StringBuilder();

        foreach (var line in lines)
        {
            var match = SectionHeaderRegex().Match(line);
            if (match.Success)
            {
                // Save previous section
                if (currentContent.Length > 0)
                {
                    var title = string.IsNullOrEmpty(currentTitle) ? "overview" : currentTitle;
                    sections.Add(new Section(title, currentContent.ToString()));
                }

                currentTitle = match.Groups[1].Value.Trim();
                currentContent.Clear();
                currentContent.AppendLine(line);
            }
            else
            {
                currentContent.AppendLine(line);
            }
        }

        // Save last section
        if (currentContent.Length > 0)
        {
            var title = string.IsNullOrEmpty(currentTitle) ? "overview" : currentTitle;
            sections.Add(new Section(title, currentContent.ToString()));
        }

        return sections;
    }

    /// <summary>
    /// Converts a section title to a file-name-safe slug.
    /// </summary>
    /// <param name="title">The section title.</param>
    /// <returns>A lowercase, hyphen-separated slug.</returns>
    internal static string ToSlug(string title)
    {
        var slug = title.ToLowerInvariant();
        // Replace non-alphanumeric chars (except hyphens) with hyphens
        slug = InvalidSlugCharsRegex().Replace(slug, "-");
        // Collapse multiple hyphens
        slug = MultipleHyphensRegex().Replace(slug, "-");
        slug = slug.Trim('-');

        // Limit length
        if (slug.Length > 60)
        {
            slug = slug[..60].TrimEnd('-');
        }

        return string.IsNullOrEmpty(slug) ? "section" : slug;
    }

    /// <summary>
    /// Removes characters that are invalid in file/directory names.
    /// </summary>
    /// <param name="segment">The path segment to sanitize.</param>
    /// <returns>A safe path segment.</returns>
    private static string SanitizePathSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^##\s+(.+)$")]
    private static partial Regex SectionHeaderRegex();

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex InvalidSlugCharsRegex();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex MultipleHyphensRegex();

    /// <summary>
    /// A parsed markdown section.
    /// </summary>
    /// <param name="Title">Section heading text.</param>
    /// <param name="Content">Full section content including the header line.</param>
    internal record Section(string Title, string Content);
}