param([switch] $Journal)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SafeCommandPath([string] $Path) {
    # Paths are developer-owned inputs, but cmd/CALL expansion must never interpret them.
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path) -or
        $Path -match '["%!\r\n&|<>^]') { throw 'Unsupported compile-only command path.' }
}

function Assert-NoReparse([string] $Path) {
    $Current = [IO.Path]::GetFullPath($Path)
    while (-not [string]::IsNullOrEmpty($Current)) {
        if (Test-Path -LiteralPath $Current) {
            if (((Get-Item -LiteralPath $Current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Compile-only output/source ancestry is a reparse point.'
            }
        }
        $Parent = [IO.Directory]::GetParent($Current)
        if ($null -eq $Parent) { break }
        $Current = $Parent.FullName
    }
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::Is64BitProcess) {
        throw 'Compile-only WER assertions require 64-bit Windows.'
    }
    $Repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $SourceName = if ($Journal) { 'JournalSdkAbiAssertions' } else { 'WerAbiAssertions' }
    $Source = Join-Path $Repository ('tools\MadClean.WindowsSmoke\' + $SourceName + '.cpp')
    $OutputDirectory = Join-Path $Repository 'artifacts\native-abi'
    $Object = Join-Path $OutputDirectory ($SourceName + '.obj')
    $Vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $CommandProcessor = Join-Path $env:SystemRoot 'System32\cmd.exe'
    foreach ($Path in @($Repository, $Source, $OutputDirectory, $Object, $Vswhere, $CommandProcessor)) {
        Assert-SafeCommandPath $Path
    }
    foreach ($Path in @($Source, $Vswhere, $CommandProcessor)) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'A fixed compile-only prerequisite is missing.' }
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
    Assert-NoReparse $Object
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    if (Test-Path -LiteralPath $Object) { Remove-Item -LiteralPath $Object -Force }
    # Do not inherit compiler option injection (including syntax-only/no-output options).
    Remove-Item Env:CL -ErrorAction SilentlyContinue
    Remove-Item Env:_CL_ -ErrorAction SilentlyContinue
    # vcvars sets the installed SDK include environment. Invoke the exact discovered compiler,
    # never PATH/current-directory cl.exe. /c creates only an object; no linker or program runs.
    $Command = 'call "' + $Vcvars + '" >nul && "' + $Compiler + '" /nologo /c /W4 /WX /Fo"' + $Object + '" "' + $Source + '"'
    & $CommandProcessor /d /s /c $Command
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Object -PathType Leaf) -or
        (Get-Item -LiteralPath $Object).Length -le 0) { throw 'Compile-only Windows SDK assertions failed.' }
    if ($Journal) {
        Write-Output 'Journal SDK compile-only x64 assertions passed; no linking or executable execution occurred.'
    } else {
        Write-Output 'WER SDK compile-only x64 assertions passed; no linking or executable execution occurred.'
    }
    exit 0
}
catch {
    # Fixed developer message only: do not dump environment, discovery output or exception text.
    [Console]::Error.WriteLine('SDK compile-only validation failed; verify installed x64 Visual Studio/SDK tools and bounded compiler diagnostics.')
    exit 1
}
