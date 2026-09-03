# Ridder.Aspire.Dokploy

`Ridder.Hosting.Dokploy` is a .NET Aspire hosting integration for provisioning and deploying Aspire resources to Dokploy.

## What is in this repository

- `src/Ridder.Hosting.Dokploy` contains the Dokploy hosting integration extracted from ScarletPigsServices and prepared for standalone packaging.
- `samples/SimpleWebApp` shows a project deployment with external endpoints and custom Dokploy domains.
- `samples/ContainerVolume` shows a container deployment with a self-hosted registry and persistent volume.
- `samples/RunOnceJob` shows a one-shot project deployment for background jobs and migration-style workloads.
- `samples/PostgresApp` shows a project plus PostgreSQL deployment with volume-backed storage.

## Packaging

The library is configured to produce a NuGet package and symbol package with repository metadata, package readme, license metadata, and XML documentation.

## Build

```bash
dotnet build /home/runner/work/Ridder.Aspire.Dokploy/Ridder.Aspire.Dokploy/Ridder.Aspire.Dokploy.sln
```

## Pack

```bash
dotnet pack /home/runner/work/Ridder.Aspire.Dokploy/Ridder.Aspire.Dokploy/src/Ridder.Hosting.Dokploy/Ridder.Hosting.Dokploy.csproj -c Release
```
