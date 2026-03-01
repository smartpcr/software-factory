// -----------------------------------------------------------------------
// <copyright file="QuestionLoader.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace CopilotChat.Helpers;

using Spectre.Console;

/// <summary>
/// Loads question prompts from the Questions folder.
/// Files are named NN-title.md where NN is the question number.
/// </summary>
public static class QuestionLoader
{
    /// <summary>
    /// A loaded question with its number, title, slug, and prompt content.
    /// </summary>
    /// <param name="Number">Question number parsed from filename.</param>
    /// <param name="Title">Display title derived from filename.</param>
    /// <param name="Slug">Filename without extension, used for output paths (e.g., "02-feature-inventory").</param>
    /// <param name="Prompt">Full prompt text from file content.</param>
    public record Question(int Number, string Title, string Slug, string Prompt);

    /// <summary>
    /// Loads all questions from the Questions folder, sorted by number.
    /// </summary>
    /// <returns>List of questions, or empty list if folder not found.</returns>
    public static List<Question> LoadQuestions()
    {
        var questionsDir = Path.Combine(AppContext.BaseDirectory, "Questions");
        if (!Directory.Exists(questionsDir))
        {
            return [];
        }

        var questions = new List<Question>();
        var files = Directory.GetFiles(questionsDir, "*.md")
            .OrderBy(f => Path.GetFileName(f));

        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            var dashIndex = fileName.IndexOf('-');
            if (dashIndex <= 0)
            {
                continue;
            }

            var numberPart = fileName[..dashIndex];
            if (!int.TryParse(numberPart, out var number))
            {
                continue;
            }

            var title = fileName[(dashIndex + 1)..]
                .Replace('-', ' ')
                .Replace('_', ' ');
            // Capitalize first letter of each word
            title = string.Join(' ', title.Split(' ')
                .Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));

            var prompt = File.ReadAllText(file).Trim();
            if (!string.IsNullOrEmpty(prompt))
            {
                questions.Add(new Question(number, title, fileName, prompt));
            }
        }

        return questions;
    }

    /// <summary>
    /// Gets a question by its number.
    /// </summary>
    /// <param name="questions">The loaded questions list.</param>
    /// <param name="number">The question number to find.</param>
    /// <returns>The matching question, or null.</returns>
    public static Question? GetByNumber(List<Question> questions, int number)
    {
        return questions.FirstOrDefault(q => q.Number == number);
    }

    /// <summary>
    /// Displays all available questions to the console.
    /// </summary>
    /// <param name="questions">The loaded questions list.</param>
    public static void ShowQuestions(List<Question> questions)
    {
        if (questions.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]   No question files found in Questions/ folder.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[magenta]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.MarkupLine("[magenta]                    📋 Repository Questions                   [/]");
        AnsiConsole.MarkupLine("[magenta]═══════════════════════════════════════════════════════════════[/]");

        foreach (var q in questions)
        {
            AnsiConsole.MarkupLine($"   [yellow]question {q.Number}:[/] {q.Title}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]   Type 'question <number>' to run, e.g., 'question 1'[/]");
        AnsiConsole.MarkupLine("[magenta]═══════════════════════════════════════════════════════════════[/]");
        AnsiConsole.WriteLine();
    }
}