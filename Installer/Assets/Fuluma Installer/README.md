# Fuluma Installer

Editor scripts of the `Fuluma-Installer.unitypackage` installer. When the `.unitypackage` is imported, `Installer` adds the OpenUPM
scoped registry (`https://package.openupm.com`) with the scopes `com.saesentsessis.fuluma`, `com.saesentsessis.unity-collections-specialized`, `com.saesentsessis.unity-meshgen`, `com.saesentsessis.unity-shaderworks` to the host project's
`Packages/manifest.json`, then adds `com.saesentsessis.fuluma` at `Installer.Version` (it never downgrades a newer version).
Packages from Unity's registry need no scope.

This project is not the UPM package (`Fuluma/Assets/root`). `SAESENTSESSIS_INSTALLER_PROJECT` (Player settings, scripting define
symbols) keeps the installer from editing this project's own manifest.

## CI export

The release workflow calls `Saesentsessis.Fuluma.Installer.PackageExporter.ExportPackage` through GameCI `unity-builder` to produce
`Fuluma-Installer.unitypackage`. `commands/bump-version.ps1` keeps `Installer.Version` in step with `package.json`.
