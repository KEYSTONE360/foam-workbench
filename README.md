# FOAM Workbench

FOAM Workbench is a Windows desktop application that controls native OpenFOAM workflows through a graphical interface and opens completed cases in ParaView.

It does **not** reimplement CFD equations, finite-volume discretization, linear solvers, or meshing algorithms. It runs the selected OpenFOAM installation directly, including `blockMesh`, `snappyHexMesh`, `checkMesh`, `foamRun`, OpenMPI, `reconstructPar`, and `postProcess`. The generated OpenFOAM dictionaries and the installed OpenFOAM version remain the source of truth for every simulation.

## Highlights

- Windows WPF interface with high-contrast, minimal, neomorphic styling
- External aerodynamics and porous-media workflows in one application
- STEP, STP, IGES, IGS, BREP, and STL import
- Interactive CAD preview with axes and flow-direction guidance
- Gmsh/OpenCASCADE surface triangulation
- `blockMesh` and `snappyHexMesh` case generation
- Laminar and RANS k-omega SST external-flow cases
- Force, moment, drag, lift, wall-shear, y+, vorticity, and Q-criterion controls
- WSL2 and Docker execution backends
- Serial and OpenMPI parallel execution
- Live residual extraction, logarithmic plotting, mouse-wheel zoom, and log recovery
- Python/matplotlib residual exports to PNG, SVG, and CSV
- User-selectable case and result directories
- `.foam` marker generation and native Windows ParaView launch
- Raw OpenFOAM dictionary editor and runtime capability discovery

## Porous-Media CFD

The porous-media workspace generates structured multilayer filter cases without requiring a STEP or STL model.

Supported features include:

- Independent `cellZone` generation for every filter layer
- Isotropic and anisotropic intrinsic permeability
- Automatic permeability-to-Darcy-resistance conversion (`1/k`)
- Darcy-Forchheimer coefficients
- Water preset with density and dynamic viscosity
- Laminar steady SIMPLE and transient PIMPLE workflows
- Rainfall Flux, Gravity Drainage, and Water Head boundary-condition modes
- Gravity body-force treatment
- Analytical series-Darcy calculation
- Equivalent permeability and hydraulic conductivity
- Layer resistance fractions and automatic bottleneck detection
- Layer pressure drop, average velocity, nominal residence time, and flow balance
- Centerline CSV output and ParaView visualization fields
- Serial parameter sweeps and HTML/CSV/JSON reports
- Kozeny-Carman estimation with applicability warnings

The bundled TreeShield preset represents a seven-layer filter. Material properties should be replaced with measured or properly referenced values before results are used for engineering decisions.

## Requirements

- Windows 10 or Windows 11
- .NET 10 SDK or a compatible published build
- WSL2 with Ubuntu 24.04, or Docker
- OpenFOAM Foundation v14 or a compatible distribution
- Gmsh with OpenCASCADE support
- ParaView for Windows

Default runtime locations:

```text
WSL distribution: Ubuntu-24.04
OpenFOAM bashrc:   /opt/openfoam14/etc/bashrc
ParaView:          C:\Program Files\ParaView 6.1.0\bin\paraview.exe
```

## Build

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project .\FoamWorkbench.csproj
```

## OpenFOAM Setup

To inspect the environment without changing it:

```powershell
.\scripts\Verify-Environment.ps1
```

To install OpenFOAM Foundation v14 in the configured WSL distribution:

```powershell
.\scripts\Install-OpenFOAM14.ps1
```

The installation script requires explicit confirmation before it changes the WSL package configuration.

## Typical External-Aerodynamics Workflow

1. Select **External Aerodynamics**.
2. Import a CAD or STL model.
3. Inspect the interactive 3D preview and confirm units and flow direction.
4. Configure the domain, mesh, fluid, turbulence, and result functions.
5. Select a case directory.
6. Generate and validate the mesh.
7. Run the solver and monitor residuals.
8. Open the latest result in ParaView.

## Typical Porous-Media Workflow

1. Select **Porous Media**.
2. Load the seven-layer preset or create a custom stack.
3. Enter validated layer thickness, permeability, and porosity values.
4. Select Rainfall Flux, Gravity Drainage, or Water Head.
5. Generate the structured mesh and verify all `cellZone` regions.
6. Review analytical Darcy results and validation warnings.
7. Run OpenFOAM and monitor the actual residual fields.
8. Inspect pressure, velocity, layer ID, and permeability in ParaView.

## Validation

The source tree includes automated tests for unit conversion, permeability/resistance round trips, Darcy calculations, boundary-condition generation, result parsing, and porous-layer logic.

```powershell
dotnet test
```

The application also validates generated OpenFOAM cases with the installed runtime. Numerical results depend on geometry, mesh quality, boundary conditions, material data, solver settings, and OpenFOAM version.

## Repository Policy

Generated binaries, local validation cases, solver time directories, logs, and user simulation results are intentionally excluded from version control. This keeps the repository source-focused and prevents accidental publication of large or private case data.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).

OpenFOAM, ParaView, Gmsh, OpenCASCADE, and other third-party components retain their respective licenses and trademarks.
