using ApplicationValidationException = Concourse.Application.Common.Exceptions.ValidationException;
using Microsoft.AspNetCore.Mvc;

namespace Concourse.Api.Middleware;

internal sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    IProblemDetailsService problemDetailsService)
{
    private const string InternalServerErrorType =
        "https://tools.ietf.org/html/rfc9110#section-15.6.1";

    private const string ValidationErrorType =
        "https://tools.ietf.org/html/rfc9110#section-15.5.1";

    public async Task InvokeAsync(HttpContext httpContext)
    {
        try
        {
            await next(httpContext);
        }
        catch (Exception exception)
        {
            if (httpContext.Response.HasStarted)
            {
                throw;
            }

            await HandleExceptionAsync(httpContext, exception);
        }
    }

    private async Task HandleExceptionAsync(HttpContext httpContext, Exception exception)
    {
        var problemDetails = exception switch
        {
            ApplicationValidationException validationException =>
                CreateValidationProblemDetails(validationException),
            _ => CreateInternalServerErrorProblemDetails()
        };

        httpContext.Response.Clear();
        httpContext.Response.StatusCode =
            problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        var problemDetailsContext = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
            Exception = exception
        };

        if (!await problemDetailsService.TryWriteAsync(problemDetailsContext))
        {
            await httpContext.Response.WriteAsJsonAsync(
                problemDetails,
                httpContext.RequestAborted);
        }
    }

    private static HttpValidationProblemDetails CreateValidationProblemDetails(
        ApplicationValidationException exception) =>
        new(exception.Errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failure",
            Type = ValidationErrorType
        };

    private static ProblemDetails CreateInternalServerErrorProblemDetails() =>
        new()
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Server error",
            Detail = "An unexpected error occurred.",
            Type = InternalServerErrorType
        };
}
