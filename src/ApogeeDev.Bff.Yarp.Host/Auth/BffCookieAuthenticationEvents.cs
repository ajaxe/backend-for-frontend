using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using ApogeeDev.Bff.Yarp.Host.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ApogeeDev.Bff.Yarp.Host.Auth;

public class BffCookieAuthenticationEvents(
    IOptions<OidcOptions> oidcConfigOptions,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptionsMonitor,
    ILogger<BffCookieAuthenticationEvents> logger) : CookieAuthenticationEvents
{
    private const string ExpiresAtTokenName = "expires_at";

    private readonly ConcurrentDictionary<string, Lazy<Task<OpenIdConnectMessage?>>> _inFlightRefreshes = new();

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var expiresAt = context.Properties.GetTokenValue(ExpiresAtTokenName);
        if (string.IsNullOrEmpty(expiresAt) ||
            !DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAtUtc))
        {
            return;
        }

        var oidcOptions = oidcOptionsMonitor.Get(OpenIdConnectDefaults.AuthenticationScheme);
        var oidcConfig = oidcConfigOptions.Value;
        var utcNow = oidcOptions.TimeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;

        if (expiresAtUtc > utcNow.AddSeconds(oidcConfig.RefreshThresholdSeconds))
        {
            return;
        }

        var refreshToken = context.Properties.GetTokenValue(OpenIdConnectParameterNames.RefreshToken);
        if (string.IsNullOrEmpty(refreshToken))
        {
            if (expiresAtUtc <= utcNow)
            {
                logger.LogWarning("Access token expired at {ExpiresAtUtc} and no refresh_token is available. Rejecting session principal.", expiresAtUtc);
                context.RejectPrincipal();
            }

            return;
        }

        var lazyRefresh = _inFlightRefreshes.GetOrAdd(
            refreshToken,
            rt => new Lazy<Task<OpenIdConnectMessage?>>(
                () => RedeemRefreshTokenAsync(rt, oidcOptions, oidcConfig, context.HttpContext.RequestAborted),
                LazyThreadSafetyMode.ExecutionAndPublication));

        OpenIdConnectMessage? tokenResponse;
        try
        {
            tokenResponse = await lazyRefresh.Value;
        }
        finally
        {
            _inFlightRefreshes.TryRemove(KeyValuePair.Create(refreshToken, lazyRefresh));
        }

        if (tokenResponse is null || string.IsNullOrEmpty(tokenResponse.AccessToken))
        {
            context.RejectPrincipal();
            return;
        }

        context.Properties.UpdateTokenValue(OpenIdConnectParameterNames.AccessToken, tokenResponse.AccessToken);

        if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
        {
            context.Properties.UpdateTokenValue(OpenIdConnectParameterNames.RefreshToken, tokenResponse.RefreshToken);
        }

        if (!string.IsNullOrEmpty(tokenResponse.IdToken))
        {
            context.Properties.UpdateTokenValue(OpenIdConnectParameterNames.IdToken, tokenResponse.IdToken);
        }

        var refreshedAtUtc = oidcOptions.TimeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        if (int.TryParse(tokenResponse.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresInSeconds))
        {
            var newExpiresAt = refreshedAtUtc.AddSeconds(expiresInSeconds).ToString("o", CultureInfo.InvariantCulture);
            context.Properties.UpdateTokenValue(ExpiresAtTokenName, newExpiresAt);
        }

        context.ShouldRenew = true;
        logger.LogDebug("Successfully renewed access_token using refresh_token.");
    }

    private async Task<OpenIdConnectMessage?> RedeemRefreshTokenAsync(
        string refreshToken,
        OpenIdConnectOptions oidcOptions,
        OidcOptions oidcConfig,
        CancellationToken cancellationToken)
    {
        if (oidcOptions.ConfigurationManager is null)
        {
            logger.LogError("OpenIdConnectOptions.ConfigurationManager is not configured; cannot discover token_endpoint.");
            return null;
        }

        try
        {
            var discoveryConfig = await oidcOptions.ConfigurationManager.GetConfigurationAsync(cancellationToken);
            if (string.IsNullOrEmpty(discoveryConfig.TokenEndpoint))
            {
                logger.LogError("OIDC discovery document did not contain a valid token_endpoint.");
                return null;
            }

            var requestMessage = new OpenIdConnectMessage
            {
                GrantType = OpenIdConnectGrantTypes.RefreshToken,
                ClientId = oidcConfig.ClientId,
                ClientSecret = oidcConfig.ClientSecret,
                RefreshToken = refreshToken
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, discoveryConfig.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(requestMessage.Parameters)
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await oidcOptions.Backchannel.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorMessage = TryParseMessage(payload);
                logger.LogWarning(
                    "Refresh token exchange failed with HTTP {StatusCode}. Error: {Error}, Description: {ErrorDescription}",
                    (int)response.StatusCode,
                    errorMessage?.Error,
                    errorMessage?.ErrorDescription);
                return null;
            }

            var tokenResponse = new OpenIdConnectMessage(payload);
            if (!string.IsNullOrEmpty(tokenResponse.Error) || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                logger.LogWarning(
                    "Refresh token response did not contain a valid access_token. Error: {Error}, Description: {ErrorDescription}",
                    tokenResponse.Error,
                    tokenResponse.ErrorDescription);
                return null;
            }

            return tokenResponse;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "An unexpected error occurred while redeeming the refresh_token.");
            return null;
        }
    }

    private static OpenIdConnectMessage? TryParseMessage(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return new OpenIdConnectMessage(payload);
        }
        catch
        {
            return null;
        }
    }
}
