# `mpegts-recover`

> [!WARNING]
> This program is an early prototype.
> It hasn't been tested thoroughly.
> Expect errors when running this program.

**`mpegts-recover`** is a simple C# console application that **detects and recovers MPEG-TS datastreams** spread across **a file system image or a block device**.

Use cases for this program include recovering TS data when its file system entry is corrupt due to a failing hard drive.

E.g.: I was able to recover a multi-gigabyte TV recording that was reported to only have 72 KiB because its NTFS file entry was corrupt.

## Supported Formats

- [x] Standard MPEG-TS (188-byte packets)
- [x] [BDAV/M2TS](https://en.wikipedia.org/wiki/M2TS) (192-byte packets)
- [ ] RS204 (204-byte packets)

