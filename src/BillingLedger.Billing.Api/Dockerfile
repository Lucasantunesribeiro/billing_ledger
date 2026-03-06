# Build stage — restore only referenced projects for optimal layer caching
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY BillingLedger.sln .
COPY src/BillingLedger.SharedKernel/BillingLedger.SharedKernel.csproj           src/BillingLedger.SharedKernel/
COPY src/BillingLedger.BuildingBlocks/BillingLedger.BuildingBlocks.csproj       src/BillingLedger.BuildingBlocks/
COPY src/BillingLedger.Contracts/BillingLedger.Contracts.csproj                 src/BillingLedger.Contracts/
COPY src/BillingLedger.Billing.Api/BillingLedger.Billing.Api.csproj             src/BillingLedger.Billing.Api/

RUN dotnet restore src/BillingLedger.Billing.Api/BillingLedger.Billing.Api.csproj

COPY src/ src/

RUN dotnet publish src/BillingLedger.Billing.Api/BillingLedger.Billing.Api.csproj \
    -c Release --no-restore -o /app

# Runtime stage — minimal ASP.NET image, non-root user
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN addgroup --system --gid 1001 appgroup \
 && adduser  --system --uid 1001 --ingroup appgroup appuser

COPY --from=build /app .

USER appuser
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "BillingLedger.Billing.Api.dll"]
