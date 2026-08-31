# Security Policy

## Reporting a Vulnerability

Please report security issues privately to the repository owner instead of opening a public issue. Include the affected version, reproduction steps, and the potential impact.

## Sensitive Data

FOAM Workbench can launch external tools and process user-selected CAD files and simulation directories. Do not commit API tokens, credentials, proprietary geometry, local solver cases, logs containing private paths, or unpublished simulation results.

Review installation scripts before running them because they may modify WSL package sources and install system packages.
