using System.Security.Claims;
using ApogeeDev.Bff.Yarp.Host.Auth;
using ApogeeDev.Bff.Yarp.Host.Config;
using ApogeeDev.Bff.Yarp.Host.Services;
using ApogeeDev.Bff.Yarp.Host.Transforms;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog;

namespace ApogeeDev.Bff.Yarp.Host;

public class Startup(ConfigurationManager configuration, IWebHostEnvironment environment)
{
    public const string EnvVarPrefix = "BFF_";
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSerilog((services, lc) => lc
            .ReadFrom.Configuration(configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext());
        
        services.AddHealthChecks();

        services.AddOptions<OidcOptions>()
            .BindConfiguration(OidcOptions.SectionName)
            .Validate(
                o => !string.IsNullOrWhiteSpace(o.Authority) && !string.IsNullOrWhiteSpace(o.ClientId),
                "Missing required Oidc Authority or ClientId configuration.")
            .ValidateOnStart();

        var oidcOptions = configuration.GetSection(OidcOptions.SectionName).Get<OidcOptions>()
            ?? throw new InvalidOperationException("missing oidc configuration");

        services.AddSingleton<BffCookieAuthenticationEvents>();
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IDistributedCache, ILogger<AppTicketStore>>((options, cache, logger) =>
            {
                var ticketStore = new AppTicketStore(cache, TicketSerializer.Default, logger);
                options.SessionStore = ticketStore;
            });

        // 1. Authentication & OIDC Setup
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "__Host-bff-session";
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.Path = "/";
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.EventsType = typeof(BffCookieAuthenticationEvents);
        })
        .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.Authority = oidcOptions.Authority;
            options.ClientId = oidcOptions.ClientId;
            options.ClientSecret = oidcOptions.ClientSecret; // Confidential client
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;

            options.CallbackPath = oidcOptions.CallbackPath;
            options.SignedOutCallbackPath = oidcOptions.SignedOutCallbackPath;
            options.RemoteSignOutPath = oidcOptions.RemoteSignOutPath;

            options.SaveTokens = true; // Stores access_token, refresh_token, id_token in authentication session
            options.GetClaimsFromUserInfoEndpoint = true; // Set to true only if thin tokens are used
            options.MapInboundClaims = false;

            options.ClaimActions.MapUniqueJsonKey("picture", "picture");
            options.ClaimActions.MapUniqueJsonKey("username", "username");
            options.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");
            options.ClaimActions.MapUniqueJsonKey("email_verified", "email_verified");
            options.ClaimActions.MapUniqueJsonKey("roles", "roles");
            options.ClaimActions.MapUniqueJsonKey("given_name", ClaimTypes.GivenName);
            options.ClaimActions.MapUniqueJsonKey("family_name", ClaimTypes.Surname);

            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            options.Scope.Add("offline_access"); // Request refresh_token

            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
        });

        services.AddAuthorization();

        // 2. Configure YARP with Token Transform
        services.AddReverseProxy()
            .LoadFromConfig(configuration.GetSection("ReverseProxy"))
            .AddTransforms<AccessTokenRequestTransform>();

        // Antiforgery protection for state-modifying requests from SPA
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-XSRF-TOKEN";
            options.Cookie.Name = "__Host-XSRF-TOKEN";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        });

        services.AddStackExchangeRedisCache(o =>
        {
            o.InstanceName = $"{configuration["AppOptions:AppName"]}:{environment.EnvironmentName}:";
            var redisConfig = configuration.GetConnectionString("Redis")
                   ?? throw new InvalidOperationException("missing redis configuration");

            o.Configuration = Uri.TryCreate(redisConfig, UriKind.Absolute, out var uri) && uri.Scheme is "redis" or "rediss"
                ? $"{uri.Host}:{(uri.Port > 0 ? uri.Port : 6379)},ssl={uri.Scheme == "rediss"}"
                : redisConfig;
        });
    }
}
