# WebMail

WebMail MVP skeleton — an ASP.NET Core Razor Pages application for managing buyer email
authorization and supplier mail synchronization. This is an early-stage skeleton: the core
domain model, services, and page structure are in place, but several integrations are stubbed
and database initialization is deferred (see below).

## Build / Test / Run

```bash
dotnet build WebMail.sln          # build the solution
dotnet test WebMail.sln           # run the test suite
dotnet run --project src/WebMail  # run the web app
```

## Known limitations / deferred

- **Database is NOT auto-created.** No migrations are applied and `EnsureCreated` is not called.
  As a result, the buyer/admin/sales/supplier pages and the background mail sync tick will fail
  against a fresh database until migrations (or `EnsureCreated`) are added. The home page works
  without a database.
- **Google OAuth + Gmail fetch are stubbed.** The OAuth token exchange and Gmail message fetch
  throw `NotImplementedException`.
- **Login UI is not implemented.** `/Login` and `/AccessDenied` do not exist yet, so the
  authenticated backend pages are not reachable while unauthenticated.
