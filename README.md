# Ridder.Aspire.Dokploy

`Ridder.Aspire.Hosting.Dokploy` is a .NET Aspire hosting integration for provisioning and deploying Aspire resources to Dokploy.

## What is in this repository

- `src/Ridder.Aspire.Hosting.Dokploy` contains the Dokploy hosting integration extracted from ScarletPigsServices and prepared for standalone packaging.
- `samples/SimpleWebApp` shows a project deployment with external endpoints and custom Dokploy domains.
- `samples/ContainerVolume` shows a container deployment with a self-hosted registry and persistent volume.
- `samples/RunOnceJob` shows a one-shot project deployment for background jobs and migration-style workloads.
- `samples/PostgresApp` shows a project plus PostgreSQL deployment with volume-backed storage.

## Packaging

The library is configured to produce a NuGet package and symbol package with repository metadata, package readme, license metadata, and XML documentation.

For self-hosted Dokploy registries, the integration derives a public `sslip.io` hostname automatically from the Dokploy server instead of prompting for a registry domain. Saw how this was handled in the [sorvia-jomaxso/aspire](https://github.com/jomaxso/sorvia-aspire) repo. Very nice way to handle it.

## Build

```bash
dotnet build ./Ridder.Aspire.Dokploy/Ridder.Aspire.Dokploy/Ridder.Aspire.Dokploy.slnx
```

## Pack

```bash
dotnet pack ./Ridder.Aspire.Dokploy/Ridder.Aspire.Dokploy/src/Ridder.Aspire.Hosting.Dokploy/Ridder.Aspire.Hosting.Dokploy.csproj -c Release
```
