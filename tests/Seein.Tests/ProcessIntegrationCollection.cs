namespace Seein.Tests;

// These tests launch real Windows child processes. Run them separately from
// parallel CPU/file-heavy fixtures so cold shell startup is not mistaken for
// a protocol or process-lifecycle failure on shared CI runners.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessIntegrationCollection
{
    public const string Name = "Windows child process integration";
}
