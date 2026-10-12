using Xunit;

// Classes that select the process-wide bundle root (BundleLayout.Initialize) run alone. HostOptions.Parse and every other bundle
// lookup read that selection, so running them beside other classes let BundleRootTests resolve the repository instead of its fixture.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BundleSelectionCollection
{
    public const string Name = "Process-wide bundle root selection";
}
