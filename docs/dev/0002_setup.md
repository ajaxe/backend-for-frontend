# 0002: Docker Setup & Production Deployment Guide (`Dockerfile.yarp`)

## 1. Overview

`ApogeeDev.Bff.Yarp.Host` is packaged as a generic, stateless **Backend-for-Frontend (BFF) Security Proxy** container image using [`Dockerfile.yarp`](../../Dockerfile.yarp). Because the container image is designed to be reusable across different Single-Page Applications (SPAs) and upstream microservice topologies, runtime customization is achieved without rebuilding the image by:

1. **Mounting the compiled SPA bundle** into the container's `wwwroot` directory (`/home/app/wwwroot`).
2. **Overriding `appsettings.Production.json`** via a bind-mounted file to configure declarative **YARP (`ReverseProxy`)** routes and upstream cluster destinations.
3. **Injecting secrets and environment-specific settings** (Redis connection string, OIDC `ClientId`, `ClientSecret`, and `Authority`) using `BFF_`-prefixed environment variables.

---

## 2. Container Image Architecture (`Dockerfile.yarp`)

[`Dockerfile.yarp`](../../Dockerfile.yarp) uses a multi-stage Alpine Linux build:

- **Build Stage (`mcr.microsoft.com/dotnet/sdk:10.0-alpine`):** Restores and publishes `ApogeeDev.Bff.Yarp.Host.csproj` in `Release` configuration targeting `linux-musl-x64`.
- **Runtime Stage (`mcr.microsoft.com/dotnet/aspnet:10.0-alpine`):**
  - **Working Directory:** `/home/app`
  - **Execution User:** Non-root user `app` (`USER app`)
  - **Default Environment:**
    - `ASPNETCORE_URLS=http://*:5000`
    - `ASPNETCORE_ENVIRONMENT=Production`
  - **Static Assets Volume:** `VOLUME /home/app/wwwroot`
  - **Exposed Port:** `5000` (HTTP)
  - **Health Check:** Probes `http://localhost:5000/health` every `30s` via `curl`.

### Building the Docker Image

From the repository root, build the image using `Dockerfile.yarp`:

```bash
docker build -f Dockerfile.yarp -t apogeedev/bff-yarp-host:latest .
```

> [!NOTE]
> Verify that line `COPY ./src/ApogeeDev.Bff.Yarp.Host/ApogeeDev.Bff.Yarp.Host .` in `Dockerfile.yarp` matches the project file path (`./src/ApogeeDev.Bff.Yarp.Host/ApogeeDev.Bff.Yarp.Host.csproj`) in your build context if running a fresh build.

---

## 3. Configuration Loading Order

In [`Program.cs`](../../src/ApogeeDev.Bff.Yarp.Host/Program.cs), configuration sources are registered in the following order (later sources override earlier ones):

1. `appsettings.json` *(baked into the image — default Serilog, `AppOptions`, and placeholder `Oidc` settings)*
2. `appsettings.{ASPNETCORE_ENVIRONMENT}.json` *(resolves to `appsettings.Production.json` in the container)*
3. `secrets.json` *(optional; intended for local development only)*
4. Environment Variables prefixed with `BFF_` (`Startup.EnvVarPrefix = "BFF_"`)

---

## 4. Required Runtime Mappings & Overrides

### 4.1 Mapping the SPA Static Assets (`/wwwroot`)

The BFF host serves static frontend files and falls back unmatched navigation routes to `index.html`:

```csharp
app.UseDefaultFiles();
app.UseStaticFiles();
// ...
app.MapFallbackToFile("index.html");
```

Inside the container, ASP.NET Core resolves the web root (`wwwroot`) relative to the working directory `/home/app`, which corresponds to **`/home/app/wwwroot`**.

Mount your SPA's compiled build output directory (e.g., `./dist` or `./build` containing `index.html` and static assets) to `/home/app/wwwroot`:

```text
Host: ./spa-dist/          --->  Container: /home/app/wwwroot/
      ├── index.html                        ├── index.html
      └── assets/                           └── assets/
```

---

### 4.2 Defining Upstream Routing with `appsettings.Production.json`

The base [`appsettings.json`](../../src/ApogeeDev.Bff.Yarp.Host/appsettings.json) does not define YARP routes or clusters. In local development, [`src/ApogeeDev.Bff.Yarp.Host/appsettings.Development.json`](../../src/ApogeeDev.Bff.Yarp.Host/appsettings.Development.json) defines the `ReverseProxy` configuration:

```json
{
  "ReverseProxy": {
    "Routes": {
      "route1": {
        "ClusterId": "cluster1",
        "AuthorizationPolicy": "Default",
        "Match": {
          "Path": "/api/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "cluster1": {
        "Destinations": {
          "destination1": {
            "Address": "http://localhost:5065"
          }
        }
      }
    }
  }
}
```

