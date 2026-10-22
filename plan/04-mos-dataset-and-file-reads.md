# Step 04 — MOS, datasets, directories, and file reads

Status: `in progress`

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

Progress: the native read-only layer now validates the MOS root, resolves the
meta-dnode's first indirect block, supports ZLE and OpenZFS LZ4 framing, and
returns the first 16 KiB MOS dnode data block through the C ABI and C# wrapper,
then resolves object 1 to its first 16 KiB object-directory ZAP block. The
fixture test confirms the expected empty object-0 dnode, object-directory dnode,
ZAP magic, and a fat-ZAP lookup of `root_dataset` to object 32. Remaining work
is checksum verification, full ZAP/dataset traversal, sparse and regular-file
reads, and the final handle ABI. Object 32 DSL-directory decoding now resolves
head dataset object 54, child-directory ZAP object 34, and properties ZAP object
33.

Commit: `native: read datasets directories and files`

