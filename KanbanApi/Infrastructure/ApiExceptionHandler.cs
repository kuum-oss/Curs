using KanbanApi.Application;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KanbanApi.Infrastructure;

public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, message) = exception switch
        {
            ApiException known when known.Status is >= 400 and < 500 => (known.Status, known.Code, known.Message),
            DbUpdateConcurrencyException => (409, "concurrency_conflict", "Дані змінилися. Оновіть їх і повторіть дію."),
            _ => (500, "internal_error", "Не вдалося виконати запит."),
        };
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status, Title = message,
                Extensions = { ["code"] = code, ["traceId"] = context.TraceIdentifier },
            },
        });
    }
}
