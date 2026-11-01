$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SafeCommandPath([string] $Path) {
    # Paths are developer-owned inputs, but cmd/CALL expansion must never interpret them.
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path) -or
        $Path -match '["%!\r\n&|<>^]') { throw 'Unsupported shortcut-control command path.' }
}

function Assert-NoReparse([string] $Path) {
    $Current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($Current)) {
        if (Test-Path -LiteralPath $Current) {
            if (((Get-Item -LiteralPath $Current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Shortcut-control output/source ancestry is a reparse point.'
            }
        }
        $Parent = [IO.Directory]::GetParent($Current)
        if ($null -eq $Parent) { break }
        $Current = $Parent.FullName
    }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitProcess) {
        throw 'Development shortcut control requires 64-bit Windows.'
    }
    $Repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $Source = Join-Path $Repository 'tools\MadClean.WindowsSmoke\ShortcutRenameControl.cpp'
    $OutputDirectory = Join-Path $Repository 'artifacts\native-controls'
    $Object = Join-Path $OutputDirectory 'ShortcutRenameControl.obj'
    $Dll = Join-Path $OutputDirectory 'ShortcutRenameControl.dll'
    $ImportLibrary = Join-Path $OutputDirectory 'ShortcutRenameControl.lib'
    $Exports = Join-Path $OutputDirectory 'ShortcutRenameControl.exp'
    $Symbols = Join-Path $OutputDirectory 'ShortcutRenameControl.pdb'
    $Vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $CommandProcessor = Join-Path $env:SystemRoot 'System32\cmd.exe'
    foreach ($Path in @($Repository, $Source, $OutputDirectory, $Object, $Dll, $ImportLibrary, $Exports, $Symbols, $Vswhere, $CommandProcessor)) {
        Assert-SafeCommandPath $Path
    }
    foreach ($Path in @($Source, $Vswhere, $CommandProcessor)) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'A fixed shortcut-control prerequisite is missing.' }
        Assert-NoReparse $Path
    }
    # No preview instances or downloads. Require exactly one latest installed matching instance.
    $Installations = @(& $Vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath -utf8)
    if ($LASTEXITCODE -ne 0 -or $Installations.Count -ne 1) { throw 'Installed x64 Visual Studio tools discovery failed.' }
    $Installation = [IO.Path]::GetFullPath($Installations[0].Trim())
    Assert-SafeCommandPath $Installation
    $Vcvars = Join-Path $Installation 'VC\Auxiliary\Build\vcvars64.bat'
    $VersionFile = Join-Path $Installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt'
    foreach ($Path in @($Vcvars, $VersionFile)) {
        Assert-SafeCommandPath $Path
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Installed x64 compiler configuration is missing.' }
        Assert-NoReparse $Path
    }
    $Version = [IO.File]::ReadAllText($VersionFile).Trim()
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Installed default compiler version is invalid.' }
    $Compiler = Join-Path $Installation ('VC\Tools\MSVC\' + $Version + '\bin\Hostx64\x64\cl.exe')
    Assert-SafeCommandPath $Compiler
    if (-not (Test-Path -LiteralPath $Compiler -PathType Leaf)) { throw 'Installed default x64 compiler is missing.' }
    Assert-NoReparse $Compiler
    Assert-NoReparse $OutputDirectory
    foreach ($Output in @($Object, $Dll, $ImportLibrary, $Exports, $Symbols)) { Assert-NoReparse $Output }
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    foreach ($Output in @($Object, $Dll, $ImportLibrary, $Exports, $Symbols)) {
        if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
    }
    # Do not inherit compiler option injection (including syntax-only/no-output options).
    Remove-Item Env:CL -ErrorAction SilentlyContinue
    Remove-Item Env:_CL_ -ErrorAction SilentlyContinue
    Remove-Item Env:LINK -ErrorAction SilentlyContinue
    Remove-Item Env:_LINK_ -ErrorAction SilentlyContinue
    # vcvars sets the installed SDK include environment. Invoke the exact discovered compiler,
    # never PATH/current-directory cl.exe. /LD links only the isolated development DLL; no control runs.
    $Command = 'call "' + $Vcvars + '" >nul && "' + $Compiler + '" /nologo /LD /W4 /WX /Fo"' + $Object + '" /Fe"' + $Dll + '" "' + $Source + '" /link /INCREMENTAL:NO /IMPLIB:"' + $ImportLibrary + '" /PDB:"' + $Symbols + '"'
    & $CommandProcessor /d /s /c $Command
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Object -PathType Leaf) -or
        -not (Test-Path -LiteralPath $Dll -PathType Leaf) -or
        (Get-Item -LiteralPath $Object).Length -le 0 -or (Get-Item -LiteralPath $Dll).Length -le 0) {
        throw 'Development shortcut control compilation/linking failed.'
    }
    Write-Output 'Development-only x64 shortcut control DLL compiled and linked; no control executed or packaged.'
    exit 0
}
catch {
    # Fixed developer message only: do not dump environment, discovery output or exception text.
    [Console]::Error.WriteLine('Development shortcut control DLL compilation failed; verify installed x64 Visual Studio/SDK tools and bounded compiler diagnostics.')
    exit 1
}
