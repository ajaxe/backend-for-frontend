# AGENTS.md

## 1. Project Mission
This repository provides dual, feature-equivalent **generic Backend-for-Frontend (BFF) security proxies** implemented in **.NET (C# / YARP)** and **Go (Golang)** to secure Single-Page Applications (SPAs) and frontend workloads. Both proxies implement the **OAuth 2.0 / OpenID Connect (OIDC) Confidential Client** pattern—terminating tokens at the edge, managing hardened `HttpOnly` encrypted/distributed sessions, and translating browser cookies into `Authorization: Bearer` headers for upstream microservice APIs.

---

## 2. Tech Stack & Core Dependencies

### .NET Implementation (Current)
- **Runtime & Framework:** **.NET 10.0** (`net10.0`), ASP.NET Core Minimal APIs.
- **Reverse Proxy Engine:** **YARP (Yet Another Reverse Proxy)** (`Yarp.ReverseProxy` v2.3.0).
- **Authentication & Identity:** `Microsoft.AspNetCore.Authentication.OpenIdConnect` (v10.0.15) & Cookie Authentication for BFF edge; `Microsoft.AspNetCore.Authentication.JwtBearer` (v10.0.15) for upstream sample API validation.
- **Distributed State & Caching:** **Redis** via `Microsoft.Extensions.Caching.StackExchangeRedis` (v11.0.0).
- **Observability & Logging:** **Serilog** (`Serilog.AspNetCore` v10.0.0) with structured JSON output (`Serilog.Formatting.Compact` v3.0.0).

### Go Implementation (Planned / Co-located)
- **Runtime & Standard Library:** **Go (Golang)** utilizing `net/http` and `net/http/httputil.ReverseProxy`.
- **Authentication & OIDC:** `coreos/go-oidc` and `golang.org/x/oauth2` for OIDC Authorization Code flow and JWKS verification.
- **Session & Caching:** **Redis** (`redis/go-redis`) for distributed session/token storage matching the .NET session contract.
- **Observability & Logging:** Structured JSON logging via standard library `log/slog` (or `uber-go/zap`).

### Shared Infrastructure
- **Identity Provider (IdP):** Any compliant **OIDC Authority** (e.g., Duende IdentityServer, ZITADEL, Keycloak, Entra ID) supporting Authorization Code flow + PKCE and Refresh Tokens (`offline_access`).
- **Session Store:** **Redis** instance keyed by application name and environment prefix.

---

## 3. Architecture & Design Patterns

- **Zero-Trust Browser Token Mediation:** Browser clients **never** touch OAuth access or refresh tokens. The BFF acts as an **OIDC Confidential Client** (`response_type=code`), storing `access_token`, `refresh_token`, and `id_token` server-side/in encrypted session state and issuing a hardened session cookie (`__Host-bff-session`) to the browser.
- **Cookie-to-Bearer Request Transformation:**
  - In **.NET**, `AccessTokenRequestTransform` (`ITransformProvider`) intercepts proxied routes, extracts the `access_token` from the authenticated `HttpContext`, attaches `Authorization: Bearer <token>` to the upstream request, and **strips the `Cookie` header** to prevent session leakage to internal services.
  - In **Go**, equivalent `httputil.ReverseProxy` `Rewrite` / middleware directors must extract the session token from Redis/cookie context, inject the `Bearer` header, and strip incoming browser cookies before forwarding.
- **Hardened Cookie & CSRF Semantics:**
  - Session cookies enforce `__Host-` prefix invariants (`__Host-bff-session`, `SameSite=Strict`, `HttpOnly=true`, `Secure=Always`, `Path=/`, 8-hour sliding expiration).
  - State-modifying requests from the SPA are protected by **Antiforgery / CSRF** tokens (`__Host-XSRF-TOKEN` cookie paired with `X-XSRF-TOKEN` request header).
- **Declarative Route & Cluster Configuration:** Upstream routing, path matching (`/api/{**catch-all}`), and authorization policies are driven entirely by configuration (`ReverseProxy` section in `appsettings.json` / environment variables) rather than hardcoded endpoint proxies.
- **SPA Host & Fallback:** The BFF serves static frontend assets (`UseDefaultFiles`, `UseStaticFiles`) and falls back unmatched navigation routes to `index.html` (`MapFallbackToFile("index.html")`).
- **Standard BFF Management Endpoints:**
  - `GET /bff/login` (`AppOptions:BffLoginPath`): Initiates OIDC `Challenge` with open-redirect validation (`RedirectHttpResult.IsLocalUrl`).
  - `GET /bff/logout` (`AppOptions:BffLogoutPath`): Clears local cookie session and triggers upstream OIDC RP-Initiated Logout (`SignOut`).
  - `GET /bff/userinfo` (`AppOptions:BffUserInfoPath`): Returns authenticated status and normalized user claims as JSON (requires authorization).

---

## 4. Directory Mental Model

```text
backend-for-frontend/
├── .vscode/
│   ├── launch.json                          # Debug profiles (BFF-Proxy, BFF-Upstream, and compound Test-Stack)
│   └── tasks.json                           # Build tasks for proxy and sample upstream
├── src/
│   ├── ApogeeDev.Bff.Yarp.Host/             # .NET 10 Generic BFF Proxy (YARP)
│   │   ├── Properties/launchSettings.json   # Local dev ports (HTTP :5084, HTTPS :7033)
│   │   ├── Transforms/
│   │   │   └── AccessTokenRequestTransform.cs # Injects Bearer token & strips Cookie header
│   │   ├── Program.cs                       # Bootstrap, middleware pipeline, Minimal API BFF endpoints, YARP mapping
│   │   ├── Startup.cs                       # DI registration: Serilog, Cookie+OIDC Auth, YARP, Antiforgery, Redis
│   │   ├── appsettings.json                 # Base config: Serilog, AppOptions (/bff/* paths), Oidc defaults
│   │   ├── appsettings.Development.json     # Dev YARP routes (/api/{**catch-all}) & clusters (localhost:5065)
│   │   └── secrets.json                     # Local uncommitted secrets (gitignored)
│   ├── ApogeeDev.Sample.Backend.Host/       # Sample Upstream Protected API (.NET 10)
│   │   ├── Properties/launchSettings.json   # Local dev ports (HTTP :5065, HTTPS :7007)
│   │   ├── Program.cs                       # JWT Bearer validation & sample GET /api/profile endpoint
│   │   └── appsettings.Development.json     # Upstream OIDC Authority & Audience config
│   └── go-bff/                              # (Planned/Reserved) Golang Generic BFF Proxy implementation
│       ├── cmd/bff/                         # Go entrypoint (main.go)
│       ├── internal/auth/                   # OIDC confidential client, cookie & CSRF management
│       ├── internal/proxy/                  # httputil.ReverseProxy director & Bearer token injector
│       └── internal/config/                 # Config & BFF_ environment variable loader
└── AGENTS.md                                # Contextual anchor for AI coding agents
```

---

## 5. Development Standards

### Naming & Structural Conventions
- **.NET:**
  - Use **file-scoped namespaces** (`namespace ApogeeDev.Bff.Yarp.Host;`), primary constructors for DI classes (`public class Startup(...)`), and `Nullable` reference types enabled.
  - Prefix all BFF environment variable overrides with `BFF_` (`Startup.EnvVarPrefix = "BFF_"`).
  - Keep startup orchestration split cleanly: service registration in `Startup.ConfigureServices`, and HTTP pipeline + endpoint routing in `Program.cs`.
- **Golang:**
  - Follow standard Go project layout (`cmd/`, `internal/`). Use `camelCase` for internal identifiers and `PascalCase` for exported types.
  - Maintain parity with the .NET BFF contract: support `BFF_` environment variable overrides, identical `/bff/login`, `/bff/logout`, `/bff/userinfo` endpoints, and `__Host-bff-session` / `__Host-XSRF-TOKEN` cookie names.
- **Redis Key Namespacing:**
  - Prefix all Redis cache keys using the pattern `<AppName>:<EnvironmentName>:` (e.g., `ApogeeDev.Bff.Yarp.Host:Development:`).

### Error Handling & Logging Strategies
- **Structured JSON Logging Only:** All console output must be machine-parsable compact JSON (`CompactJsonFormatter` in Serilog; JSON handler in Go `slog`).
- **Bootstrap Logging:** Initialize a bootstrap logger before host construction so startup and configuration failures are captured in structured format.
- **Fail-Fast Configuration Validation:** Critical infrastructure dependencies (such as the `redis` connection string or OIDC authority settings) must fail immediately during startup (`InvalidOperationException` in .NET / `log.Fatal` in Go) rather than failing silently at request time.

### Specific Implementation Patterns
- **Middleware Ordering (Critical):**
  1. `UseSerilogRequestLogging()`
  2. `UseDefaultFiles()` & `UseStaticFiles()`
  3. `UseRouting()`
  4. `UseAuthentication()`
  5. `UseAuthorization()`
  6. BFF Minimal API auth handlers (`/bff/login`, `/bff/logout`, `/bff/userinfo`)
  7. `MapReverseProxy()`
  8. `MapFallbackToFile("index.html")`
- **Open Redirect Prevention:** Always validate `returnUrl` parameters on `/bff/login` using `RedirectHttpResult.IsLocalUrl(returnUrl)` (or a strict relative-path check in Go) before passing to OIDC `AuthenticationProperties.RedirectUri`.
- **Claim Normalization:** Configure `NameClaimType = "name"` and `RoleClaimType = "roles"` across both BFF OIDC validation and upstream JWT Bearer validation.

---

## 6. Hard Constraints & Anti-Patterns

1. **DO NOT expose tokens to the browser:** Never return `access_token`, `refresh_token`, or `id_token` in `/bff/userinfo` responses, JSON payloads, response headers, or non-`HttpOnly` cookies.
2. **DO NOT forward browser cookies to upstream services:** Every proxied request MUST strip the incoming `Cookie` header (`transformContext.ProxyRequest.Headers.Remove("Cookie")`) after extracting the session token.
3. **DO NOT weaken `__Host-` cookie attributes:** Never set a `Domain` attribute or change `Path` from `/` on `__Host-bff-session` or `__Host-XSRF-TOKEN`, and never downgrade `SecurePolicy` from `Always` or `HttpOnly` from `true`.
4. **DO NOT allow unvalidated redirects:** Never pass an unvalidated query string `returnUrl` directly into an OIDC challenge or sign-out redirect.
5. **DO NOT commit secrets:** Never commit `secrets.json`, `.env` files, or real OIDC `ClientSecret` values to source control. Use `secrets.json` (which is gitignored) or `BFF_` environment variables for local development.
6. **DO NOT embed business/domain logic in the BFF:** Both the .NET and Go proxies are **generic** security and routing gateways. Domain-specific aggregation or business rules belong in upstream microservices, not in the BFF host.
7. **DO NOT reorder the ASP.NET Core / Go middleware pipeline:** Placing `MapReverseProxy()` before `UseAuthentication()`/`UseAuthorization()` will bypass token attachment and policy enforcement.

---

## 7. Operational Commands

### .NET BFF Proxy & Sample Upstream

```powershell
# Restore and build the BFF YARP Proxy
dotnet build src/ApogeeDev.Bff.Yarp.Host/ApogeeDev.Bff.Yarp.Host.csproj

# Restore and build the Sample Upstream Backend
dotnet build src/ApogeeDev.Sample.Backend.Host/ApogeeDev.Sample.Backend.Host.csproj

# Run the BFF YARP Proxy locally (HTTP :5084 / HTTPS :7033)
dotnet run --project src/ApogeeDev.Bff.Yarp.Host/ApogeeDev.Bff.Yarp.Host.csproj

# Run the Sample Upstream Backend locally (HTTP :5065 / HTTPS :7007)
dotnet run --project src/ApogeeDev.Sample.Backend.Host/ApogeeDev.Sample.Backend.Host.csproj

# Run .NET unit/integration tests (when test projects are present)
dotnet test
```

### Golang BFF Proxy (When `src/go-bff` is initialized)

```powershell
# Build the Go BFF Proxy binary
go build -C src/go-bff -o bin/bff ./cmd/bff

# Run the Go BFF Proxy locally
go run -C src/go-bff ./cmd/bff

# Execute Go test suite with race detector
go test -C src/go-bff -race ./...
```
