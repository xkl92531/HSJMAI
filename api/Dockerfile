#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/core/aspnet:3.1-buster-slim AS base
WORKDIR /app/out
EXPOSE 80

FROM mcr.microsoft.com/dotnet/core/sdk:3.1-buster AS build
WORKDIR /src
COPY . /src
COPY hsjm.ai/nuget.config /src
RUN dotnet restore "hsjm.ai/hsjm.ai.csproj" --configfile "nuget.config"
COPY . /src
WORKDIR "/src"
RUN dotnet build "hsjm.ai/hsjm.ai.csproj" -c Release -o /app/out/build
 
 
FROM build AS publish
RUN dotnet publish "EIS.Service.Auth/EIS.Service.Auth.csproj" -c Release -o /app/out/publish

FROM base AS final
WORKDIR /app/out
COPY --from=publish /app/out/publish .
ENTRYPOINT ["dotnet", "hsjm.ai.dll"]
