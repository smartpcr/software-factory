// -----------------------------------------------------------------------
// <copyright file="TransformHelpers.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Core;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Predefined static transform functions available in Transform node expressions.
/// These are loaded into the Roslyn script context so users can call them directly.
/// </summary>
public static class TransformHelpers
{
    /// <summary>Aggregate multiple key-value pairs into a dictionary.</summary>
    public static Dictionary<string, object?> Aggregate(params (string Key, object? Value)[] fields)
    {
        var result = new Dictionary<string, object?>();
        foreach (var (key, value) in fields)
        {
            result[key] = value;
        }

        return result;
    }

    /// <summary>Parse a JSON string into a dynamic object.</summary>
    public static object? ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>Parse a JSON string into a list of objects (for JSON arrays).</summary>
    public static List<JsonElement> ParseJsonArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var arr = JsonSerializer.Deserialize<JsonElement>(json);
        if (arr.ValueKind != JsonValueKind.Array) return [arr];
        return arr.EnumerateArray().ToList();
    }

    /// <summary>Parse CSV text into a list of dictionaries (header row → keys).</summary>
    public static List<Dictionary<string, string>> ParseCsv(string csv, string delimiter = ",")
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return [];

        var headers = SplitCsvLine(lines[0].TrimEnd('\r'), delimiter);
        var result = new List<Dictionary<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var values = SplitCsvLine(lines[i].TrimEnd('\r'), delimiter);
            var row = new Dictionary<string, string>();
            for (var j = 0; j < headers.Length && j < values.Length; j++)
            {
                row[headers[j]] = values[j];
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary>Read a text file and return its content.</summary>
    public static string ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var normalized = path.Replace('\\', '/');
        var fullPath = Path.GetFullPath(normalized);
        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
    }

    /// <summary>Read a JSON file and parse it.</summary>
    public static object? ReadJsonFile(string path)
    {
        var content = ReadFile(path);
        return string.IsNullOrEmpty(content) ? null : ParseJson(content);
    }

    /// <summary>Read a CSV file and parse it into rows.</summary>
    public static List<Dictionary<string, string>> ReadCsvFile(string path, string delimiter = ",")
    {
        var content = ReadFile(path);
        return ParseCsv(content, delimiter);
    }

    /// <summary>Extract named groups from a string using a regex pattern.</summary>
    public static Dictionary<string, string> ExtractRegex(string input, string pattern)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern)) return result;
        var match = Regex.Match(input, pattern);
        if (!match.Success) return result;
        foreach (var groupName in match.Groups.Keys)
        {
            if (groupName != "0")
            {
                result[groupName] = match.Groups[groupName].Value;
            }
        }

        return result;
    }

    /// <summary>Extract all matches from a string using a regex pattern.</summary>
    public static List<string> ExtractAll(string input, string pattern)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern)) return [];
        return Regex.Matches(input, pattern).Select(m => m.Value).ToList();
    }

    /// <summary>Split a string by delimiter and return trimmed parts.</summary>
    public static List<string> Split(string input, string delimiter = ",")
    {
        if (string.IsNullOrEmpty(input)) return [];
        return input.Split(delimiter).Select(s => s.Trim()).ToList();
    }

    /// <summary>Select a field from each item in a collection.</summary>
    public static List<object?> Pluck(IEnumerable<object> items, string fieldName)
    {
        var result = new List<object?>();
        foreach (var item in items)
        {
            if (item is Dictionary<string, object> dict)
            {
                result.Add(dict.TryGetValue(fieldName, out var v) ? v : null);
            }
            else if (item is Dictionary<string, string> strDict)
            {
                result.Add(strDict.TryGetValue(fieldName, out var v) ? v : null);
            }
            else if (item is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                result.Add(je.TryGetProperty(fieldName, out var prop) ? (object)prop : null);
            }
        }

        return result;
    }

    private static string[] SplitCsvLine(string line, string delimiter)
    {
        // Simple CSV split handling quoted fields
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && line.AsSpan(i).StartsWith(delimiter))
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                i += delimiter.Length - 1;
            }
            else
            {
                current.Append(line[i]);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields.ToArray();
    }
}
