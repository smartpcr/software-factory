try {
  $p = Get-Process -Id 263772 -ErrorAction Stop
  Write-Output ("Forge.Api PID 263772 found: " + $p.ProcessName + " started " + $p.StartTime)
} catch {
  Write-Output ("Forge.Api PID 263772 NOT found: " + $_)
}

# Token elevation check via WindowsIdentity of THIS elevated probe shell
# (won't directly check 263772's token, but if our shell is elevated and 263772
# was launched from the self-elevation block, it inherited the same elevation).
$id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$pr = New-Object System.Security.Principal.WindowsPrincipal($id)
$amAdmin = $pr.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output ("THIS probe shell admin: " + $amAdmin)

# Direct token check on Forge.Api via Win32 GetTokenInformation (TokenElevation)
$signature = @'
using System;
using System.Runtime.InteropServices;
public class TokenCheck {
  [DllImport("advapi32.dll", SetLastError=true)]
  public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
  [DllImport("advapi32.dll", SetLastError=true)]
  public static extern bool GetTokenInformation(IntPtr TokenHandle, uint TokenInformationClass, out uint TokenInformation, uint TokenInformationLength, out uint ReturnLength);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern bool CloseHandle(IntPtr hObject);
  [DllImport("kernel32.dll", SetLastError=true)]
  public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
}
'@
Add-Type -TypeDefinition $signature -ErrorAction SilentlyContinue
$PROCESS_QUERY_INFO = 0x0400
$TOKEN_QUERY = 0x0008
$TokenElevation = 20
$h = [TokenCheck]::OpenProcess($PROCESS_QUERY_INFO, $false, 263772)
if ($h -eq [IntPtr]::Zero) {
  Write-Output ("OpenProcess failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
} else {
  $tok = [IntPtr]::Zero
  $ok = [TokenCheck]::OpenProcessToken($h, $TOKEN_QUERY, [ref]$tok)
  if (-not $ok) {
    Write-Output ("OpenProcessToken failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
  } else {
    $elev = 0; $ret = 0
    $ok2 = [TokenCheck]::GetTokenInformation($tok, $TokenElevation, [ref]$elev, 4, [ref]$ret)
    if ($ok2) {
      Write-Output ("Forge.Api 263772 TokenIsElevated: " + ($elev -ne 0))
    } else {
      Write-Output ("GetTokenInformation failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    [TokenCheck]::CloseHandle($tok) | Out-Null
  }
  [TokenCheck]::CloseHandle($h) | Out-Null
}
