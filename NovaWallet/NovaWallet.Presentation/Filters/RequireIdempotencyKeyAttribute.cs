using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NovaWallet.Presentation.Filters;

/// <summary>
/// Validates the Idempotency-Key header before the action runs.
/// Short-circuits with RFC 7807 ProblemDetails on failure so the controller stays logic-free.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireIdempotencyKeyAttribute : ActionFilterAttribute
{
    public const string HeaderName = "Idempotency-Key";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var key = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(key) || key.Length > 255)
        {
            var problem = new ProblemDetails
            {
                Type = "https://novawallet.io/errors/invalid-idempotency-key",
                Title = "Invalid Idempotency-Key",
                Status = StatusCodes.Status400BadRequest,
                Detail = "Idempotency-Key header is required and must be ≤ 255 characters.",
                Instance = context.HttpContext.Request.Path
            };
            context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }
}
