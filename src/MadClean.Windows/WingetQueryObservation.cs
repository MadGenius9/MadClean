using MadClean.Core;

namespace MadClean.Windows;

internal sealed record WingetQueryObservation(CheckResult<AppUpdate> Result, WingetQueryDiagnostics Diagnostics);
