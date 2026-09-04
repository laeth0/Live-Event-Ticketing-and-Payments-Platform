# ASP.NET Core Engineering Standards

These rules apply to ASP.NET Core work in this repository.

## Required ASP.NET Core Stack

Every project uses the following libraries and patterns unless the request explicitly
requires otherwise:

- Entity Framework Core for persistence.
- Mapster for object mapping.
- FluentValidation for request and command validation.
- CQRS with MediatR for application commands, queries, notifications, and pipeline
  behaviors.
- Scrutor for convention-based dependency registration.
- Scalar for interactive OpenAPI documentation.

> **Version policy:** Always use the latest stable ASP.NET Core and .NET release and the
> latest stable, mutually compatible versions of all required libraries. Do not use
> preview or release-candidate versions unless the request explicitly requires them.

Inspect the target framework and pinned package versions before changing code. Use APIs
compatible with those versions, preserve the solution's established conventions, and do
not replace a required library with an alternative mapping, validation, mediator,
registration, persistence, or API-documentation library.

## ASP.NET Core Application Structure

- Always follow established ASP.NET Core best practices and clean-code principles. Write
  production-ready code that is maintainable, readable, well-structured, and consistent
  with the project's .NET 10 ASP.NET Core backend.
- Do not add comments to the code.
- Use the modern `WebApplication.CreateBuilder(args)` and `WebApplication` hosting model
  unless the project intentionally targets an older ASP.NET Core version.
- Keep `Program.cs` focused on service registration and HTTP pipeline composition.
  Move cohesive registrations into clearly named `IServiceCollection` extension methods
  when `Program.cs` would otherwise become difficult to scan.
- Preserve the solution's existing project boundaries and feature organization. Keep
  ASP.NET Core transport types in the API project and MediatR requests and handlers in
  the established application project or feature slice.
- Prefer Clean Architecture and preserve clear separation of concerns and dependency
  direction between the project's established layers.
- Use design patterns only when they solve a concrete architectural or maintainability
  problem. Apply Strategy, Decorator, Factory, Singleton, or another pattern only when
  it fits the specific problem, existing architecture, dependency lifetimes, and project
  conventions. Prefer the simplest maintainable design over unnecessary abstraction or
  over-engineering.
- Keep controllers limited to ASP.NET Core concerns: model binding, authorization
  metadata, status codes, headers, and dispatching the relevant MediatR request.
- Use explicit request and response contracts at HTTP boundaries. Do not expose EF Core
  entities, `IdentityUser`, MediatR request types, or internal exception types as API
  responses.
- Use ASP.NET Core options binding and validation for configuration. Do not read
  configuration values ad hoc throughout handlers or create static configuration
  accessors.
- Avoid magic numbers and strings. Extract meaningful reusable values into clearly
  named C# constants, enums, or typed configuration. Keep environment-specific values,
  credentials, secrets, URLs, and ports out of source code and load them through the
  project's ASP.NET Core configuration patterns.

## Dependency Injection and Scrutor

- Use ASP.NET Core's built-in dependency-injection container. Constructor-inject
  dependencies and do not resolve application services through `IServiceProvider` in
  controllers or handlers.
- Use `ITransientService`, `IScopedService`, and `ISingletonService` marker interfaces to
  declare conventionally registered service lifetimes.
- Register marker-interface services with Scrutor using this scan in the appropriate
  `IServiceCollection` extension method:

```csharp
services.Scan(scan => scan
    .FromAssemblies(AssemblyReference.Assembly)
    .AddClasses(classes => classes.AssignableTo<ITransientService>())
        .AsImplementedInterfaces()
        .WithTransientLifetime()
    .AddClasses(classes => classes.AssignableTo<IScopedService>())
        .AsImplementedInterfaces()
        .WithScopedLifetime()
    .AddClasses(classes => classes.AssignableTo<ISingletonService>())
        .AsImplementedInterfaces()
        .WithSingletonLifetime());
```

- Keep `AssemblyReference.Assembly` in the assembly that owns the implementations being
  scanned. Add another explicit assembly only when its services must participate in the
  same scan.
- Do not manually register a service already covered by the Scrutor conventions unless
  an intentional override, decorator, keyed registration, factory, or instance-specific
  setup is required.
- Choose marker interfaces deliberately: EF Core-dependent and request-specific services
  are scoped; lightweight stateless services may be transient; singleton services must
  be thread-safe and must not capture scoped dependencies.

## CQRS and MediatR

- Represent state changes as MediatR commands and data retrieval as MediatR queries.
  Name each request and handler for the use case they implement.
