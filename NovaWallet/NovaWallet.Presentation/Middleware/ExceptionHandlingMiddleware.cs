using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NovaWallet.Domain.Exceptions;

namespace NovaWallet.Presentation.Middleware;

public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? string.Empty;

        var (statusCode, title, type, detail, extensions) = exception switch
        {
            ValidationException vex => (
                StatusCodes.Status400BadRequest,
                "Validation Failed",
                "validation-error",
                "One or more validation errors occurred.",
                (IDictionary<string, object?>?)new Dictionary<string, object?>
                {
                    ["errors"] = vex.Errors
                        .GroupBy(e => e.PropertyName)
                        .ToDictionary(g => g.Key, g => (object)g.Select(e => e.ErrorMessage).ToArray())
                }),

            WalletNotFoundException => (
                StatusCodes.Status404NotFound, "Wallet Not Found",
                ((DomainException)exception).ErrorCode, exception.Message, null),

            WalletAlreadyExistsException => (
                StatusCodes.Status409Conflict, "Wallet Already Exists",
                ((DomainException)exception).ErrorCode, exception.Message, null),

            InsufficientFundsException => (
                StatusCodes.Status422UnprocessableEntity, "Insufficient Funds",
                ((DomainException)exception).ErrorCode, exception.Message, null),

            DailyLimitExceededException => (
                StatusCodes.Status422UnprocessableEntity, "Daily Limit Exceeded",
                ((DomainException)exception).ErrorCode, exception.Message, null),

            IdempotencyKeyConflictException => (
                StatusCodes.Status422UnprocessableEntity, "Idempotency Key Conflict",
                ((DomainException)exception).ErrorCode, exception.Message, null),

            IdempotencyKeyInFlightException => (
                StatusCodes.Status409Conflict, "Request In-Flight",
                ((DomainException)exception).ErrorCode, exception.Message, null),

            WalletAccessDeniedException => (
                StatusCodes.Status403Forbidden, "Forbidden",
                ((DomainException)exception).ErrorCode, exception.Message, null),

            _ => (
                StatusCodes.Status500InternalServerError, "Internal Server Error",
                "internal-error", "An unexpected error occurred.", null)
        };

        if (statusCode >= 500)
            _logger.LogError(exception, "Unhandled exception. CorrelationId: {CorrelationId}", correlationId);
        else
            _logger.LogWarning(exception, "Handled exception {Type}. CorrelationId: {CorrelationId}", type, correlationId);

        var problem = new ProblemDetails
        {
            Type = $"https://novawallet.io/errors/{type}",
            Title = title,
            Status = statusCode,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["correlationId"] = correlationId;
        if (extensions is not null)
            foreach (var kv in extensions)
                problem.Extensions[kv.Key] = kv.Value;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
}
