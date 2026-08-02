using System.Runtime.CompilerServices;

// The persistence rows, the row mapper, and the context's DbSets are internal on purpose: they are
// the store's private shape, and nothing outside this module may take a dependency on a column name
// or map a row back into a domain record. The tests still have to assert what is actually written,
// which is the one property no public surface can express, so they are let in explicitly rather than
// by widening the module's API.
[assembly: InternalsVisibleTo("AgentSplice.UnitTests")]
[assembly: InternalsVisibleTo("AgentSplice.IntegrationTests")]
