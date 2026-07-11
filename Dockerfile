FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy projects and restore dependencies
COPY Server/Server.csproj Server/
COPY Shared/Shared.csproj Shared/
RUN dotnet restore Server/Server.csproj

# Copy the remaining source code
COPY Server/ Server/
COPY Shared/ Shared/

# Build and publish
RUN dotnet publish Server/Server.csproj -c Release -o /out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out .

# Expose required ports
EXPOSE 50005/udp
EXPOSE 50006
EXPOSE 50007
EXPOSE 5000

ENTRYPOINT ["dotnet", "Server.dll"]
