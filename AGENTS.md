# SEEIN public repository

This is the public Windows x64 project. Keep all optional features out of Core.
Never add personal runtime data or signing keys. Version is 2.2.0.
Build and test with scripts/build.ps1. Public profiles are Core and PublicFull.
Use fixed upstream versions in vendor/tools/sources.lock.json and preserve upstream notices.
Hardware acceptance is opt-in; unit tests must not write hardware or change the installed application.
