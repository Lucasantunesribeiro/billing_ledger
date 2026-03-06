FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY BillingLedger.sln .
COPY src/BillingLedger.SharedKernel/BillingLedger.SharedKernel.csproj             src/BillingLedger.SharedKernel/
COPY src/BillingLedger.BuildingBlocks/BillingLedger.BuildingBlocks.csproj         src/BillingLedger.BuildingBlocks/
COPY src/BillingLedger.Contracts/BillingLedger.Contracts.csproj                   src/BillingLedger.Contracts/
COPY src/BillingLedger.Ledger.Worker/BillingLedger.Ledger.Worker.csproj           src/BillingLedger.Ledger.Worker/

RUN dotnet restore src/BillingLedger.Ledger.Worker/BillingLedger.Ledger.Worker.csproj

COPY src/ src/

RUN dotnet publish src/BillingLedger.Ledger.Worker/BillingLedger.Ledger.Worker.csproj \
    -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN addgroup --system --gid 1001 appgroup \
 && adduser  --system --uid 1001 --ingroup appgroup appuser

COPY --from=build /app .

USER appuser

ENTRYPOINT ["dotnet", "BillingLedger.Ledger.Worker.dll"]
