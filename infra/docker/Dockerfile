# Native AOT publish + chiseled runtime — Phase A single-container mailer.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot@sha256:977c117b98abe301eeb4c2ed3f8deebf8b8f11e65549ee7886581d2b41641adb AS build
ARG TARGETARCH
ARG DOTNET_SDK_VERSION=10.0.401
RUN case "${TARGETARCH}" in \
        amd64) \
            sdk_arch="x64"; \
            sdk_sha512="51c8b999af9e8dd9998c9edc5944e19a90788862068acd38694e098889054ce8c23d4f0c5cccfa16bf187d044562359e5ee69a9f8ad0bbe913ba90311fbce25b" \
            ;; \
        arm64) \
            sdk_arch="arm64"; \
            sdk_sha512="58ace73ced6b4360754689a686bdfb8a317f4da6cb8bb416dbc7d0ba9f47e43e3c09f5eb1f1a1cfaacbd10df9558da4882bf2a5e195d6ab56a02c1f9f76102ed" \
            ;; \
        *) echo "Unsupported Docker target architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && sdk_archive="/tmp/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${sdk_arch}.tar.gz" \
    && sdk_url="https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${sdk_arch}.tar.gz" \
    && curl --fail --show-error --location --retry 5 --output "${sdk_archive}" "${sdk_url}" \
    && printf '%s  %s\n' "${sdk_sha512}" "${sdk_archive}" | sha512sum --check --strict - \
    && tar -xzf "${sdk_archive}" -C /usr/share/dotnet \
    && rm -f "${sdk_archive}" \
    && test "$(dotnet --version)" = "${DOTNET_SDK_VERSION}"
WORKDIR /repo

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/Amane.Mailer.Contracts/ ./src/Amane.Mailer.Contracts/
COPY src/Amane.Mailer/ ./src/Amane.Mailer/

ARG SOURCE_COMMIT=unknown
ARG MAILER_VERSION=0.0.0
ARG SOURCE_DATE_EPOCH=
# CI=true enables the Contracts project's deterministic SourceLink/PDB path.
RUN target_arch="${TARGETARCH}" \
    && case "${target_arch}" in \
        amd64) runtime_id="linux-x64" ;; \
        arm64) runtime_id="linux-arm64" ;; \
        *) echo "Unsupported Docker target architecture: ${target_arch}" >&2; exit 1 ;; \
    esac \
    && if [ -n "${SOURCE_DATE_EPOCH}" ]; then deterministic_assets_timestamp="$(date -u -d "@${SOURCE_DATE_EPOCH}" '+%Y-%m-%dT%H:%M:%SZ')"; else deterministic_assets_timestamp=""; fi \
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
    /p:CI=true \
    /p:ContinuousIntegrationBuild=true \
    /p:IncludeSourceRevisionInInformationalVersion=false \
    /p:DeterministicStaticWebAssetsTimestamp="${deterministic_assets_timestamp}"

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
