# NodeSet Editor — application image.
#
# Stateless: no volumes, no embedded database. PostgreSQL is supplied by the operator through
# ConnectionStrings__Postgres. See docker/DESIGN.md for the full design.
#
# Build from the REPOSITORY ROOT so the build context covers every referenced project:
#
#   docker build -f docker/Dockerfile -t nodeset-editor:dev .
#
# The UANodeSetSerializer submodule must be checked out — the server, the model library and the
# database tool all reference Opc.Ua.NodeSetSerializer from it:
#
#   git submodule update --init

# ---------------------------------------------------------------------------------------------
# 1. SPA. Built here rather than through the .esproj, which needs the Visual Studio JavaScript
#    SDK. The output is dropped into the server's wwwroot before publish (see stage 2) because
#    MapStaticAssets serves from a manifest generated at build time — files copied into wwwroot
#    after publish are not served.
# ---------------------------------------------------------------------------------------------
FROM node:22-bookworm-slim AS spa
WORKDIR /src/nodeseteditor.client

COPY nodeseteditor.client/package.json nodeseteditor.client/package-lock.json ./
RUN npm ci

COPY nodeseteditor.client/ ./

# Azure AD / MSAL is off by default: the SPA asks GET /api/config which providers exist, and the
# server reports Azure AD only when AzureAd__ClientId is set at runtime. These stay empty unless
# someone builds an image for a tenant of their own — in which case set all four here AND the
# matching AzureAd__* settings on the running container.
ARG VITE_MSAL_CLIENT_ID=
ARG VITE_MSAL_AUTHORITY=
ARG VITE_MSAL_SCOPE=
ARG VITE_REDIRECT_URL=

# Vite gives environment variables that already exist priority over .env files, so these override
# .env.production without a separate mode. The API is same-origin in the container.
#
# Set on the RUN rather than with ENV so they scope to this one command instead of persisting in
# the stage's image metadata. That also settles the SecretsUsedInArgOrEnv build warning, which
# fires on the NAME "VITE_DEV_AUTH" containing "AUTH" — the value is a feature flag reading
# "false", not a credential.
#
# No VITE_* value can ever be a secret regardless of how it is injected: Vite substitutes them at
# build time, so each one is compiled into the JavaScript bundle and served to every browser.
# Secrets belong in server-side configuration, which the SPA reaches through the API.
RUN VITE_BASE_API_URL=/api \
    VITE_DEV_AUTH=false \
    VITE_MSAL_ENABLE_LOGGER=false \
    VITE_MSAL_CLIENT_ID="$VITE_MSAL_CLIENT_ID" \
    VITE_MSAL_AUTHORITY="$VITE_MSAL_AUTHORITY" \
    VITE_MSAL_SCOPE="$VITE_MSAL_SCOPE" \
    VITE_REDIRECT_URL="$VITE_REDIRECT_URL" \
    npm run build

# ---------------------------------------------------------------------------------------------
# 2. Server and database tool.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so a source-only change does not invalidate the restore layer.
COPY NodeSetEditor.Server/NodeSetEditor.Server.csproj      NodeSetEditor.Server/
COPY NodeSetEditor.DbTool/NodeSetEditor.DbTool.csproj      NodeSetEditor.DbTool/
COPY NodeSetModel/NodeSetEditor.Model.csproj               NodeSetModel/
COPY Opc.Ua.RestfulApi/Opc.Ua.RestfulApi.csproj            Opc.Ua.RestfulApi/
COPY Opc.Ua.CloudLibraryApi/Opc.Ua.CloudLibraryApi.csproj  Opc.Ua.CloudLibraryApi/
# UANodeSetSerializer is a submodule with its own Directory.Build.props and version.json, so
# MSBuild resolves its build properties from inside it rather than from this repository.
COPY UANodeSetSerializer/Directory.Build.props UANodeSetSerializer/version.json UANodeSetSerializer/
COPY UANodeSetSerializer/Opc.Ua.NodeSetSerializer/Opc.Ua.NodeSetSerializer.csproj UANodeSetSerializer/Opc.Ua.NodeSetSerializer/

