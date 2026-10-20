# Step 12 — Windows kernel filesystem driver

Status: `planned`

Objective: expose the validated core through a real WDK filesystem driver.

Implementation: kernel adapters, volume discovery, mount/dismount, IRP
dispatch, enumeration, cached reads, synchronization, metadata operations,
and eventually writes. Test only in a dedicated Windows 11 VM using Driver
Verifier and WinDbg. Never load the experimental driver automatically on the
developer host.

Exit evidence: standard .NET file APIs read validated fixture data through the
driver, and upstream tools independently validate the pool.

Commit: `driver: add isolated-VM filesystem driver milestone`

