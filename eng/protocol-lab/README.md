# Controlled protocol lab

This test-only lab creates five disposable loopback servers for the real LFTP
acceptance matrix: FTP, opportunistic FTP TLS, explicit FTPS, implicit FTPS,
and SFTP. The SFTP endpoint supplies unencrypted and passphrase-encrypted
client keys and can rotate its host key in place so enrollment, unchanged
trust, and replacement are tested against one endpoint. The lab never installs
or enables a Windows service and never listens on a non-loopback interface.

`build/Test-ProtocolMatrix.ps1` creates an ignored Python virtual environment,
installs the exact versions in `requirements.txt`, launches the lab, and runs
the opt-in .NET integration tests against the selected LFTP runtime. Generated
roots, credentials, certificates, and SSH keys remain under `artifacts/` and
are removed after the run unless `-KeepLab` is supplied for diagnosis.

```powershell
.\build\Test-ProtocolMatrix.ps1
```

The script copies the selected packaged runtime into its ignored run directory.
The integration fixture sets that run's `ssl:ca-file` through a test-only
process wrapper because the relocatable runtime does not discover an appended
default bundle. Certificate verification remains enabled without changing the
user's Windows trust stores, production commands, or the acquired runtime.

The lab is not production code. Paramiko and pyftpdlib are development-only
server fixtures and are not included in LFTP Pilot packages.
