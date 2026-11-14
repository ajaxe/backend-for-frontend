using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // 1. Point to your OIDC provider (enables automatic JWKS key discovery)
        options.Authority = builder.Configuration["Authentication:Authority"];

        // 2. Specify the expected 'aud' claim for this API
        // options.Audience = builder.Configuration["Authentication:Audience"];

        // 3. Enforce HTTPS for metadata discovery (set to false only for local HTTP dev)
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        // 4. Customize validation parameters and claim mappings
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            
            // Optional: Map standard OIDC claims to ASP.NET Core User.Identity properties
            NameClaimType = "name",
            RoleClaimType = "roles",

            // Optional: Reduce default 5-minute clock skew tolerance
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/profile", (HttpContext context) =>
{
    var claims = context.User.Claims.Select(c => new { c.Type, c.Value });
    return Results.Ok(claims);
}).RequireAuthorization();

app.Run();
