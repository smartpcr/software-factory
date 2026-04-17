Param()
# One-shot reorganization of docs/workflow-ui to match phase-loop extension's discovery conventions.
# Renames phase-N/ -> phase_NN_<slug>/, stage-N.M-... -> stage_MM_<slug>.md,
# inserts a `## Steps` checkbox list at the top of each stage file, demotes `## Step N.M.K` headings,
# and emits a sibling phase_NN_<slug>.md plan file per phase.

$ErrorActionPreference = 'Stop'

$docsRoot = Join-Path $PSScriptRoot '..\docs\workflow-ui' | Resolve-Path | Select-Object -ExpandProperty Path

$phases = [ordered]@{
    'phase-0' = @{ Num = '00'; Slug = 'foundations_and_gates';           Title = 'Foundations & Gates';           Done = $true  }
    'phase-1' = @{ Num = '01'; Slug = 'host_contract_ci';                Title = 'Host, Contract, CI';             Done = $false }
    'phase-2' = @{ Num = '02'; Slug = 'node_kinds_schemas_round_trip';   Title = 'Node Kinds, Schemas, Round-Trip'; Done = $false }
    'phase-3' = @{ Num = '03'; Slug = 'canvas_palette_connections';      Title = 'Canvas, Palette, Connections';   Done = $false }
    'phase-4' = @{ Num = '04'; Slug = 'property_grid_and_validation';    Title = 'Property Grid & Validation';      Done = $false }
    'phase-5' = @{ Num = '05'; Slug = 'mapper_fixtures_golden_files';    Title = 'Mapper, Fixtures, Golden Files'; Done = $false }
    'phase-6' = @{ Num = '06'; Slug = 'signalr_telemetry_and_live_monitor'; Title = 'SignalR Telemetry & Live Monitor'; Done = $false }
    'phase-7' = @{ Num = '07'; Slug = 'auth_persistence_file_ops';       Title = 'Auth, Persistence, File Ops';    Done = $false }
    'phase-8' = @{ Num = '08'; Slug = 'packaging_docs_release';          Title = 'Packaging, Docs, Release';       Done = $false }
}

function Convert-StageFile {
    param(
        [string]$FilePath,
        [bool]$PhaseDone
    )

    $lines = Get-Content -Path $FilePath -Encoding UTF8

    $stepRegex = '^##\s+Step\s+([0-9]+(?:\.[0-9]+)*[a-z]?)\s*[—\-\u2013]\s*(.+?)\s*$'
    $stepMatches = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $stepRegex) {
            $stepMatches.Add([pscustomobject]@{
                LineIndex = $i
                Tag       = $Matches[1]
                Title     = $Matches[2]
            })
        }
    }

    if ($stepMatches.Count -eq 0) {
        Write-Warning "no ## Step headings found in $FilePath - skipping transform"
        return
    }

    $check = if ($PhaseDone) { 'x' } else { ' ' }
    $stepsBlock = [System.Collections.Generic.List[string]]::new()
    $stepsBlock.Add('## Steps')
    $stepsBlock.Add('')
    for ($n = 0; $n -lt $stepMatches.Count; $n++) {
        $m = $stepMatches[$n]
        $num = $n + 1
        $stepsBlock.Add("- [${check}] **Step ${num}**: $($m.Title) _(ref: Step $($m.Tag))_")
    }
    $stepsBlock.Add('')
    $stepsBlock.Add('---')
    $stepsBlock.Add('')

    $firstStepIdx = $stepMatches[0].LineIndex

    $insertIdx = $firstStepIdx
    while ($insertIdx -gt 0 -and [string]::IsNullOrWhiteSpace($lines[$insertIdx - 1])) { $insertIdx-- }
    if ($insertIdx -gt 0 -and $lines[$insertIdx - 1].Trim() -eq '---') { $insertIdx-- }
    while ($insertIdx -gt 0 -and [string]::IsNullOrWhiteSpace($lines[$insertIdx - 1])) { $insertIdx-- }

    $before = if ($insertIdx -gt 0) { $lines[0..($insertIdx - 1)] } else { @() }
    $after  = $lines[$insertIdx..($lines.Count - 1)]

    $afterDemoted = foreach ($ln in $after) {
        if ($ln -match $stepRegex) { '### ' + $ln.Substring(3) } else { $ln }
    }

    $merged = [System.Collections.Generic.List[string]]::new()
    foreach ($b in $before) { [void]$merged.Add($b) }
    if ($merged.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($merged[$merged.Count - 1])) { [void]$merged.Add('') }
    [void]$merged.Add('---')
    [void]$merged.Add('')
    foreach ($s in $stepsBlock) { [void]$merged.Add($s) }
    foreach ($a in $afterDemoted) { [void]$merged.Add($a) }

    Set-Content -Path $FilePath -Value $merged -Encoding UTF8
    Write-Host "transformed $FilePath ($($stepMatches.Count) steps)"
}

