# Native AOT publish + chiseled runtime — Phase A single-container mailer.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot@sha256:977c117b98abe301eeb4c2ed3f8deebf8b8f11e65549ee7886581d2b41641adb AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/Amane.Mailer.Contracts/ ./src/Amane.Mailer.Contracts/
COPY src/Amane.Mailer/ ./src/Amane.Mailer/

ARG TARGETARCH
ARG SOURCE_COMMIT=unknown
ARG MAILER_VERSION=0.0.0
RUN target_arch="${TARGETARCH:-amd64}" \
    && case "${target_arch}" in \
        amd64) runtime_id="linux-x64" ;; \
        arm64) runtime_id="linux-arm64" ;; \
        *) echo "Unsupported Docker target architecture: ${target_arch}" >&2; exit 1 ;; \
    esac \
    && dotnet restore src/Amane.Mailer/Amane.Mailer.csproj --locked-mode \
    && dotnet publish src/Amane.Mailer/Amane.Mailer.csproj \
    -c Release \
    -r "${runtime_id}" \
    --self-contained \
    --no-restore \
    -o /app/publish \
    /p:PublishAot=true \
    /p:IlcTreatWarningsAsErrors=true \
    /p:Version="${MAILER_VERSION}" \
    /p:InformationalVersion="${MAILER_VERSION}+${SOURCE_COMMIT}" \
    /p:IncludeSourceRevisionInInformationalVersion=false

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:bc6ba0158e93277ca1a5bc0881d13b08e6ca7f6d98db623592095eaf7fd7a816 AS runtime
ARG SOURCE_COMMIT=unknown
ARG MAILER_VERSION=0.0.0
LABEL org.opencontainers.image.revision="${SOURCE_COMMIT}" \
      org.opencontainers.image.version="${MAILER_VERSION}"
WORKDIR /app

COPY --from=build /app/publish/ ./
# Deployment-specific tenant configuration should be mounted by compose.
# The image only carries safe examples and schema files.
COPY config/mailer/tenants.example.json \
    config/mailer/tenants.local-acs.json.example \
    config/mailer/tenants.schema.json \
    ./config/mailer/

VOLUME ["/app/data"]

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=20s \
    CMD ["/app/Amane.Mailer", "healthcheck"]

USER $APP_UID
ENTRYPOINT ["./Amane.Mailer"]
