# Session Perf Tracker - Domain Layer

## Responsibility
The **Core** of the application. Contains business rules, entities, and interfaces that define WHAT the system does, without knowing HOW it's implemented.

## Key Components
- **Models/**: POCO classes representing sessions, metrics, and settings.
- **Abstractions/**: Interfaces for collectors, storage, and services.
- **Services/**: Pure logic like SessionComparisonEngine and SessionSummaryService.

## Constraints for Agents
- **NO System.Diagnostics.Process**: Do not use process-related classes here. Use abstractions.
- **NO External Dependencies**: Only standard .NET libraries or pure logic packages.
- **Consistency**: Keep models immutable where possible or use ObservableObject if they are bound to the UI.
