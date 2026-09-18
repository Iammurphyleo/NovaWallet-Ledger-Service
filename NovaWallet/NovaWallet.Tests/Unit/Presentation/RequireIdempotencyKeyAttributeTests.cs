using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using NovaWallet.Presentation.Filters;

namespace NovaWallet.Tests.Unit.Presentation;

public sealed class RequireIdempotencyKeyAttributeTests
{
    private static ActionExecutingContext BuildContext(string? headerValue = null)
    {
        var httpContext = new DefaultHttpContext();
        if (headerValue is not null)
            httpContext.Request.Headers[RequireIdempotencyKeyAttribute.HeaderName] = headerValue;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
    }

    private readonly RequireIdempotencyKeyAttribute _sut = new();

    [Fact]
    public void OnActionExecuting_MissingHeader_Sets400Result()
    {
        var ctx = BuildContext(headerValue: null);

        _sut.OnActionExecuting(ctx);

        ctx.Result.Should().NotBeNull();
        var result = ctx.Result as ObjectResult;
        result!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var problem = result.Value as ProblemDetails;
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void OnActionExecuting_EmptyHeader_Sets400Result()
    {
        var ctx = BuildContext(headerValue: "   ");

        _sut.OnActionExecuting(ctx);

        ctx.Result.Should().NotBeNull();
        (ctx.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void OnActionExecuting_HeaderExceeds255Chars_Sets400Result()
    {
        var ctx = BuildContext(headerValue: new string('x', 256));

        _sut.OnActionExecuting(ctx);

        ctx.Result.Should().NotBeNull();
        (ctx.Result as ObjectResult)!.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void OnActionExecuting_ValidHeader_DoesNotSetResult()
    {
        var ctx = BuildContext(headerValue: Guid.NewGuid().ToString());

        _sut.OnActionExecuting(ctx);

        ctx.Result.Should().BeNull();
    }

    [Fact]
    public void OnActionExecuting_ExactlyMaxLengthHeader_DoesNotSetResult()
    {
        var ctx = BuildContext(headerValue: new string('x', 255));

        _sut.OnActionExecuting(ctx);

        ctx.Result.Should().BeNull();
    }
}
