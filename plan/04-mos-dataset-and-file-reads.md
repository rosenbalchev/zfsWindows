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
block's 1024 block pointers (10 index bits per level).

The root-directory micro-ZAP lookup now decodes the ZFS directory-entry object
number, resolving `fixture.txt` to object 2. The initial regular-file reader
validates its plain-file dnode and system-attribute bonus, extracts the exact
27-byte size from the fixture's OpenZFS SA layout 2, and decodes the embedded
LZ4 data block through the C ABI and managed wrapper. General SA layout-table
resolution, multi-block and sparse files, path traversal, and the final handle
ABI remain follow-up work. This checkpoint passes all 3 Windows
tests against the genuine fixture while preserving its SHA-256 hash.

All non-embedded blocks reached through the current reader now validate their
OpenZFS Fletcher-4 checksum before decompression. Checksum coverage includes
the MOS root selected by the uberblock, MOS and dataset indirect blocks,
dnode blocks, ZAP blocks, and non-embedded file data. Multi-block and sparse
file behavior and path traversal remain unfinished.

File-size lookup no longer assumes fixture layout 2. It resolves `SA_ATTRS`
from the filesystem master node, resolves the `LAYOUTS` fat ZAP from the SA
master node, decodes the layout's uint16 attribute array, applies OpenZFS's
8-byte SA attribute alignment and variable-length table, and locates
`ZPL_SIZE`. The existing fixture read continues to return exactly 27 bytes
through this metadata-driven path.

Regular-file reads now derive the data-block size and indirect fanout from the
dnode, walk up to six block-pointer levels, select among top-level dnode block
pointers, and expand hole block pointers as zero-filled ranges. The managed
wrapper first queries the exact SA file size, avoiding the former fixed 128 KiB
buffer. The separate Step 04 fixture has image SHA-256
`FD36A1527F1E2E5E8905314769C5F5FCDB1788FBDA3848ECA4765FBB658254C0`.
Its 300,000-byte `multi.bin` reads with SHA-256
`4D10D520EC99C6FAFADD95BEE19D13AB272F132B47D5B5FFE252BC7498006463`;
its 300,000-byte `sparse.bin` expands the middle 128 KiB hole and reads with
SHA-256
`D8989B12168EF493193A84C62328C99825CCB5E5F616814F33FD5AB2D867EA40`.
All four Windows tests pass against the Step 03 and Step 04 images while both
image hashes remain unchanged. The directory lookup ABI now accepts any
directory object, and the managed wrapper resolves slash-separated paths from
the filesystem `ROOT`. `/nested/nested.txt` resolves through directory object 3
to file object 256 and reads with SHA-256
`9316BCDAEBF6D62525D6FAF5E446CDA3A712EC32A7308307C2BB44C74A287902`.
The native enumeration ABI counts and returns bounded micro-ZAP entries with
their UTF-8 name bytes, 48-bit object number, and ZFS directory-entry type. The
Step 04 root enumerates exactly the four unique names `fixture.txt`,
`multi.bin`, `nested`, and `sparse.bin`; `/nested` enumerates `nested.txt` as
regular-file type 8. The final object-handle ABI remains unfinished.

Commit: `native: read datasets directories and files`

