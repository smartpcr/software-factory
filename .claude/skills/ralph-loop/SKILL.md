---
name: ralph-loop
description: Execute tasks using the ralph-wiggum agentic loop pattern. Iterates autonomously until completion or max iterations. Use when user says "ralph loop", "ralph-wiggum", "agentic loop", or needs iterative task execution.
author: xiaodoli
created: 2026-01-30
updated: 2026-01-31
---

# Ralph-Wiggum Loop

An autonomous agentic loop pattern that iterates on a task until completion.

## Configuration

| Parameter | Value |
|-----------|-------|
| **max-iterations** | 20 |
| **completion-promise** | `DONE` |

## How It Works

The ralph-wiggum loop is an iterative execution pattern where:

1. **Start**: Receive a task or goal from the user
2. **Execute**: Work on the task, making progress each iteration
3. **Check**: After each iteration, evaluate if the task is complete
4. **Signal**: Output `DONE` when the task is fully complete
5. **Repeat**: If not complete and under max iterations, continue

## Loop Protocol

### Starting the Loop

When the user invokes this skill with a task:

```
ralph loop: <task description>
```

Begin the iterative process.

### Each Iteration

For each iteration (1 to 20):

1. **Assess** current state and what remains
2. **Act** on the highest-priority item
3. **Verify** the action succeeded
4. **Evaluate** if the overall task is complete

### Completion Conditions

The loop terminates when either:

- **Success**: Task is complete - output `DONE` on its own line
- **Max iterations**: Reached 20 iterations without completion

### Output Format

During each iteration, report:

```
[Iteration N/20]
Action: <what you're doing>
Result: <outcome>
Remaining: <what's left to do>
```

When complete:

```
[Iteration N/20]
Action: <final action>
Result: <final outcome>
Status: Task complete

DONE
```

If max iterations reached without completion:

```
[Iteration 20/20]
Status: Max iterations reached
Completed: <what was accomplished>
Remaining: <what couldn't be finished>
```

## Usage Examples

### Example 1: Fix All Lint Errors

```
User: ralph loop: fix all eslint errors in src/
```

The loop will:
1. Run eslint to identify errors
2. Fix errors one file/category at a time
3. Re-run eslint to verify fixes
4. Continue until no errors remain
5. Output `DONE`

### Example 2: Implement Feature with Tests

```
User: ralph loop: add user authentication with tests
```

The loop will:
1. Plan the implementation
2. Create auth module
3. Add login/logout functions
4. Write unit tests
5. Run tests, fix failures
6. Output `DONE` when all tests pass

### Example 3: Refactor Module

```
User: ralph loop: refactor utils.js to TypeScript
```

The loop will:
1. Analyze current utils.js
2. Create utils.ts with types
3. Update imports in dependent files
4. Fix type errors iteratively
5. Verify build succeeds
6. Output `DONE`

## Best Practices

1. **Clear Goals**: Provide specific, measurable completion criteria
2. **Scope**: Keep tasks focused enough to complete in 20 iterations
3. **Verification**: Each iteration should verify its own success
4. **Progress**: Make measurable progress each iteration

## Notes

- The `DONE` signal must appear on its own line to indicate completion
- Progress is tracked visually with `[Iteration N/20]` markers
- If a task is too large, break it into sub-tasks before starting
- Use `ralph loop --dry-run: <task>` to preview the approach without executing