function Write-PhasePlanFile {
    param(
        [string]$PhaseDirPath,
        [string]$PhaseNum,
        [string]$PhaseSlug,
        [string]$PhaseTitle,
        [bool]  $PhaseDone
    )

    $phasePlanPath = Join-Path (Split-Path $PhaseDirPath -Parent) ("phase_${PhaseNum}_${PhaseSlug}.md")
    $stageFiles = Get-ChildItem -Path $PhaseDirPath -Filter 'stage_*.md' | Sort-Object Name

    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine("# Phase $([int]$PhaseNum) - $PhaseTitle")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('> Part of [Workflow UI master plan](./workflow-ui-plan.md)')
    [void]$sb.AppendLine('')
    $statusWord = if ($PhaseDone) { 'Completed' } else { 'Not started' }
    [void]$sb.AppendLine("**Status:** $statusWord")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## Stages')
    [void]$sb.AppendLine('')

    foreach ($sf in $stageFiles) {
        $first = Get-Content -Path $sf.FullName -TotalCount 20 | Where-Object { $_ -match '^#\s+' } | Select-Object -First 1
        if (-not $first) { $first = $sf.BaseName }
        $stageTitle = ($first -replace '^#\s+','').Trim()
        $rel = "$([IO.Path]::GetFileName($PhaseDirPath))/$($sf.Name)"
        [void]$sb.AppendLine("- [$rel]($rel) - $stageTitle")
    }

    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('## Per-step enumeration')
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('Steps are enumerated by the `phase-loop` extension from the `## Steps` checklist at the top of each stage file. One pull request is produced per step.')
    [void]$sb.AppendLine('')

    Set-Content -Path $phasePlanPath -Value $sb.ToString() -Encoding UTF8
    Write-Host "wrote $phasePlanPath"
}

foreach ($phaseDirName in $phases.Keys) {
    $phase = $phases[$phaseDirName]
    $oldDir = Join-Path $docsRoot $phaseDirName
    if (-not (Test-Path $oldDir)) { continue }

    Get-ChildItem -Path $oldDir -Filter 'stage-*.md' | ForEach-Object {
        if ($_.Name -match '^stage-(\d+)\.(\d+)-(.+)\.md$') {
            $minor = [int]$Matches[2]
            $slug  = $Matches[3].Replace('-', '_')
            $newName = 'stage_{0:D2}_{1}.md' -f $minor, $slug
            Rename-Item -Path $_.FullName -NewName $newName
            Write-Host "renamed $($_.Name) -> $newName"
        }
    }
}

foreach ($phaseDirName in $phases.Keys) {
    $phase = $phases[$phaseDirName]
    $oldDir = Join-Path $docsRoot $phaseDirName
    if (-not (Test-Path $oldDir)) { continue }
    $newName = "phase_$($phase.Num)_$($phase.Slug)"
    $newDir  = Join-Path $docsRoot $newName
    if ($oldDir -ne $newDir) {
        Rename-Item -Path $oldDir -NewName $newName
        Write-Host "renamed dir $phaseDirName -> $newName"
    }
}

foreach ($phaseDirName in $phases.Keys) {
    $phase = $phases[$phaseDirName]
    $newDir = Join-Path $docsRoot ("phase_$($phase.Num)_$($phase.Slug)")
    if (-not (Test-Path $newDir)) { continue }
    Get-ChildItem -Path $newDir -Filter 'stage_*.md' | ForEach-Object {
        Convert-StageFile -FilePath $_.FullName -PhaseDone:$phase.Done
    }
}

foreach ($phaseDirName in $phases.Keys) {
    $phase = $phases[$phaseDirName]
    $newDir = Join-Path $docsRoot ("phase_$($phase.Num)_$($phase.Slug)")
    if (-not (Test-Path $newDir)) { continue }
    Write-PhasePlanFile -PhaseDirPath $newDir -PhaseNum $phase.Num -PhaseSlug $phase.Slug -PhaseTitle $phase.Title -PhaseDone:$phase.Done
}

Write-Host "`nReorganization complete."
