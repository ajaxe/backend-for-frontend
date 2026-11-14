# 0001: JWE Access Token Validation in Upstream Resource Servers

- **Status:** Accepted
- **Date:** 2026-11-14
- **Affected Components:**
  - `src/ApogeeDev.Sample.Backend.Host` (Upstream Resource Server)
  - `src/ApogeeDev.Bff.Yarp.Host` (BFF Security Proxy)
  - Identity Provider (`ApogeeDev.IdentityProvider.Host` / OpenIddict)

---

## 1. Issue Description

In the Backend-for-Frontend (BFF) architecture, `ApogeeDev.Bff.Yarp.Host` authenticates users via OpenID Connect (Authorization Code Flow) and stores the issued `access_token` inside an encrypted, server-side/cookie-backed session (`__Host-bff-session`). When the SPA makes requests to `/api/*`, `AccessTokenRequestTransform` extracts the `access_token` and forwards it as an `Authorization: Bearer <token>` header to upstream APIs such as `ApogeeDev.Sample.Backend.Host`.

`ApogeeDev.Sample.Backend.Host` uses `Microsoft.AspNetCore.Authentication.JwtBearer` configured with OIDC metadata discovery (`options.Authority`) to validate incoming Bearer tokens locally using the Identity Provider's public JWKS (`/.well-known/openid-configuration` -> `jwks_uri`).

By default, **OpenIddict** issues **JWE (JSON Web Encryption)** access tokens—5-segment dot-separated tokens (`header.encrypted_key.iv.ciphertext.tag`) whose payload is encrypted in addition to being signed.

When `ApogeeDev.Sample.Backend.Host` receives a 5-segment JWE access token:
1. `JsonWebTokenHandler` detects the JWE header and attempts to decrypt the token before verifying its signature.
2. Because OIDC discovery only publishes **public** keys (and never the private decryption key), local validation fails with:
   ```text
   IDX10609: Decryption failed. No Keys tried: token: 'System.String'.
   ```
3. To any resource server that does not possess the IdP's private encryption certificate, the JWE access token is effectively an **opaque token**.

---

## 2. Evaluated Options

| Option | Mechanism | Pros | Cons |
| :--- | :--- | :--- | :--- |
| **1. Local JWE Decryption (`TokenDecryptionKey`)** | Distribute the IdP's private encryption certificate (`.pfx`) to `ApogeeDev.Sample.Backend.Host` and configure `TokenValidationParameters.TokenDecryptionKey`. | Fast local validation; no extra HTTP hops per request. | Requires distributing and rotating a sensitive private key across every upstream resource server. |
| **2. Remote OAuth 2.0 Introspection (`/connect/introspect` or `/connect/userinfo`)** | Resource servers treat the JWE as opaque and call the IdP's introspection/userinfo endpoint (via a custom `TokenHandler`, `Duende.AspNetCore.Authentication.OAuth2Introspection`, or `OpenIddict.Validation`). | Private encryption key remains strictly on the IdP; supports immediate token revocation check. | Adds an extra network round-trip to the IdP for every API service (unless cached), requires provisioning client credentials for every resource server, and makes the IdP a runtime availability dependency. |
| **3. Disable Access Token Encryption for the BFF Client (Selected)** | Configure the BFF client registration in the IdP with `SkipTokenEncryption = true` (`DisableAccessTokenEncryption = true`) so the IdP issues a standard 3-segment signed **JWS** (`typ: "at+jwt"`). | Zero changes or extra dependencies required in upstream resource servers; works out-of-the-box with standard `JwtBearer` and public JWKS discovery; no runtime introspection latency. | Access token claims are base64url-readable on internal backend network hops (mitigated by the BFF boundary). |

---

## 3. Reason for the Choice

We selected **Option 3 (Disabling Access Token Encryption for the BFF Client)** for the following architectural reasons:

1. **The BFF Already Enforces Token Confidentiality at the Edge:**
   Identity Providers like OpenIddict encrypt access tokens by default to prevent public browser clients (SPAs storing tokens in `localStorage`/`sessionStorage`) from inspecting sensitive token claims. In our architecture, `ApogeeDev.Bff.Yarp.Host` acts as an **OIDC Confidential Client** and terminates tokens at the edge. The browser only ever receives an `HttpOnly`, `Secure`, `SameSite=Strict` session cookie (`__Host-bff-session`) and **never** sees the `access_token`.
2. **Simpler Resource Server Scaling:**
   If access tokens remain encrypted as JWEs without shared keys, **every** upstream microservice must integrate an introspection client, manage its own `ClientId`/`ClientSecret`, and perform network calls back to the Identity Provider. Issuing a signed 3-segment JWS allows all internal microservices to remain stateless and validate tokens locally using standard `Microsoft.AspNetCore.Authentication.JwtBearer` and public JWKS discovery.
3. **First-Class Support in the Identity Provider:**
   `ApogeeDev.IdentityProvider.Host` already includes a dedicated `ConfigureAccessTokenEncryption` sign-in event handler that checks the per-client `SkipTokenEncryption` property and sets `context.Options.DisableAccessTokenEncryption = true` dynamically per client without affecting other clients that still require JWE encryption.

---

## 4. Resolution Steps

### Step 1: Enable `SkipAccessTokenEncryption` on the BFF Client in the Identity Provider
1. Open the **IDP Manager UI** (or update the client registration via the `AppClient` API in `ApogeeDev.IdentityProvider.Host`).
2. Locate the OIDC Client registration used by `ApogeeDev.Bff.Yarp.Host` (configured under `Oidc:ClientId`).
3. Enable **Skip Access Token Encryption** (`SkipAccessTokenEncryption: true`), which persists the application property:
   ```json
   {
     "SkipTokenEncryption": true
   }
   ```
4. During token issuance (`/connect/token`), `ConfigureAccessTokenEncryption` reads this property and sets:
   ```csharp
   context.Options.DisableAccessTokenEncryption = true;
   ```
   The IdP will now issue a 3-segment signed JWS (`header.payload.signature`) instead of a 5-segment JWE.

### Step 2: Refresh the Active BFF Session
1. Sign out of the BFF via `GET /bff/logout` (or clear the `__Host-bff-session` cookie) to evict any previously cached JWE `access_token`.
2. Sign back in via `GET /bff/login` so the BFF stores a newly issued 3-segment JWS `access_token`.

### Step 3: Align Audience Validation in `ApogeeDev.Sample.Backend.Host`
In `src/ApogeeDev.Sample.Backend.Host/Program.cs`, `JwtBearer` validates the token signature, issuer, lifetime, and audience:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentication:Authority"];
        options.Audience = builder.Configuration["Authentication:Audience"];
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = "name",
            RoleClaimType = "roles",
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
```

- **If the IdP sets resource audiences** (`claimsPrincipal.SetResources(...)` during token exchange), ensure `Authentication:Audience` in `src/ApogeeDev.Sample.Backend.Host/appsettings.Development.json` matches the resource identifier emitted in the token's `aud` claim.
- **If the IdP does not emit an `aud` claim** on access tokens, set `ValidateAudience = false` in `TokenValidationParameters` to avoid `IDX10206: Unable to validate audience`.