# SkipSpaProjectReference drops the .esproj reference (stage 1 built the SPA already).
# GitVersioningGitRepoRootIsExpected: the build context has no .git, and Nerdbank.GitVersioning
# in UANodeSetSerializer would otherwise fail rather than fall back to version.json.
ARG BUILD_PROPS="-p:SkipSpaProjectReference=true -p:GitVersioningGitRepoRootIsExpected=false"
RUN dotnet restore NodeSetEditor.Server/NodeSetEditor.Server.csproj $BUILD_PROPS \
 && dotnet restore NodeSetEditor.DbTool/NodeSetEditor.DbTool.csproj $BUILD_PROPS

COPY . .

# The SPA must be in wwwroot before publish so it lands in the static asset manifest.
COPY --from=spa /src/nodeseteditor.client/dist/ NodeSetEditor.Server/wwwroot/

# OpenApiGenerateDocumentsOnBuild is off: OpenApi/v1.json is generated on a developer machine and
# checked in, and the image serves that checked-in file. Regenerating here would run the app's
# entry point during the image build for a document the repository already has.
RUN dotnet publish NodeSetEditor.Server/NodeSetEditor.Server.csproj \
      -c Release -o /app/publish --no-restore \
      $BUILD_PROPS -p:OpenApiGenerateDocumentsOnBuild=false

RUN dotnet publish NodeSetEditor.DbTool/NodeSetEditor.DbTool.csproj \
      -c Release -o /app/dbtool --no-restore $BUILD_PROPS

# Seed data: the Core OPC UA NodeSet, fetched from the same place db/initialize_db.ps1 uses so
# the two initialization paths agree. Fetched rather than vendored: a pinned copy silently
# trails the published release (one such copy held 1.05.06 while `latest` was on 1.05.07).
#
# Baked into the image so first-run initialization needs no internet access. The version is
# therefore a property of the build: override the URL with a tag or commit to pin one.
ARG UA_CORE_NODESET_URL=https://raw.githubusercontent.com/OPCFoundation/UA-Nodeset/refs/heads/latest/Schema/Opc.Ua.NodeSet2.xml
ADD ${UA_CORE_NODESET_URL} /app/seed/Opc.Ua.NodeSet2.xml
# ADD from a URL writes the file 0600 root — unreadable by the non-root user the app runs as,
# which surfaces at first run as "Access to the path is denied" during initialize.
RUN chmod 0644 /app/seed/Opc.Ua.NodeSet2.xml \
 && grep -m1 -o '<Model [^>]*ModelUri="http://opcfoundation.org/UA/"[^>]*>' /app/seed/Opc.Ua.NodeSet2.xml \
    || (echo "Downloaded file is not a Core NodeSet" && exit 1)

# ---------------------------------------------------------------------------------------------
# 3. Runtime.
# ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# curl is here for HEALTHCHECK only; the runtime image ships without it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=build /app/dbtool ./dbtool/

# Seed data for first-run initialization, so a new database can be populated without reaching
# the internet. The Core NodeSet was fetched and version-checked in the build stage.
COPY --from=build /app/seed/Opc.Ua.NodeSet2.xml ./seed/
COPY db/grant_webapp.sql ./seed/

COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    # appsettings.json ships the OPC Foundation tenant's client id for the hosted deployment.
    # A self-hosted image has no claim on that tenant, so Azure AD is switched off here and the
    # server reports email-code sign-in only. Set AzureAd__ClientId (and rebuild the SPA with
    # the VITE_MSAL_* build args) to turn it back on for a tenant of your own.
    AzureAd__ClientId=

# 8081 (the setup/maintenance port, DESIGN.md §2) is deliberately not exposed here. It is added
# once the maintenance branch exists, and stays unpublished except during a maintenance window.
EXPOSE 8080

# /health is anonymous and reports 503 when the database is unreachable, so an instance that
# cannot serve is not marked healthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health >/dev/null || exit 1

USER $APP_UID

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["serve"]

# ---------------------------------------------------------------------------------------------
# Usage
#
#   Initialize a new database (the database itself must already exist):
#     docker run --rm -e ConnectionStrings__Postgres="Host=db;Database=nodeseteditor;..." \
#       nodeset-editor:dev initialize
#
#   Run:
#     docker run -p 8080:8080 \
#       -e ConnectionStrings__Postgres="Host=db;Database=nodeseteditor;Username=app;Password=..." \
#       -e EmailAuth__CookieSecret="$(openssl rand -base64 32)" \
#       -e Smtp__Host=smtp.example.com -e Smtp__From=noreply@example.com \
#       nodeset-editor:dev
#
#   These manual steps are what the setup/maintenance page replaces (DESIGN.md §3).
# ---------------------------------------------------------------------------------------------
