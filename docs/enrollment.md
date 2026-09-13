# Device enrollment

1. Sign in to the Controller as Owner or Administrator.
2. Choose **Add device** and create a 15-minute code.
3. Verify the Controller HTTPS certificate thumbprint on the Controller host using a
   trusted administrative channel.
4. Run `Veltrix-Control-Setup.exe` on an owned/authorized node and select **Managed Node** or
   **Controller + Managed Node**.
5. Enter the HTTPS URL, verified thumbprint, code, and permitted file root.
6. The installer writes the code into an Administrator/SYSTEM-only ProgramData folder.
7. On first service start the Agent generates its ECDSA key, enrolls, protects the
   private identity with DPAPI, and deletes the now-consumed code file.

Codes are single-use, stored as digests, and rejected after expiry or revocation.
Reinstalling an already-enrolled node preserves its ProgramData identity by default.
To intentionally create a new identity, remove the device from the Controller (Phase 2
UI) and securely delete its local Agent data as an administrator before reenrolling.
