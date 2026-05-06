@incremental-sync
Feature: Incremental Sync
    Keep the memory graph current without full re-extraction by
    detecting changed files via git diff, upserting/retiring nodes
    and edges, propagating stale markers, and triggering expertise decay.

    @modified-file
    Scenario: Modified file sync
        Given an indexed repo with file "src/Services/PaymentService.cs" containing 3 nodes
        And the file is modified so extraction returns 2 nodes with new ContentHash and 1 node is removed
        When ApplyDiffAsync is called from commit "abc123" to "def456"
        Then 2 changed nodes are upserted with new ContentHash and new SinceIndex
        And the removed node is retired via RetireNodeAsync which cascades to its edges
        And SyncResult.FilesChanged is 1
        And SyncResult.NodesAdded is 2
        And SyncResult.NodesRetired is 1

    @deleted-file
    Scenario: Deleted file sync
        Given an indexed file "src/Services/OldService.cs" with 5 nodes and 2 nodes share edge "shared_edge_1"
        When the file is deleted and ApplyDiffAsync runs from "abc123" to "def456"
        Then all 5 nodes have until_index set via RetireNodeAsync
        And all 6 edges connected to file nodes are retired via RetireEdgeAsync
        And SyncResult.NodesRetired is 5
        And SyncResult.EdgesRetired is 6

    @expertise-decay
    Scenario: Expertise decay on change
        Given an expertise entry targeting node "src/Services/PaymentService.cs::PaymentService.processRefund"
        And the expertise has Confidence 1.0
        When the node's ContentHash changes during sync
        Then expertise decay is applied with StaleDecayFactor 0.5
        And SyncResult.ExpertiseDecayed is 1

    @expertise-decay-minor
    Scenario: Expertise minor decay on whitespace-only change
        Given an expertise entry targeting node "src/Services/PaymentService.cs::PaymentService.processRefund" with source code
        And the expertise has Confidence 1.0
        When the node's source code changes by whitespace and comments only during sync
        Then expertise decay is applied with MinorDecayFactor 0.9

    @stale-edge
    Scenario: Stale edge propagation
        Given function A calls function B via a CALLS edge
        When function B is modified during sync
        Then the CALLS edge from A to B has Metadata Stale set to true
        And the CALLS edge from A to B is not retired by cascade

    @semantic-indexer
    Scenario: Semantic indexer receives only changed nodes
        Given an indexed repo with file "src/Services/Svc.cs" containing 3 nodes
        And a semantic indexer is registered
        And the file is modified so only 1 node has a changed ContentHash
        When ApplyDiffAsync is called from commit "abc123" to "def456"
        Then the semantic indexer receives exactly 1 node
        And SyncResult.NodesAdded is 1

    @edge-diff
    Scenario: Modified file retires removed edges for surviving nodes
        Given an indexed repo with file "src/Services/Calc.cs" containing 2 nodes
        And node "src/Services/Calc.cs::Node0" has an outgoing edge "edge_old_outgoing" to "src/Services/Calc.cs::Node1"
        And the file is modified so both nodes survive but edge "edge_old_outgoing" is no longer extracted
        When ApplyDiffAsync is called from commit "abc123" to "def456"
        Then edge "edge_old_outgoing" is retired
        And SyncResult.EdgesRetired is 1
        And SyncResult.EdgesAdded is 0

    @git-diff-failure
    Scenario: Git diff failure returns zero-change SyncResult
        Given an indexed repo with file "src/Services/Svc.cs" containing 1 nodes
        And git diff will fail with an exception
        When ApplyDiffAsync is called from commit "abc123" to "def456"
        Then the sync returns a zero-change SyncResult
        And no graph mutations occurred

    @extraction-error
    Scenario: Empty extraction result retires existing nodes when file becomes empty
        Given an indexed repo with file "src/Services/PaymentService.cs" containing 3 nodes
        And the file is modified but extraction returns zero nodes
        When ApplyDiffAsync is called from commit "abc123" to "def456"
        Then all existing nodes are retired because the file became empty
        And SyncResult.FilesChanged is 1
        And SyncResult.NodesRetired is 3

    @added-file-extraction-error
    Scenario: Added file extraction failure is handled gracefully
        Given git diff reports an added file "src/Services/NewService.cs"
        And extraction will throw an exception for the added file
        When ApplyDiffAsync is called from commit "abc123" to "def456"
        Then no graph mutations occurred
        And SyncResult.FilesChanged is 1

    @renamed-file-extraction-error
    Scenario: Renamed file extraction failure does not retire old path
        Given an indexed file "src/Services/OldName.cs" with 2 nodes and 0 nodes share edge "none"
        And git diff reports a rename from "src/Services/OldName.cs" to "src/Services/NewName.cs"
        And extraction will throw an exception for the renamed file new path
        When ApplyDiffAsync is called from commit "abc123" to "def456"
        Then no nodes are retired
        And SyncResult.FilesChanged is 1