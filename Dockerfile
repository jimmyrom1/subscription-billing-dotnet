# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source

# Copy solution and project files first for efficient caching
COPY SubscriptionBilling.slnx ./
COPY src/SubscriptionBilling.Api/*.csproj src/SubscriptionBilling.Api/
COPY tests/SubscriptionBilling.Tests/*.csproj tests/SubscriptionBilling.Tests/

RUN dotnet restore SubscriptionBilling.slnx

# Copy source code and build
COPY src/ src/
COPY tests/ tests/

RUN dotnet build --configuration Release --no-restore
RUN dotnet test --configuration Release --no-build --verbosity normal

# Publish API
RUN dotnet publish src/SubscriptionBilling.Api/SubscriptionBilling.Api.csproj \
    --configuration Release \
    --no-build \
    --output /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN groupadd -g 1000 appgroup && useradd -u 1000 -g appgroup -m appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

ENTRYPOINT ["dotnet", "SubscriptionBilling.Api.dll"]
