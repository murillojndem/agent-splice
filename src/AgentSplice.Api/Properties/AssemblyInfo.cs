using System.Runtime.CompilerServices;

// The generated entry-point type for top-level statements is internal. Integration tests host the
// real application through WebApplicationFactory, and architecture tests load this assembly through
// that same type, so both need to see it. Exposing it this way avoids declaring a public Program
// type purely for test access.
[assembly: InternalsVisibleTo("AgentSplice.IntegrationTests")]
[assembly: InternalsVisibleTo("AgentSplice.ArchitectureTests")]
