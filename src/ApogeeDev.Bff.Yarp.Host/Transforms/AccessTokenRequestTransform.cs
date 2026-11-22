using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace ApogeeDev.Bff.Yarp.Host.Transforms;
public class AccessTokenRequestTransform(ILogger<AccessTokenRequestTransform> logger) : ITransformProvider
{
    ///  <inheritdoc/>
    public void ValidateRoute(TransformRouteValidationContext context) { }

    ///  <inheritdoc/>
    public void ValidateCluster(TransformClusterValidationContext context) { }

    ///  <inheritdoc/>
    public void Apply(TransformBuilderContext context)
    {
        context.AddRequestTransform(async transformContext =>
        {
            var httpContext = transformContext.HttpContext;
            
            // Retrieve access_token stored by cookie authentication ticket
            var accessToken = await httpContext.GetTokenAsync("access_token");
            if (!string.IsNullOrEmpty(accessToken))
            {
                // Inject Bearer header into the proxied upstream request
                transformContext.ProxyRequest.Headers.Authorization = 
                    new AuthenticationHeaderValue("Bearer", accessToken);
                logger.LogDebug("Injected access token into proxied request for {Method} {Path}",
                    transformContext.ProxyRequest.Method, transformContext.ProxyRequest.RequestUri);
            } 
            else
            {
                logger.LogWarning("No access token found for proxied request {Method} {Path}",
                    transformContext.ProxyRequest.Method, transformContext.ProxyRequest.RequestUri);
            }

            // Strip the browser session cookie so it does not leak to internal services
            transformContext.ProxyRequest.Headers.Remove("Cookie");
        });
    }
}