# 请参阅 https://aka.ms/customizecontainer 以了解如何自定义调试容器，以及 Visual Studio 如何使用此 Dockerfile 生成映像以更快地进行调试。

# 此阶段用于在快速模式(默认为调试配置)下从 VS 运行时
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080


# 此阶段用于生成服务项目
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["HSJM.AI/nuget.config", "HSJM.AI/"]
COPY ["hsjm.ai/HSJM.AI.csproj", "hsjm.ai/"]
COPY ["hsjm.ai.AppLayer/HSJM.AI.AppLayer.csproj", "hsjm.ai.AppLayer/"]
COPY ["hsjm.ai.Core/HSJM.AI.Core.csproj", "hsjm.ai.Core/"]
COPY ["hsjm.ai.IDbService/HSJM.AI.IDbService.csproj", "hsjm.ai.IDbService/"]
COPY ["hsjm.ai.Utility/HSJM.AI.Utility.csproj", "hsjm.ai.Utility/"]
COPY ["hsjm.ai.DbService/HSJM.AI.DbService.csproj", "hsjm.ai.DbService/"]
COPY ["hsjm.ai.MediatR/HSJM.AI.MediatR.csproj", "hsjm.ai.MediatR/"]
RUN dotnet restore "./hsjm.ai/HSJM.AI.csproj"
COPY . .
WORKDIR "/src/hsjm.ai"
RUN dotnet build "./HSJM.AI.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 此阶段用于发布要复制到最终阶段的服务项目
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./HSJM.AI.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 此阶段在生产中使用，或在常规模式下从 VS 运行时使用(在不使用调试配置时为默认值)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HSJM.AI.dll"]