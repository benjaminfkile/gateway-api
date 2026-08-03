# gateway-api

C#/.NET rebuild of `bk-gateway-api` (aka **bk-gateway-v2** in the spec). The gateway is
also the node agent for the EC2 instance it runs on: YARP edge proxy, opt-in SignalR
real-time hub, Docker reconciler driven by a Postgres desired-state manifest, and a
Cognito-protected (MFA) management API + ops dashboard.

**Read the tech spec first: [`docs/tech-spec.md`](docs/tech-spec.md).** The design
invariant in §1 (downstream applications must never be required to be gateway-aware)
is non-negotiable.

## Layout

- `src/Gateway.Api` — the gateway (ASP.NET Core, .NET 10, publishes self-contained `linux-arm64`)
- `tests/Gateway.Api.Tests` — xUnit test suite

## Build & run

```bash
dotnet build
dotnet run --project src/Gateway.Api   # serves /api/health
dotnet test
```

## Status

Phase 0 (scaffold) of the migration plan in `docs/tech-spec.md` §10.
# gateway-api
