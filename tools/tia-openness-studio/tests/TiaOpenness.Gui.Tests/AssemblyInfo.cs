using Xunit;

// Loc, ThemeManager and Application are process-wide singletons, and the WPF tests share one
// UI thread. Running collections in parallel would let one test's language change land in the
// middle of another's assertion.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
