using System;

namespace TiaOpenness.Gui.Localization;

/// <summary>
/// A message stored as a key plus its arguments and translated only when read.
///
/// The status line is written by background operations and then sits on screen, so keeping it
/// deferred means switching language re-renders what is currently shown rather than leaving the
/// last operation's sentence stranded in the previous language. Log lines are deliberately not
/// deferred - they are a transcript, and a transcript should not rewrite itself.
///
/// An argument may itself be a <see cref="LocalizedText"/>, which is how "Compiling…" is built
/// from a frame key and an operation key without either of them being resolved early.
/// </summary>
public readonly struct LocalizedText
{
    private readonly string? _key;
    private readonly object?[]? _args;
    private readonly string? _literal;

    private LocalizedText(string? key, object?[]? args, string? literal)
    {
        _key = key;
        _args = args;
        _literal = literal;
    }

    public static LocalizedText Empty => new(null, null, string.Empty);

    /// <summary>A catalogue entry, formatted on read.</summary>
    public static LocalizedText Key(string key, params object?[] args) => new(key, args, null);

    /// <summary>Text that is already in its final form - an exception message, say.</summary>
    public static LocalizedText Literal(string text) => new(null, null, text);

    /// <summary>"&lt;operation&gt;…", the shape the status line takes while something is running.</summary>
    public static LocalizedText Working(string key, params object?[] args)
        => Key("Status.Working", Key(key, args));

    public string Resolve()
    {
        if (_key is null) return _literal ?? string.Empty;
        if (_args is null || _args.Length == 0) return Loc.Current[_key];

        // Resolve nested entries into a copy; the struct has to stay reusable after a read.
        var args = new object?[_args.Length];
        for (var i = 0; i < _args.Length; i++)
        {
            args[i] = _args[i] is LocalizedText nested ? nested.Resolve() : _args[i];
        }

        return Loc.Current.T(_key, args);
    }

    public override string ToString() => Resolve();
}
