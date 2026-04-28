#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

#Depending on the operating system of the host machines(s) that will build or run the containers, the image specified in the FROM statement may need to be changed.
#For more information, please see https://aka.ms/containercompat
ARG BASE_IMAGE  
ARG BUILD_IMAGE

FROM ${BASE_IMAGE} AS base
USER ContainerAdministrator
WORKDIR /app

FROM ${BUILD_IMAGE} AS build
USER ContainerAdministrator
WORKDIR /src
COPY ["windows-hosts-writer.csproj", "."]
RUN dotnet restore "./windows-hosts-writer.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "windows-hosts-writer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "windows-hosts-writer.csproj" -c Release -o /app/publish

FROM $BASE_IMAGE AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER ContainerAdministrator
ENTRYPOINT ["dotnet", "windows-hosts-writer.dll"]