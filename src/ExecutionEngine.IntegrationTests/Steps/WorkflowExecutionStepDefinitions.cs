// -----------------------------------------------------------------------
// <copyright file="WorkflowExecutionStepDefinitions.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace ExecutionEngine.IntegrationTests.Steps
{
    using ExecutionEngine.Enums;
    using ExecutionEngine.Nodes.Definitions;
    using ExecutionEngine.Workflow;
    using Reqnroll;

    [Binding]
    public class WorkflowExecutionStepDefinitions
    {
        private readonly ScenarioContext context;
        private readonly IReqnrollOutputHelper outputWriter;
        private readonly WorkflowDefinition workflowDefinition;

        public WorkflowExecutionStepDefinitions(ScenarioContext context, IReqnrollOutputHelper outputWriter)
        {
            this.context = context;
            this.outputWriter = outputWriter;
            this.workflowDefinition = new WorkflowDefinition()
            {
                WorkflowName = "TestWorkflow",
                Description = "Test workflow",
                Nodes = [],
                Connections = []
            };
        }

        [Given("I have a workflow with nodes {string}, {string}, {string}")]
        public void GivenIHaveAWorkflowWithNodes(string nodeA, string nodeB, string nodeC)
        {
            // create enumerable with node names
            var nodeNames = new[] { nodeA, nodeB, nodeC };
            foreach (var nodeName in nodeNames)
            {
                this.workflowDefinition.Nodes.Add(new NoopNodeDefinition()
                {
                    NodeId = nodeName,
                    NodeName = nodeName,
                });
            }
        }

        [Given("{string} connects to {string} on Complete")]
        public void GivenConnectsToOnComplete(string sourceNode, string targetNode)
        {
            this.workflowDefinition.Connections.Add(new NodeConnection()
            {
                SourceNodeId = sourceNode,
                TargetNodeId = targetNode,
                TriggerMessageType = MessageType.Complete
            });
        }

        [When("I start the workflow")]
        public static void WhenIStartTheWorkflow()
        {
            throw new PendingStepException();
        }

        [Then("the workflow should complete successfully")]
        public static void ThenTheWorkflowShouldCompleteSuccessfully()
        {
            throw new PendingStepException();
        }

        [Then("nodes should execute in order {string}, {string}, {string}")]
        public static void ThenNodesShouldExecuteInOrder(string nodeA, string nodeB, string nodeC)
        {
            throw new PendingStepException();
        }

        [Then("all nodes should have status {string}")]
        public static void ThenAllNodesShouldHaveStatus(string completed)
        {
            throw new PendingStepException();
        }

        [Then("the workflow duration should be greater than zero")]
        public static void ThenTheWorkflowDurationShouldBeGreaterThanZero()
        {
            throw new PendingStepException();
        }

        [Given("I have a workflow with nodes {string}, {string}, {string}, {string}, {string}")]
        public static void GivenIHaveAWorkflowWithNodes(string node1, string node2, string node3, string node4, string node5)
        {
            throw new PendingStepException();
        }

        [Then("{string}, {string}, {string} should execute in parallel")]
        public static void ThenShouldExecuteInParallel(string parallelA, string parallelB, string parallelC)
        {
            throw new PendingStepException();
        }

        [Then("{string} should execute after all parallel nodes complete")]
        public static void ThenShouldExecuteAfterAllParallelNodesComplete(string join)
        {
            throw new PendingStepException();
        }

        [Given("I have a workflow with nodes {string}, {string}, {string}, {string}, {string}, {string}")]
        public static void GivenIHaveAWorkflowWithNodes(string start, string p1, string p2, string join, string sequential, string end)
        {
            throw new PendingStepException();
        }


        [Then("{string}, {string} should execute in parallel")]
        public static void ThenShouldExecuteInParallel(string p0, string p1)
        {
            throw new PendingStepException();
        }

        [Then("{string} should wait for both {string} and {string}")]
        public static void ThenShouldWaitForBothAnd(string join, string p1, string p2)
        {
            throw new PendingStepException();
        }

        [Then("{string} should execute after {string}")]
        public static void ThenShouldExecuteAfter(string laterNode, string earlierNode)
        {
            throw new PendingStepException();
        }

        [Given("I have a workflow with nodes {string}, {string}, {string}, {string}")]
        public static void GivenIHaveAWorkflowWithNodes(string p0, string p1, string nodeA, string nodeB)
        {
            throw new PendingStepException();
        }


        [Then("{string} and {string} should be identified as entry points")]
        public static void ThenAndShouldBeIdentifiedAsEntryPoints(string p0, string p1)
        {
            throw new PendingStepException();
        }

        [Then("both entry points should receive initial trigger")]
        public static void ThenBothEntryPointsShouldReceiveInitialTrigger()
        {
            throw new PendingStepException();
        }

        [Then("all target nodes should receive the completion message")]
        public static void ThenAllTargetNodesShouldReceiveTheCompletionMessage()
        {
            throw new PendingStepException();
        }

        [Then("all targets should execute in parallel")]
        public static void ThenAllTargetsShouldExecuteInParallel()
        {
            throw new PendingStepException();
        }

        [Given("I have a valid acyclic workflow")]
        public static void GivenIHaveAValidAcyclicWorkflow()
        {
            throw new PendingStepException();
        }

        [Then("no infinite loops should occur")]
        public static void ThenNoInfiniteLoopsShouldOccur()
        {
            throw new PendingStepException();
        }

        [Then("all reachable nodes should execute")]
        public static void ThenAllReachableNodesShouldExecute()
        {
            throw new PendingStepException();
        }

        [Given("I have a long-running workflow")]
        public static void GivenIHaveALong_RunningWorkflow()
        {
            throw new PendingStepException();
        }

        [When("I cancel the workflow after {int} milliseconds")]
        public static void WhenICancelTheWorkflowAfterMilliseconds(int p0)
        {
            throw new PendingStepException();
        }

        [Then("the workflow status should be {string}")]
        public static void ThenTheWorkflowStatusShouldBe(string cancelled)
        {
            throw new PendingStepException();
        }

        [Then("some nodes may not have executed")]
        public static void ThenSomeNodesMayNotHaveExecuted()
        {
            throw new PendingStepException();
        }

        [Then("executed nodes should have their state preserved")]
        public static void ThenExecutedNodesShouldHaveTheirStatePreserved()
        {
            throw new PendingStepException();
        }
    }
}