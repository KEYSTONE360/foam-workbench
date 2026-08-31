# Contributing

Thank you for your interest in FOAM Workbench.

## Development Setup

1. Install the .NET 10 SDK.
2. Install or configure an OpenFOAM runtime through WSL2 or Docker.
3. Install ParaView for Windows.
4. Run `dotnet build -c Release`.
5. Run `dotnet test` before submitting a change.

## Pull Requests

- Keep changes focused and explain the user-facing behavior.
- Preserve the existing External Aerodynamics workflow when modifying porous-media features.
- Do not replace OpenFOAM numerical algorithms with simplified local implementations.
- Add or update tests for case-generation and calculation changes.
- Do not commit generated cases, solver results, binaries, logs, tokens, or private data.
- Use English for commit messages, issues, pull requests, and repository documentation.

## Numerical Changes

When changing OpenFOAM dictionaries or solver selection, document the tested OpenFOAM distribution and version. Validate generated cases with `blockMesh`, `checkMesh`, and at least solver startup whenever the required runtime is available.
