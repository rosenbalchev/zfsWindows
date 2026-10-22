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
33. The head dataset object 54 is now decoded through its DSL dataset bonus
and its child objset root block is read and validated as a dnode block.
The dataset objset multi-level dnode chain now reaches the filesystem master
node block (object 1), validated as a ZFS master-node dnode. The current Visual
Studio installation is temporarily incomplete for CMake manifest linking
(`rc.exe` is missing), so this checkpoint was additionally verified with the
available MSVC compiler/linker directly. The master-node micro-ZAP now resolves
`ROOT` to object 34. Filesystem dnode indexing is now generalized beyond object
1, and object 34's directory dnode and embedded LZ4 data block can be read
through the native ABI and C# wrapper. The genuine fixture confirms the
directory dnode type, a 512-byte logical block, and the `ZBT_MICRO` header.
The pool-root child map is now decoded from its embedded LZ4 micro-ZAP and
resolves `data` to DSL directory object 385 and head dataset object 388. The
walker therefore opens the populated `zfstest/data` objset instead of the empty
pool-root filesystem. Its directory block contains the expected `fixture.txt`
entry and object 2 is validated as a plain-file dnode. Embedded block-pointer
payload extraction follows OpenZFS by excluding the property and logical-birth
words. LZ4 decompression now calls the vendored OpenZFS implementation rather
than a local decoder. The filesystem dnode walker uses the 128 KiB indirect
block's 1024 block pointers (10 index bits per level). Directory-entry lookup,
regular-file byte reads, checksums, sparse files, and the final handle ABI
remain follow-up work. This checkpoint passes all 3 Windows tests against the
genuine fixture while preserving its SHA-256 hash.

Commit: `native: read datasets directories and files`