For container deployments (`ASPNETCORE_ENVIRONMENT=Production`), create an **`appsettings.Production.json`** file on the host using `appsettings.Development.json` as the reference template, updating the cluster destination addresses to point to your production/internal upstream microservice URLs:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    }
  },
  "ReverseProxy": {
    "Routes": {
      "route1": {
        "ClusterId": "cluster1",
        "AuthorizationPolicy": "Default",
        "Match": {
          "Path": "/api/{**catch-all}"
        }
      }
    },
    "Clusters": {
      "cluster1": {
        "Destinations": {
          "destination1": {
            "Address": "http://upstream-backend-api:8080"
          }
        }
      }
    }
  }
}
```

Mount this file into `/home/app/appsettings.Production.json` in the container:

```text
Host: ./appsettings.Production.json  --->  Container: /home/app/appsettings.Production.json:ro
```

- **`AuthorizationPolicy: "Default"`:** Ensures YARP requires an authenticated BFF session before proxying requests matching `/api/{**catch-all}`.
- **Cookie-to-Bearer Transformation:** For every matched route, `AccessTokenRequestTransform` automatically extracts the user's `access_token` from the Redis-backed session, attaches `Authorization: Bearer <access_token>`, and strips the browser `Cookie` header before forwarding to `destination1`.

---

### 4.3 Overriding Secrets via `BFF_` Environment Variables

Never store sensitive credentials (`ClientSecret`, Redis passwords) in `appsettings.Production.json`. Instead, override them at runtime using environment variables prefixed with **`BFF_`**.

ASP.NET Core strips the `BFF_` prefix and translates double underscores (`__`) into hierarchical configuration keys (`:`):

| Environment Variable | Maps to Configuration Key | Required | Description / Example |
| :--- | :--- | :---: | :--- |
| `BFF_ConnectionStrings__Redis` | `ConnectionStrings:Redis` | **Yes** | Redis connection string or URI (e.g., `redis://redis:6379`, `rediss://redis:6379`, or `redis:6379,password=secret,ssl=false`). Startup fails immediately if missing. |
| `BFF_Oidc__Authority` | `Oidc:Authority` | **Yes** | Upstream OIDC Identity Provider URL (e.g., `https://idp.example.com`). |
| `BFF_Oidc__ClientId` | `Oidc:ClientId` | **Yes** | OIDC Confidential Client ID registered in the IdP. |
| `BFF_Oidc__ClientSecret` | `Oidc:ClientSecret` | **Yes** | OIDC Confidential Client Secret. |
| `BFF_AppOptions__AppName` | `AppOptions:AppName` | No | Redis key prefix namespace (`<AppName>:<Env>:`). Defaults to `apogeedev-bff-yarp-host`. |
| `BFF_AppOptions__BffLoginPath` | `AppOptions:BffLoginPath` | No | Login endpoint path. Defaults to `/bff/login`. |
| `BFF_AppOptions__BffLogoutPath` | `AppOptions:BffLogoutPath` | No | Logout endpoint path. Defaults to `/bff/logout`. |
| `BFF_AppOptions__BffUserInfoPath` | `AppOptions:BffUserInfoPath` | No | UserInfo endpoint path. Defaults to `/bff/userinfo`. |

> [!TIP]
> You can also override individual YARP destination addresses via environment variables if needed (for example, `BFF_ReverseProxy__Clusters__cluster1__Destinations__destination1__Address=http://upstream-api:8080`).

---

## 5. Running the Container

### Option A: `docker run` CLI

```bash
docker run -d \
  --name bff-yarp-proxy \
  -p 5000:5000 \
  -v "$(pwd)/spa-dist:/home/app/wwwroot:ro" \
  -v "$(pwd)/appsettings.Production.json:/home/app/appsettings.Production.json:ro" \
  -e BFF_ConnectionStrings__Redis="redis://redis:6379" \
  -e BFF_Oidc__Authority="https://idp.example.com" \
  -e BFF_Oidc__ClientId="bff-production-client" \
  -e BFF_Oidc__ClientSecret="your-production-client-secret" \
  apogeedev/bff-yarp-host:latest
```

### Option B: `docker-compose.yml`

```yaml
services:
  bff-proxy:
    build:
      context: .
      dockerfile: Dockerfile.yarp
    image: apogeedev/bff-yarp-host:latest
    container_name: bff-yarp-proxy
    ports:
      - "5000:5000"
    volumes:
      # 1. Mount compiled SPA static bundle to /home/app/wwwroot
      - ./spa-dist:/home/app/wwwroot:ro
      # 2. Override YARP routing configuration for Production
      - ./appsettings.Production.json:/home/app/appsettings.Production.json:ro
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      # 3. Override secrets & infrastructure settings via BFF_ env vars
      BFF_ConnectionStrings__Redis: "redis://redis:6379"
      BFF_Oidc__Authority: "https://idp.example.com"
      BFF_Oidc__ClientId: "bff-production-client"
      BFF_Oidc__ClientSecret: "${OIDC_CLIENT_SECRET}"
    depends_on:
      - redis
    restart: unless-stopped

  redis:
    image: redis:7-alpine
    container_name: bff-redis
    ports:
      - "6379:6379"
    restart: unless-stopped
```

---

## 6. Production Deployment Checklist & Gotchas

1. **TLS Termination & `__Host-` Cookie Requirement:**
   - The BFF issues `__Host-bff-session` and `__Host-XSRF-TOKEN` cookies with `SecurePolicy = CookieSecurePolicy.Always` and `Path = "/"`.
   - Browsers strictly reject `__Host-` cookies over plain HTTP (except `localhost`). In production, always run the BFF container behind a TLS-terminating reverse proxy/ingress (e.g., Traefik, NGINX, Cloudflare, or AWS ALB) serving `https://`.
2. **Access Token Format (JWS vs. JWE):**
   - Ensure the OIDC Client (`BFF_Oidc__ClientId`) is configured in your Identity Provider with access token encryption disabled (`SkipTokenEncryption = true`) if upstream microservices validate JWTs directly via standard JWKS discovery. See [0001: JWE Access Token Validation](./0001_jwe_access_token_validation.md) for details.
3. **File Permissions for Non-Root User (`USER app`):**
   - The container runs under the non-root `app` user. Ensure host-mounted files (`appsettings.Production.json` and the `spa-dist` contents mapped to `/home/app/wwwroot`) have world-readable permissions (`chmod a+r`) so the container process can read them.
4. **Health Check Endpoint:**
   - Container orchestrators (Docker Swarm, Kubernetes, ECS) can probe `GET http://localhost:5000/health` (mapped anonymously via `app.MapHealthChecks("/health").AllowAnonymous()`).
