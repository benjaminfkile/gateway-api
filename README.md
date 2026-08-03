# gateway-api

A self-hosted application gateway that is also the **node agent** for the VM it
runs on: YARP edge proxy, opt-in SignalR real-time hub, Docker reconciler
driven by a Postgres desired-state manifest, and a Cognito-protected (MFA)
management API + ops dashboard. Deploy downstream services in seconds with
blue-green cutover — no instance replacement — whether you run one box or a
fleet of twenty behind a load balancer.

**Read the tech spec first: [`docs/tech-spec.md`](docs/tech-spec.md).** The
design invariant in §1 (downstream applications must never be required to be
gateway-aware) is non-negotiable.

## Layout

- `src/Gateway.Api` — the gateway (ASP.NET Core, .NET 10, publishes self-contained single-file)
- `tests/Gateway.Api.Tests` — xUnit test suite

## Build & run

```bash
dotnet build
dotnet run --project src/Gateway.Api   # serves /api/health
dotnet test
```

## Status

Phase 0 (scaffold) of the build plan in `docs/tech-spec.md` §10.
