FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/QualityLab.Api/QualityLab.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG GIT_SHA=local
ENV GIT_SHA=$GIT_SHA
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "QualityLab.Api.dll"]