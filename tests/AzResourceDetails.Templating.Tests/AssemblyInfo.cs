using Xunit;

// RegionDisplayNamesTests and TemplateFunctionsTests both call RegionDisplayNames.Configure, which
// mutates that class's shared static state — xUnit parallelizes across test classes by default, so
// without this, two classes calling Configure with different tables could interleave and produce
// flaky, order-dependent failures. This project is small enough that running it fully sequentially
// costs nothing worth trading determinism for.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
