KiwiAvatarSystem v44.55.29 local PowerShell 5.1 execution hotfixes

Source package:
D:\Users\main\Downloads\KiwiAvatarSystem_v44_55_29_CommandBufferGraphicsExecutionFormIsolation.zip
SHA256: D4C5B5001E3730D6E9C03B803209C50E7C20EBF96EB31226C53908A9F0B0E0D7

Vendor hotfix package:
D:\Users\main\Downloads\KiwiAvatarSystem_v44_55_29_1_PowerShell51BuildHotfix.zip
SHA256: D4EE1B7C37B202C79A09AADE2A13795A565B9894BE423BE022284A8BFB11A8CB

Local changes are limited to the execution harness:

1. Build-KiwiV44_55_29.ps1 waits for the Unity GUI-subsystem process with
   Start-Process -Wait -PassThru and consumes Process.ExitCode. The package
   script returned exit 1 under Set-StrictMode because $LASTEXITCODE was not
   defined after asynchronous Unity.exe launch.

2. Validate-Runtime-KiwiV44_55_29.ps1 changes the three Python-style `elif`
   tokens to PowerShell `elseif`. Claim gates and decision strings are
   otherwise identical to the vendor hotfix package.

No Production C#, observer C#, Native DLL, experiment environment, payload
classification, threshold, tracking, inference, or presentation behavior is
changed by these local scripts.
