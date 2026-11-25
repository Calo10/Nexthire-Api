# Vendored Nexa.WorkflowAutomation 0.1.1864

These assemblies are checked in so Azure Pipelines can restore/build without
access to the private `CreativeOneSolutions/Nexa` Azure Artifacts feed.

To upgrade:
1. Restore the new version from the Nexa feed locally.
2. Copy `lib/net8.0/*.dll` (+ `.xml`) from the NuGet cache into this folder.
3. Optionally refresh `../../packages/*.nupkg`.
