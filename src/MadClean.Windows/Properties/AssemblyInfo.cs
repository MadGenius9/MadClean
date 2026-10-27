using System.Runtime.CompilerServices;

// The development-only harness may create strictly scoped fixture services. It is not shipped
// with MadClean and has no capability to enable production mutations.
[assembly: InternalsVisibleTo("MadClean.WindowsSmoke")]
