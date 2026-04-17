// -----------------------------------------------------------------------
// <copyright file="TsWriter.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Emitters;

using System.Text;

/// <summary>
/// Helper for writing TypeScript output with LF line endings and consistent indentation.
/// </summary>
public sealed class TsWriter
{
    private readonly StringBuilder sb = new();
    private int indentLevel;

    /// <summary>
    /// Writes a line with the current indentation, using LF line ending.
    /// </summary>
    public void Line(string text = "")
    {
        if (string.IsNullOrEmpty(text))
        {
            this.sb.Append('\n');
        }
        else
        {
            this.sb.Append(new string(' ', this.indentLevel * 2));
            this.sb.Append(text);
            this.sb.Append('\n');
        }
    }

    /// <summary>
    /// Increases indentation.
    /// </summary>
    public void Indent() => this.indentLevel++;

    /// <summary>
    /// Decreases indentation.
    /// </summary>
    public void Dedent() => this.indentLevel = Math.Max(0, this.indentLevel - 1);

    /// <inheritdoc/>
    public override string ToString() => this.sb.ToString();
}
