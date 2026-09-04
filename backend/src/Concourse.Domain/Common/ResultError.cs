namespace Concourse.Domain.Common;

public sealed record ResultError(string Code, string Description, ErrorType Type)
{
    public static readonly ResultError None = new(string.Empty, string.Empty, ErrorType.None);

    public static ResultError Failure(string code, string description) =>
        new(code, description, ErrorType.Failure);

    public static ResultError Validation(string code, string description) =>
        new(code, description, ErrorType.Validation);

    public static ResultError NotFound(string code, string description) =>
        new(code, description, ErrorType.NotFound);

    public static ResultError Conflict(string code, string description) =>
        new(code, description, ErrorType.Conflict);

    public static ResultError Unauthorized(string code, string description) =>
        new(code, description, ErrorType.Unauthorized);

    public static ResultError Forbidden(string code, string description) =>
        new(code, description, ErrorType.Forbidden);
}
