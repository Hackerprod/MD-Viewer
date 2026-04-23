# Register-MD-Association.ps1
# Asocia los archivos .md a MD Viewer en HKCU (sin necesitar permisos de administrador)
# Uso: .\Register-MD-Association.ps1 [-ExePath "ruta\MDViewer.exe"] [-Unregister]

param(
    [string]$ExePath = "",
    [switch]$Unregister
)

$ProgId = "MDViewer.md"

if ($Unregister) {
    Remove-Item -Path "HKCU:\Software\Classes\$ProgId" -Recurse -ErrorAction SilentlyContinue
    $ext = Get-ItemProperty -Path "HKCU:\Software\Classes\.md" -ErrorAction SilentlyContinue
    if ($ext.'(default)' -eq $ProgId) {
        Remove-Item -Path "HKCU:\Software\Classes\.md" -Recurse -ErrorAction SilentlyContinue
    }
    Write-Host "Asociacion eliminada." -ForegroundColor Yellow
    exit 0
}

if (-not $ExePath) {
    $ExePath = Join-Path $PSScriptRoot "MDViewer.exe"
}

if (-not (Test-Path $ExePath)) {
    Write-Error "No se encontro el ejecutable: $ExePath`nEspecifica la ruta con -ExePath"
    exit 1
}

# ProgID
New-Item    -Path "HKCU:\Software\Classes\$ProgId"                         -Force | Out-Null
Set-ItemProperty "HKCU:\Software\Classes\$ProgId"                          -Name "(default)" -Value "Archivo Markdown"

New-Item    -Path "HKCU:\Software\Classes\$ProgId\DefaultIcon"             -Force | Out-Null
Set-ItemProperty "HKCU:\Software\Classes\$ProgId\DefaultIcon"              -Name "(default)" -Value "`"$ExePath`",0"

New-Item    -Path "HKCU:\Software\Classes\$ProgId\shell\open\command"      -Force | Out-Null
Set-ItemProperty "HKCU:\Software\Classes\$ProgId\shell\open\command"       -Name "(default)" -Value "`"$ExePath`" `"%1`""

# Extension
New-Item    -Path "HKCU:\Software\Classes\.md"  -Force | Out-Null
Set-ItemProperty "HKCU:\Software\Classes\.md"  -Name "(default)" -Value $ProgId

# Notify shell
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class Shell32 {
    [DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, int f, IntPtr a, IntPtr b);
}
"@ -ErrorAction SilentlyContinue

[Shell32]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)

Write-Host "OK: Los archivos .md ahora se abriran con:" -ForegroundColor Green
Write-Host "    $ExePath" -ForegroundColor Cyan
Write-Host ""
Write-Host "Para revertir: .\Register-MD-Association.ps1 -Unregister"
