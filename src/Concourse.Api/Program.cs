using Concourse.Application;
using Concourse.Api.Middleware;
using Concourse.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApplication()
    .AddInfrastructure();

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseStatusCodePages();
app.UseRouting();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGet("/", () =>
{
    const string page = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>Concourse API</title>
            <style>
                :root {
                    --bg: #f5f7fb;
                    --card: #ffffff;
                    --text: #0f172a;
                    --muted: #475569;
                    --primary: #0ea5e9;
                    --primary-hover: #0284c7;
                }
                * { box-sizing: border-box; }
                body {
                    margin: 0;
                    font-family: "Segoe UI", Tahoma, Geneva, Verdana, sans-serif;
                    background: linear-gradient(135deg, #f5f7fb 0%, #e2e8f0 100%);
                    color: var(--text);
                    min-height: 100vh;
                    display: grid;
                    place-items: center;
                    padding: 24px;
                }
                .card {
                    width: min(680px, 100%);
                    background: var(--card);
                    border-radius: 16px;
                    padding: 32px;
                    box-shadow: 0 20px 45px rgba(2, 8, 23, 0.12);
                    text-align: center;
                }
                h1 {
                    margin: 0 0 12px;
                    font-size: clamp(28px, 4vw, 40px);
                }
                p {
                    margin: 0 0 24px;
                    color: var(--muted);
                    font-size: 18px;
                    line-height: 1.5;
                }
                .btn {
                    display: inline-block;
                    text-decoration: none;
                    border: none;
                    border-radius: 10px;
                    padding: 12px 22px;
                    font-weight: 700;
                    background: var(--primary);
                    color: #fff;
                    transition: background .2s ease-in-out;
                }
                .btn:hover {
                    background: var(--primary-hover);
                }
            </style>
        </head>
        <body>
            <main class="card">
                <h1>Concourse API is Running</h1>
                <p>The application is working correctly. You can explore and test endpoints in Scalar.</p>
                <a class="btn" href="/scalar/v1">Open Scalar API Docs</a>
            </main>
        </body>
        </html>
        """;

    return Results.Content(page, "text/html");
})
.AllowAnonymous();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
