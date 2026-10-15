using System.Runtime.CompilerServices;

// The probe has no public surface: it is a command, run by Docker. The tests still have to prove
// which targets it refuses and what it reports, and the contract tests have to prove the Dockerfile
// hands it a target it accepts with a timeout Docker will not cut short, so both are let in
// explicitly rather than by making the probe a library.
[assembly: InternalsVisibleTo("AgentSplice.IntegrationTests")]
[assembly: InternalsVisibleTo("AgentSplice.ContractTests")]
