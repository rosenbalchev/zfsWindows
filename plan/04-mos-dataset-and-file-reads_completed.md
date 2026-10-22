# Step 04 — MOS, datasets, directories, and file reads

Status: `complete`

Objective: read actual filesystem content through ZFS metadata and block
pointers.

Implementation:

- read and validate the MOS root;
- implement block-pointer checksum and compression dispatch;
- traverse the MOS meta-dnode's indirect block and decode ZFS LZ4 blocks;
- traverse DSL datasets and ZAP directory objects;
- enumerate directories and resolve UTF-8 names;
- read regular files with overflow and sparse-file handling;
- expose pool, dataset, directory, and file handles through the C ABI.

Exit evidence: a genuine upstream fixture can be opened, enumerated, and read
with exact lengths and SHA-256 hashes from C#.

Result:

- validates label and uberblock SHA-256 checksums plus Fletcher-4 on every
  referenced non-embedded metadata/data block;
- traverses the MOS, DSL directory/dataset records, objset meta-dnodes,
  master node, SA layout tables, micro-ZAP directories, and indirect file
  block trees;
- supports embedded data, ZLE, OpenZFS LZ4 framing, multi-block files, and
  sparse-hole zero expansion;
- resolves slash-separated paths, enumerates bounded UTF-8 directory entries,
  and exposes opaque device, dataset, directory, and file handles through C;
- passes all four Windows tests against both genuine fixtures, with native
  CMake `/W4 /WX` and the Visual Studio solution build succeeding.

Evidence:

- Step 04 image SHA-256:
  `FD36A1527F1E2E5E8905314769C5F5FCDB1788FBDA3848ECA4765FBB658254C0`;
- `multi.bin`: 300,000 bytes,
  `4D10D520EC99C6FAFADD95BEE19D13AB272F132B47D5B5FFE252BC7498006463`;
- `sparse.bin`: 300,000 bytes with a 128 KiB hole,
  `D8989B12168EF493193A84C62328C99825CCB5E5F616814F33FD5AB2D867EA40`;
- `/nested/nested.txt`: object 256,
  `9316BCDAEBF6D62525D6FAF5E446CDA3A712EC32A7308307C2BB44C74A287902`;
- root enumeration returns exactly `fixture.txt`, `multi.bin`, `nested`, and
  `sparse.bin`, without duplicates;
- before/after image hashes are identical.

Completed commits include `native: read multiblock and sparse files`,
`native: resolve nested filesystem paths`, `native: enumerate filesystem
directories`, and `native: add filesystem object handles`.

