using ApogeeDev.Bff.Yarp.Host;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.HttpResults;
using Serilog;
using Serilog.Formatting.Compact;
using Microsoft.AspNetCore.Antiforgery;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new CompactJsonFormatter())
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
    .AddJsonFile("secrets.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: Startup.EnvVarPrefix);

var startup = new Startup(builder.Configuration, builder.Environment);

startup.ConfigureServices(builder.Services);

var app = builder.Build();

app.UseSerilogRequestLogging();

app.UseDefaultFiles();
app.UseStaticFiles();

// app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication(); // 1. Authenticate user from session cookie
app.UseAuthorization();  // 2. Evaluate route policies

// 3. BFF Auth Handlers (Mapped Minimal APIs)
app.MapGet(builder.Configuration["AppOptions:BffLoginPath"]!, (string? returnUrl, HttpContext context) =>
{
    var redirectUri = string.IsNullOrEmpty(returnUrl) || !RedirectHttpResult.IsLocalUrl(returnUrl)
        ? "/"
        : returnUrl;

    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = redirectUri },
        [OpenIdConnectDefaults.AuthenticationScheme]
    );
});

if (builder.Environment.IsDevelopment())
{
    app.MapGet(builder.Configuration["AppOptions:BffLogoutPath"]!, (HttpContext context) =>
    {
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
        );
    });
}
app.MapPost(builder.Configuration["AppOptions:BffLogoutPath"]!, async (
    HttpContext context,
    IAntiforgery antiforgery) =>
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new { error = "Invalid or missing CSRF token." });
        }
        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]
        );
    }).RequireAuthorization();


app.MapGet(builder.Configuration["AppOptions:BffUserInfoPath"]!, (
    ClaimsPrincipal user,
    HttpContext context,
    IAntiforgery antiforgery) =>
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return Results.Json(new { isAuthenticated = false });
        }
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Json(new
        {
            isAuthenticated = true,
            csrfToken = tokens.RequestToken,
            csrfFormFieldName = tokens.FormFieldName, // "__RequestVerificationToken"
            claims = user.Claims.Select(c => new { c.Type, c.Value })
        });
    }).RequireAuthorization();

// 4. Map YARP Endpoints
app.MapReverseProxy(); // Proxies matched routes to target clusters

app.MapFallbackToFile("index.html");

app.MapHealthChecks("/health").AllowAnonymous();

app.Run();