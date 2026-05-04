use frontend design, i want to show agent swam working on a complex software story end to end, without human intervention. Make sure the UI clearly visualizes the progress of each story through the stages, shows which agents are working on which tasks in real time, highlights the iteration cycles between implementation, review, and e2e, and indicates passing/failing scores and stalled tasks so the user can see the swarm in action.


1) i have 4 teams: architecture, engineering, review and QA, each team have about 20 agents 

2) a story go thru the following stages: design & plan -> (implementation <-> review + e2e) cycle -> release, where design split implementation into several phases (phase is the top level of implementation), after implementation by eng team, it's reviewed by review team, QA team add e2e for this phase, send back to eng team, this goes several cycle until score of 90 is reached, which is considered a passing score, and the feature is ready for release.

3) a story starts with design & plan, assigned to arch team, output is architecture, tech-spec, implementation-plan, and e2e test scenarios
  - 3a) architecture specifies system diagram, component diagram, perf & reliability requirements, usage scenarios, deployment strategies, how component interaction, and different scenarios, and flow chart/sequence diagram for each scenarios, edge cases, etc
  - 3b) tech-spec specifies library, technologies to be used in implementation, including patterns, practices, maintainability
  - 3c) implementation-plan handed to eng team for coding, its hierarchical: phase -> stage - step, where a phase is an independent and cohesive feature, all stages/steps under it must be implemented together, a phase can have no more than 20 stages, and a stage can have no more than 20 steps. A step is a PR sized implementation and should not exceeding 20 file changes
  - 3d) e2e test scenarios are defined by BBD (plain-English) feature file written using gherkin grammar (cucumber for typescript and reqnroll for c#), feature files are grouped by phases, eng team need to implement e2e tests based on features for the given phase, all tests (unit, integration, e2e) must pass for a phase before handed over to review team.

4) 2 types of tasks are created for tracking: plan task and coding task. Each task is given an effort estimation using points with Fibonacci numbers: 1, 2, 3, 5, 8, 13. AI agent use points to set maxTurns (pts * 30) and maxIterations (pts * 5)
  4a) plan task is assigned to arch team, arch team lead is responsible if multiple agents should be assigned based on pts. All plan tasks must be work in pairs (2 agents are assigned to a task, 1 generator to generate design doc, 1 evaluator to do critical review and score the output), max iteration set by pts, score of >=90 is considered passing
  4b) output design docs are checked in to main branch once plan task is done, without review (we will decide if review is required, or human gate is required, since this is swarm/workstream design, no human intervention is required)
  4c) eng team hand over once design tasks are done, all agent need to establish context from all the design doc, not just implementation plan
  4d) based on workstream, dependency and parallelism defined in implementation plan, eng team lead assign coding tasks that have no dependency, or dependency satisfied (already implemented) to pair of agents, so very likely, many agents work in parallel, on different workstreams, each workstream use git worktree, for changes, raise PR and fix PR comments, worktree can be closed only after feature branch (worktree) is merged into main branch, and PR closed
  4e) for coding task, a pair of agent work on them, 1 generator and 1 evaluator, when they get pass score (>=90) within iteration limit, they are able to create a PR from it, otherwise, the work is considered failed, eng team lead will reassign the task to another pair, when total of 3 trials failed, the task is considered stalled. Note: 2nd pair and 3rd pair must use different model, i.e. if 1st pair generator=opus-4.7, evaluator=gpt-5.5, 2nd pair generator=gpt5.5, and evaluator=opus4.7, 3rd pair use generator=opus4.6-1M, evaluator=codex5.3
  4f) when PR is closed, the work item can be closed, the associated stage/step is considered done, when all stages/steps of a phase is done, eng lead need to make sure e2e tests/integration tests are covering all implementation, and tests are passing, before close the phase.
  4g) once a phase is closed, the following phases are evaluated for its dependencies and some of them become unblocked and can be assigned to agent pairs.

5) work items must be created to track both plan task and coding task
  5a) work item have type hierarchy
  5b) for coding task, Epic=Phase, Feature=Stage, Step=Task, where Epic -> Feature -> Task are hierarchy and type of work item
  5b) for plan task, there are no hierarchy, work item type is Architecture, TechSpec, ImplementationPlan and E2EScenarios