- Give each `IRequestHandler<TRequest, TResponse>` one use-case responsibility. Do not
  place unrelated application workflows in a controller, endpoint delegate, or a large
  catch-all handler.
- Send commands and queries through `ISender`. Inject `IPublisher` only where MediatR
  notifications are intentionally published.
- Keep command and query contracts explicit and strongly typed. Prefer the Result
  Pattern for expected success and failure outcomes.
- Implement cross-cutting MediatR behavior, such as FluentValidation, logging, or
  transactions, with ordered `IPipelineBehavior<TRequest, TResponse>` implementations.
  Do not duplicate the same behavior inside every handler.
- Use notifications only for in-process fan-out where multiple handlers are intended.
  Do not treat an in-process MediatR notification as a durable integration event.
- Pass `CancellationToken` from the ASP.NET Core endpoint through `ISender.Send`, the
  handler, and EF Core async operations.

## FluentValidation

- Create `AbstractValidator<T>` validators for HTTP request models and MediatR commands
  or queries that accept user-controlled input.
- Register validators from the owning assembly with FluentValidation's assembly-scanning
  registration APIs supported by the pinned package version.
- Execute MediatR request validators in a validation pipeline behavior before the
  handler runs. Keep ASP.NET Core model-binding failures and FluentValidation failures
  mapped to the application's established `ProblemDetails` response shape.
- Use FluentValidation for input-shape and use-case validation. Keep EF Core uniqueness
  constraints, concurrency checks, and domain invariants authoritative at their proper
  boundary rather than relying solely on a validator.
- Use asynchronous validation rules only when I/O is genuinely required, and call the
  asynchronous validation path with the request cancellation token.
- Do not duplicate identical rules in controllers, endpoint filters, MediatR handlers,
  and validators. Reuse a validator or a shared rule component when the rule is truly
  identical.

## Mapster

- Use Mapster for mappings between HTTP contracts, MediatR models, domain models, and
  response DTOs. Do not add hand-written field-by-field mapping or another mapping
  library when Mapster already covers the mapping.
- Keep non-trivial mappings in explicit `TypeAdapterConfig` registrations discovered
  from the appropriate assembly. Make renames, ignored members, constructors, and
  custom conversions visible in configuration.
- Prefer Mapster projection for EF Core read queries when the mapping can be translated
  to SQL. Inspect the generated SQL when translation, selected columns, or query shape
  is uncertain.
- Do not hide business decisions, authorization checks, database access, or side effects
  inside Mapster mapping configuration.
- Treat compile-time or startup mapping validation failures as implementation errors;
  do not suppress unmapped members without confirming they are intentionally ignored.

## Entity Framework Core

- Register each `DbContext` through ASP.NET Core dependency injection with the provider
  and lifetime already established by the project. Never keep a `DbContext` in a
  singleton or use one context concurrently.
- Prefer UTC for all persisted timestamps unless a business requirement explicitly
  requires another representation. Do not mix UTC and local time in persistence or
  business logic. Avoid calling `DateTime.UtcNow` or similar static time APIs directly
  in business logic; inject `TimeProvider` so time-dependent behavior can be tested
  reliably.
- Use EF Core asynchronous APIs and propagate `CancellationToken`. Do not block EF Core
  tasks with `.Result`, `.Wait()`, or other sync-over-async calls.
- Project query results to the required response shape, preferably through a
  SQL-translatable Mapster projection. Use `AsNoTracking()` for read-only queries unless
  identity resolution or tracked updates are intentionally required.
- Avoid `Include` chains that load more data than the use case needs, per-row query
  loops, client-side evaluation, and materializing a query before filtering,
  projection, ordering, or pagination.
- Use deterministic ordering before `Skip`/`Take` pagination. Keep maximum page sizes
  explicit in the ASP.NET Core contract.
- Configure entity relationships, delete behavior, conversions, indexes, precision,
  concurrency tokens, and constraints explicitly when EF Core conventions do not match
  the model.
- Call `SaveChangesAsync` at an intentional use-case boundary. Use an explicit
  transaction only when multiple writes must be atomic beyond the guarantees of one
  `SaveChangesAsync` call.
- Convert expected `DbUpdateConcurrencyException` and constraint failures into the
  established application result and ASP.NET Core `ProblemDetails` response. Do not
  expose provider messages or SQL details.

### EF Core Migrations

> **CRITICAL INSTRUCTION:** Whenever a database model or EF Core relationship changes,
> create a new migration for that change. Never modify, rename, or delete an existing
> migration; always represent later schema changes with an additional migration.

