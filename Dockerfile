FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY NovaWallet/NovaWallet.sln .
COPY NovaWallet/NovaWallet.Domain/NovaWallet.Domain.csproj NovaWallet.Domain/
COPY NovaWallet/NovaWallet.Application/NovaWallet.Application.csproj NovaWallet.Application/
COPY NovaWallet/NovaWallet.Infrastructure/NovaWallet.Infrastructure.csproj NovaWallet.Infrastructure/
COPY NovaWallet/NovaWallet.Presentation/NovaWallet.Presentation.csproj NovaWallet.Presentation/
COPY NovaWallet/NovaWallet.Tests/NovaWallet.Tests.csproj NovaWallet.Tests/

RUN dotnet restore NovaWallet.sln

COPY NovaWallet/ .

RUN dotnet publish NovaWallet.Presentation/NovaWallet.Presentation.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "NovaWallet.Presentation.dll"]
