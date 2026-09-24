FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Сначала только файлы проектов: восстановление пакетов попадёт в слой,
# который не пересобирается при каждой правке кода.
COPY Directory.Build.props ./
COPY src/TuitionBilling.Domain/*.csproj src/TuitionBilling.Domain/
COPY src/TuitionBilling.Application/*.csproj src/TuitionBilling.Application/
COPY src/TuitionBilling.Infrastructure/*.csproj src/TuitionBilling.Infrastructure/
COPY src/TuitionBilling.Api/*.csproj src/TuitionBilling.Api/
RUN dotnet restore src/TuitionBilling.Api/TuitionBilling.Api.csproj

COPY src/ src/
RUN dotnet publish src/TuitionBilling.Api/TuitionBilling.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Приложение форматирует суммы по ru-RU, поэтому инвариантный режим выключен
# и образу нужна ICU. В обычном aspnet:8.0 она есть, в alpine пришлось бы ставить.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080

# Не от root: контейнеру незачем иметь больше прав, чем нужно приложению.
RUN useradd --uid 10001 --create-home billing
USER billing

COPY --from=build --chown=billing:billing /app ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "TuitionBilling.Api.dll"]
