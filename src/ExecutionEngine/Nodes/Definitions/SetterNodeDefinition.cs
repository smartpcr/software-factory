// -----------------------------------------------------------------------
// <copyright file="SetterNodeDefinition.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.Nodes.Definitions
{
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using ExecutionEngine.Enums;

    public class SetterNodeDefinition : NodeDefinition
    {
        public override RuntimeType RuntimeType => RuntimeType.Setter;

        /// <summary>
        /// Maps global variable names to binding expressions resolved from upstream output.
        /// Key = global variable name, Value = binding expression (e.g. "input.fieldName").
        /// </summary>
        public Dictionary<string, string>? VariableMappings { get; set; }

        public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            yield break;
        }
    }
}