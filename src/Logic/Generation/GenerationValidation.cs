using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Logic.Generation
{
    public sealed class GenerationValidationError
    {
        public string Path { get; }
        public string Rule { get; }
        public string Message { get; }

        public GenerationValidationError(string path, string rule, string message)
        {
            Path = path;
            Rule = rule;
            Message = message;
        }
    }

    public sealed class GenerationValidationException : Exception
    {
        public IReadOnlyList<GenerationValidationError> Errors { get; }

        public GenerationValidationException(IEnumerable<GenerationValidationError> errors)
            : this(errors.ToArray()) { }

        private GenerationValidationException(GenerationValidationError[] errors)
            : base(string.Join("; ", errors.Select(e => e.Path + ": " + e.Rule + ": " + e.Message)))
        {
            Errors = Array.AsReadOnly(errors);
        }
    }
}
