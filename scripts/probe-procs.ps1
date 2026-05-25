$out = 'E:\work\github\crp\workflow\forge-elevated-procs.txt'
$listenProcs = Get-NetTCPConnection -LocalPort 5180 -State Listen -ErrorAction SilentlyContinue |
  Select-Object -ExpandProperty OwningProcess -Unique
$forgePid = if ($listenProcs) { $listenProcs[0] } else { 0 }
"Resolved Forge.Api PID: $forgePid" | Set-Content -Path $out -Encoding utf8
"== Children of Forge.Api PID $forgePid ==" | Add-Content -Path $out -Encoding utf8
Get-CimInstance Win32_Process -Filter "ParentProcessId=$forgePid" |
  Select-Object ProcessId, Name, @{n='Started';e={$_.CreationDate.ToLocalTime().ToString('HH:mm:ss')}}, CommandLine |
  Format-Table -AutoSize -Wrap |
  Out-String -Width 220 |
  Add-Content -Path $out -Encoding utf8

"" | Add-Content -Path $out -Encoding utf8
"== ALL cmd/dotnet/nuget/MSBuild/git/powershell processes (last 30 min) ==" | Add-Content -Path $out -Encoding utf8
$names = @('cmd.exe','dotnet.exe','nuget.exe','MSBuild.exe','git.exe','powershell.exe')
Get-CimInstance Win32_Process | Where-Object {
  $names -contains $_.Name -and
  ($_.CreationDate.ToLocalTime() -gt (Get-Date).AddMinutes(-30))
} |
  Select-Object ProcessId, ParentProcessId, Name, @{n='Started';e={$_.CreationDate.ToLocalTime().ToString('HH:mm:ss')}}, @{n='Cmd';e={if($_.CommandLine){$_.CommandLine.Substring(0,[Math]::Min(220,$_.CommandLine.Length))}}} |
  Sort-Object Started |
  Format-Table -AutoSize -Wrap |
  Out-String -Width 240 |
  Add-Content -Path $out -Encoding utf8

"" | Add-Content -Path $out -Encoding utf8
"== Descendants of Forge.Api PID $forgePid (recursive) ==" | Add-Content -Path $out -Encoding utf8
function Get-Tree {
  param([int]$ParentPid, [int]$Indent = 0)
  $kids = Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentPid" -ErrorAction SilentlyContinue
  foreach ($k in $kids) {
    $cmd = if ($k.CommandLine) { $k.CommandLine.Substring(0, [Math]::Min(180, $k.CommandLine.Length)) } else { '' }
    ('  ' * $Indent + 'PID=' + $k.ProcessId + ' ' + $k.Name + ' :: ' + $cmd) |
      Add-Content -Path $out -Encoding utf8
    Get-Tree -ParentPid $k.ProcessId -Indent ($Indent + 1)
  }
}
Get-Tree -ParentPid $forgePid

"" | Add-Content -Path $out -Encoding utf8
"== Forge.Api process snapshot ==" | Add-Content -Path $out -Encoding utf8
try {
  $forge = Get-Process -Id $forgePid -ErrorAction Stop
  "PID=$($forge.Id) Name=$($forge.ProcessName) StartTime=$($forge.StartTime) Threads=$($forge.Threads.Count) Handles=$($forge.HandleCount)" |
    Add-Content -Path $out -Encoding utf8
} catch {
  "Could not query Forge.Api PID ${forgePid}: $_" | Add-Content -Path $out -Encoding utf8
}
