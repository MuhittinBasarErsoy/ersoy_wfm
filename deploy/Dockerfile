# API ve Web için ortak çok aşamalı imaj. Hangi projenin yayınlanacağı PROJECT argümanıyla seçilir:
#   docker build -f deploy/Dockerfile --build-arg PROJECT=Wfm.Api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll \
    ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /out .
# Kalıcı volume'lar ilk bağlandığında bu sahipliği devralır (container root olmadan çalışır).
RUN mkdir -p /app/uploads /app/keys && chown -R $APP_UID /app/uploads /app/keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_DLL"]
