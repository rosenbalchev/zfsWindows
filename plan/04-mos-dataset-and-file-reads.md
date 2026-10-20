# Step 04 — MOS, datasets, directories, and file reads

Status: `planned`

Objective: read actual filesystem content through ZFS metadata and block
pointers.

Implementation:

- read and validate the MOS root;
- implement block-pointer checksum and compression dispatch;
- traverse DSL datasets and ZAP directory objects;
- enumerate directories and resolve UTF-8 names;
- read regular files with overflow and sparse-file handling;
- expose pool, dataset, directory, and file handles through the C ABI.

Exit evidence: a genuine upstream fixture can be opened, enumerated, and read
with exact lengths and SHA-256 hashes from C#.

Commit: `native: read datasets directories and files`

