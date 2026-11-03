using Xunit;

namespace PredatorControlApp.Tests
{
    [CollectionDefinition("NoParallel", DisableParallelization = true)]
    public class NoParallelCollectionDefinition
    {
    }

    [CollectionDefinition("SingleInstanceTests", DisableParallelization = true)]
    public class SingleInstanceTestsCollectionDefinition
    {
    }

    [CollectionDefinition("NamedPipeTests", DisableParallelization = true)]
    public class NamedPipeTestsCollectionDefinition
    {
    }

    [CollectionDefinition("ExhaustiveChaosTests", DisableParallelization = true)]
    public class ExhaustiveChaosTestsCollectionDefinition
    {
    }
}
