FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/PaymentGateway.Emulator/*.csproj src/PaymentGateway.Emulator/
RUN dotnet restore src/PaymentGateway.Emulator/PaymentGateway.Emulator.csproj

COPY src/PaymentGateway.Emulator/ src/PaymentGateway.Emulator/
RUN dotnet publish src/PaymentGateway.Emulator/PaymentGateway.Emulator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080

RUN useradd --uid 10002 --create-home gateway
USER gateway

COPY --from=build --chown=gateway:gateway /app ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "PaymentGateway.Emulator.dll"]
