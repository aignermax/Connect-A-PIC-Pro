.PHONY: run build test clean restore icon installer installer-selfcontained

# Default target - run the desktop app
run:
	dotnet run --project CAP.Desktop/CAP.Desktop.csproj

# Build all projects
build:
	dotnet build

# Run tests
test:
	dotnet test UnitTests/UnitTests.csproj

# Clean build artifacts
clean:
	dotnet clean

# Restore dependencies
restore:
	dotnet restore

# Build and run
start: build run

# ─────────────────────────────────────────────────────────────────
# Installer targets (Windows only – requires WiX v4 and Python)
# ─────────────────────────────────────────────────────────────────

# Generate the Lunima application icon (Installer/LunimaIcon.ico)
icon:
	python3 scripts/generate_icon.py --output Installer/LunimaIcon.ico

# Build the MSI installer (framework-dependent; .NET 8 required on target)
installer:
ifeq ($(OS),Windows_NT)
	powershell -ExecutionPolicy Bypass -File scripts/build_installer.ps1
else
	@echo "installer target is Windows-only (requires WiX + PowerShell)"
	@exit 1
endif

# Build a self-contained MSI (bundles .NET 8 runtime; ~150 MB)
installer-selfcontained:
ifeq ($(OS),Windows_NT)
	powershell -ExecutionPolicy Bypass -File scripts/build_installer.ps1 -SelfContained
else
	@echo "installer-selfcontained target is Windows-only (requires WiX + PowerShell)"
	@exit 1
endif

# TODO: Add macos-bundle target that calls scripts/build_macos_bundle.sh once macOS build infrastructure is ready
