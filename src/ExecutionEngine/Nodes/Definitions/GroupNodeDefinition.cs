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
}