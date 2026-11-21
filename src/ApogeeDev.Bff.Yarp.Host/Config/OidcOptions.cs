namespace ApogeeDev.Bff.Yarp.Host.Config;

public class OidcOptions
{
    public const string SectionName = "Oidc";

    public string Authority { get; init; } = default!;
    public string ClientId { get; init; } = default!;
    public string ClientSecret { get; init; } = default!;
    public int RefreshThresholdSeconds { get; init; } = 60;
    public string CallbackPath { get; init; } = default!;
    public string SignedOutCallbackPath { get; init; } = default!;
    public string RemoteSignOutPath { get; init; } = default!;
}