- **CRITICAL RULE**: Every time you add, modify, or remove database models, you MUST
  also update `projectSchema.dbml` to reflect the current schema.
- Every model or relationship change must include both the corresponding
  `projectSchema.dbml` update and a newly generated EF Core migration in the same task.
- Keep schema migrations, development seed data, test data, and production reference
  data as separate responsibilities. Do not combine them in the same initialization
  workflow or service without a clear architectural reason.

> **Migration immutability:** Treat every existing migration and its generated designer
> file as immutable. Do not modify a previous migration to include a later change. Update
> the models and `projectSchema.dbml`, then generate a new migration containing only the
> new schema change.

- Review the generated migration and model snapshot. Remove unrelated operations and
  verify column types, nullability, defaults, foreign keys, indexes, and delete behavior.
- Preserve existing data during renames, type changes, and required-column additions.
  Use a staged migration when one deployment cannot safely perform the change.
- Apply pending EF Core migrations automatically during application startup through an
  `IHostedService`, keeping the migration logic outside `Program.cs`.
- Run migrations asynchronously with the startup `CancellationToken`. Let failures
  propagate so application startup fails.

## ASP.NET Core HTTP Pipeline

- Keep middleware order intentional. Place exception handling, forwarded headers,
  HTTPS, CORS, authentication, authorization, rate limiting, and endpoint mapping in the
  order required by the pinned ASP.NET Core version and the application's hosting model.
- Use ASP.NET Core `ProblemDetails` for consistent error responses. Let unexpected
  exceptions reach the centralized exception handler and keep stack traces out of HTTP
  responses.
- Handle errors intentionally and consistently with the existing architecture. Let the
  global exception middleware handle unexpected failures, never silently swallow
  exceptions or use empty `catch` blocks, and preserve useful context without exposing
  sensitive implementation details.
- Use ASP.NET Core authentication schemes and policy-based authorization. Apply
  authorization metadata to every protected controller action or endpoint and enforce
  resource ownership in the corresponding use case or EF Core query.
- Use `[ApiController]` conventions for controller APIs.
- Use `IHttpClientFactory` for outbound HTTP clients and ASP.NET Core hosted services for
  long-running background work. A hosted service must create a scope before resolving a
  scoped `DbContext` or MediatR handler dependency.

## OpenAPI and Scalar

- Generate the OpenAPI document with the ASP.NET Core OpenAPI integration used by the
  pinned target framework and expose its interactive UI with Scalar.
- Register Scalar with the package-version-compatible `Scalar.AspNetCore` APIs. Keep the
  OpenAPI document route and Scalar endpoint configuration consistent.
- Add endpoint names, summaries, descriptions, response metadata, authorization
  requirements, and documented status codes so Scalar accurately describes the API.
- Configure authentication support in Scalar without embedding tokens, credentials, or
  environment-specific secrets in source code.
- Expose Scalar according to the project's environment policy. Do not unintentionally
  publish internal or privileged API documentation in production.

## Testability and Testing Files

- Do not create new ASP.NET Core unit, integration, functional, or end-to-end test files
  or new .NET test projects unless the user explicitly requests tests to be added.
- Design all new and modified code for future testability even when automated tests are
  not part of the current task.
- Keep business logic separate from infrastructure, HTTP or UI presentation, persistence,
  and external services. Avoid unnecessary coupling and side effects.
- Use dependency injection or clear abstractions where appropriate so infrastructure and
  external dependencies can be replaced without changing the core implementation.
- Keep classes, methods, handlers, and modules focused on one responsibility. Make
  important logic callable and verifiable independently without requiring the ASP.NET
  Core host, a database, the network, or other unrelated infrastructure.
- Do not choose designs that would require major refactoring merely to add tests later.
- Continue to run and maintain any existing tests affected by a change. Do not delete,
  disable, skip, or weaken existing tests merely to satisfy this rule or make the
  solution build pass.

## ASP.NET Core Verification

- Build the affected solution with the repository-pinned .NET SDK and run its existing
  formatting, analyzer, and test commands.
- Verify ASP.NET Core service registration at startup, including Scrutor-scanned
  services, MediatR handlers and pipeline ordering, FluentValidation validators,
  Mapster configuration, EF Core provider setup, OpenAPI generation, and Scalar mapping.
- When HTTP behavior changes, exercise the affected endpoint and confirm binding,
  validation, authorization, status codes, `ProblemDetails`, and cancellation behavior.
- When EF Core behavior changes, inspect generated SQL where relevant and confirm that
  the migration, model snapshot, and database update contain only the intended changes.
