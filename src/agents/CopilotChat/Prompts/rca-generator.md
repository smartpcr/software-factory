Generate a Root Cause Analysis for incident {incident_id}.

**Incident folder:** {incident_folder}

**Available context files:**
{context_files}

**Kusto query artifacts (query .md + result .csv):**
{kusto_artifacts}

**IMPORTANT:** Read ALL files listed above. For kusto artifacts, read BOTH the query .md file (to understand what was queried and why) AND the .csv file (to see actual results). The query .md files link to issues in code_issues.md via their YAML description field. Cross-reference CSV data with code_issues.md and etw_events.md to build an evidence chain.

Write the RCA to: {output_file}
