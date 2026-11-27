FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src ./src
RUN dotnet publish src/TsdServer.Host/TsdServer.Host.csproj -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN useradd --uid 10001 --create-home portal
WORKDIR /app
COPY --from=build /out .
USER portal
ENV ASPNETCORE_URLS=http://0.0.0.0:5080
ENV TZ=Europe/Moscow
EXPOSE 5080
ENTRYPOINT ["dotnet", "TsdServer.Host.dll"]
