// -----------------------------------------------------------------------
// <copyright file="GroupNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions
{
    using System.ComponentModel.DataAnnotations;
    using ExecutionEngine.Enums;
    using ExecutionEngine.Workflow;
    using ValidationResult = System.ComponentModel.DataAnnotations.ValidationResult;

    public class GroupNodeDefinition : NodeDefinition
    {
        public override RuntimeType RuntimeType => RuntimeType.Group;

        public List<NodeDefinition>? ChildNodes { get; set; }

        public List<NodeConnection>? ChildConnections { get; set; }

        public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Sequential;

        /// <summary>
        /// Scoped variables available only to child nodes.
        /// Each entry has a name, type, and default value.
        /// </summary>
        public List<ScopedVariable>? ScopedVariables { get; set; }

        public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (this.ChildNodes?.Any() != true)
            {
                yield return new ValidationResult(
                    "Group node must have at least one child node.",
                    new[] { nameof(this.ChildNodes) });
            }
        }
    }

    /// <summary>
    /// A scoped variable with name, type, and default value.
    /// </summary>
    public class ScopedVariable
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "string";

        [System.Text.Json.Serialization.JsonPropertyName("default")]
        public string Default { get; set; } = string.Empty;
    }
}