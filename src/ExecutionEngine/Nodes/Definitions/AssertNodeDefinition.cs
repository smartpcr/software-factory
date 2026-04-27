// -----------------------------------------------------------------------
// <copyright file="AssertNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions
{
    using System.ComponentModel.DataAnnotations;
    using ExecutionEngine.Enums;

    /// <summary>
    /// Definition for an Assert node that evaluates a condition and fails if it is not met.
    /// </summary>
    public class AssertNodeDefinition : NodeDefinition
    {
        public override RuntimeType RuntimeType => RuntimeType.Assert;

        /// <summary>
        /// Gets or sets the condition expression to evaluate.
        /// Supports binding placeholders: <c>{fieldName} != ""</c>, <c>{count} > 0</c>.
        /// </summary>
        public string Condition { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the error message to use when the assertion fails.
        /// Supports binding placeholders.
        /// </summary>
        public string? Message { get; set; }

        public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(this.Condition))
            {
                yield return new ValidationResult("Condition is required.", new[] { nameof(this.Condition) });
            }
        }
    }
}
