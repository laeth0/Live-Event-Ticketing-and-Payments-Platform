namespace Concourse.Domain.Common;

public class Result
{
    protected Result(bool isSuccess, ResultError error)
    {
        if (isSuccess && error != ResultError.None)
        {
            throw new ArgumentException("A successful result cannot contain an error.", nameof(error));
        }

        if (!isSuccess && error == ResultError.None)
        {
            throw new ArgumentException("A failed result must contain an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public ResultError Error { get; }

    public static Result Success() => new(true, ResultError.None);

    public static Result<TValue> Success<TValue>(TValue value) =>
        new(value, true, ResultError.None);

    public static Result Failure(ResultError error) => new(false, error);

    public static Result<TValue> Failure<TValue>(ResultError error) =>
        new(default, false, error);

    public TResponse Match<TResponse>(
        Func<TResponse> onSuccess,
        Func<ResultError, TResponse> onFailure) =>
        IsSuccess ? onSuccess() : onFailure(Error);
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, ResultError error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");

    public TResponse Match<TResponse>(
        Func<TValue, TResponse> onSuccess,
        Func<ResultError, TResponse> onFailure) =>
        IsSuccess ? onSuccess(Value) : onFailure(Error);

    public static implicit operator Result<TValue>(TValue value) => Result.Success(value);
}
