using FluentValidation.Results;

namespace Concourse.Application.Common.Exceptions;

public sealed class ValidationException : Exception
{
    public ValidationException(IEnumerable<ValidationFailure> validationFailures)
        : base("One or more validation errors occurred.")
    {
        ArgumentNullException.ThrowIfNull(validationFailures);

        Errors = validationFailures
            .GroupBy(validationFailure => validationFailure.PropertyName)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(validationFailure => validationFailure.ErrorMessage)
                    .Distinct()
                    .ToArray());
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
