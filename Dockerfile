FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY app/KidControlGantt.App.csproj app/
RUN dotnet restore app/KidControlGantt.App.csproj

COPY app/ app/
RUN dotnet publish app/KidControlGantt.App.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish ./

EXPOSE 8080

ENTRYPOINT ["dotnet", "KidControlGantt.App.dll"]
