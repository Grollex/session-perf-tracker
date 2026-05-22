# Session Perf Tracker - Infrastructure Layer

## Responsibility
The **Implementation** layer. This is where the code talks to Windows, the File System, and Databases.

## Key Components
- **Collectors/**: Logic for gathering metrics via WMI, Performance Counters, or System.Diagnostics.
- **Storage/**: Implementation of ISessionStore (JSON/SQLite).
- **GlobalWatch/**: Logic for scanning all system processes and performing actions (Kill, Suspend).
- **Targeting/**: Resolving process trees and identifying parent-child relationships.
- **Updates/**: Checking for new versions via HTTP.

## AI Instructions
- **Error Handling**: Windows APIs can be flaky. Always use try-catch blocks and provide fallback values.
- **Performance**: Collectors run frequently. Ensure code is non-blocking and memory-efficient.
- **Service Registration**: Ensure new services are implemented according to the interfaces defined in the Domain.
