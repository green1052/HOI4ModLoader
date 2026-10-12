# Empties Windows' standby list (the file cache), like RAMMap's "Empty Standby List", so the next game start reads from disk.
# Run from an administrator PowerShell: powershell -ExecutionPolicy Bypass -File D:\HOI4ModLoader\tools\purge-standby.ps1
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Standby {
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref Privilege state, int length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] struct Privilege { public int Count; public long Luid; public int Attributes; }
    public static int Purge() {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out token)) return -1;           // TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY
        var p = new Privilege { Count = 1, Attributes = 2 };                                     // SE_PRIVILEGE_ENABLED
        if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out p.Luid)) return -2;
        if (!AdjustTokenPrivileges(token, false, ref p, 0, IntPtr.Zero, IntPtr.Zero)) return -3;
        int command = 4;                                                                         // MemoryPurgeStandbyList
        return NtSetSystemInformation(80, ref command, 4);                                       // SystemMemoryListInformation
    }
}
"@
if ($args -contains '-Watch') {
    # Helper mode for benchmarks: for 15 minutes, empty the list whenever purge.request appears next to this script.
    $request = Join-Path $PSScriptRoot 'purge.request'; $done = Join-Path $PSScriptRoot 'purge.done'
    $until = (Get-Date).AddMinutes(15)
    while ((Get-Date) -lt $until) {
        if (Test-Path $request) { Remove-Item $request; Set-Content $done ([Standby]::Purge()) }
        Start-Sleep -Milliseconds 500
    }
    return
}
$status = [Standby]::Purge()
if ($status -eq 0) { "Standby list emptied." } else { "Failed ($status): run this from an administrator PowerShell." }
