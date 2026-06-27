# Native AOT publish (linux-x64) + chiseled runtime — Phase A single-container mailer.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot@sha256:47f56dc3e5bf7b62d878c98aa7b31848ac9167462789fa73220adcd1af064e37 AS build
WORKDIR /repo

COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json ./
COPY src/Amane.Mailer.Contracts/ ./src/Amane.Mailer.Contracts/
COPY src/Amane.Mailer/ ./src/Amane.Mailer/

RUN dotnet restore src/Amane.Mailer/Amane.Mailer.csproj --locked-mode \
    && dotnet publish src/Amane.Mailer/Amane.Mailer.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained \
    --no-restore \
    -o /app/publish \
    /p:PublishAot=true \
    /p:IlcTreatWarningsAsErrors=true

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled@sha256:01bd3f3d617a16705f896962ce26f5c1e1588ea57c71c98908cadb7bdcd0cd4d AS runtime
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
