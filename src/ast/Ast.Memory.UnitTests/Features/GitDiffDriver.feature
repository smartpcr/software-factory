@git-diff
Feature: Git Diff Driver
    Parse git diff output to detect file changes between commits.

    Scenario: Parse modified and deleted files
        Given git diff output with "M" status for "Service.cs" and "D" status for "OldFile.cs"
        When the output is parsed
        Then the result contains Modified for "Service.cs"
        And the result contains Deleted for "OldFile.cs"

    Scenario: Parse renamed file
        Given git diff output with rename from "Old.cs" to "New.cs" with similarity 100
        When the output is parsed
        Then the result contains a rename for "New.cs" from "Old.cs"