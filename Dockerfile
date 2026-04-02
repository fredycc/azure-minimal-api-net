# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore
COPY Directory.Build.props .
COPY src/Doctors.Domain/Doctors.Domain.csproj src/Doctors.Domain/
COPY src/Doctors.Application/Doctors.Application.csproj src/Doctors.Application/
COPY src/Doctors.Infrastructure/Doctors.Infrastructure.csproj src/Doctors.Infrastructure/
COPY src/Doctors.Api/Doctors.Api.csproj src/Doctors.Api/
RUN dotnet restore src/Doctors.Api/Doctors.Api.csproj

# Copy all source and publish
COPY src/ src/
RUN dotnet publish src/Doctors.Api/Doctors.Api.csproj -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Doctors.Api.dll"]
