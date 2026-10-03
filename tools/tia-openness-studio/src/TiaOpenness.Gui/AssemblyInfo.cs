using System.Runtime.CompilerServices;

// The string catalogue and the theme dictionaries are implementation detail, not API - but the
// tests that check them for missing keys and mismatched placeholders have to see them.
[assembly: InternalsVisibleTo("TiaOpenness.Gui.Tests")]
