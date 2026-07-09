# VideoShrink

**Shrink a folder of videos to a target percentage of their original size — and never lose a file.**

A Windows desktop GUI that batch-transcodes a folder and **replaces the originals in place**,
but only after the result has been cross-checked against the source with `ffprobe` and fully
decoded end-to-end. If any check fails, the original is left byte-for-byte untouched.

---

## Why another transcoder?

Most tools ask you for a CRF or a bitrate. VideoShrink asks you for a **size target**:

> *"Make this folder 15% of its current size."*

It reads each file's real duration and audio bitrate, derives the video bitrate that hits that
target, and encodes with two-pass ABR so the target is actually met.

The second difference is that it will **overwrite your originals** — which is exactly the thing
most GUIs refuse to do, because getting it wrong destroys data. The safety machinery is the
point of this project:

- The encode goes to a **cache directory on a different drive**, never next to your media.
- Before anything is deleted, `ffprobe` cross-checks the result against the source:
  container type, video codec, resolution, duration, frame rate, **colour tags**,
  audio stream count/channels/sample-rate, subtitle stream count, and output size.
- Then `ffmpeg -xerror` **decodes the entire file** looking for hidden corruption.
- Only then: copy to the target volume → verify length → delete the original → atomic rename.
  Filename **and extension** stay byte-identical, so media libraries don't re-index or re-download.
- A crash at any point is recoverable. On next launch, leftover `*.transcoding.tmp` files are
  reconciled: original still present → discard the temp; original gone but temp verifies →
  finish the rename; temp is corrupt → keep it as `*.corrupt` and shout. It is never silently deleted.
- Every file — success, failure, or skip — lands in a SQLite ledger with the full before/after
  `ffprobe` snapshots, plus a **notes** column recording anything lossy that happened
  (audio re-encoded, subtitles dropped, cover art discarded).

If a file fails, it is skipped and the batch continues. Your original survives.

## Features

- Encoders: **SVT-AV1** (`preset 0–13`) or **x265** (`veryslow…veryfast`), 10-bit, software only.
  Both preset lists are ordered *highest compression → fastest*, and labelled as such.
- Container-aware: output container is derived from the **source extension**, and encoder
  parameters are gated accordingly (`+faststart` and `hvc1` only for MP4/MOV; attachments only
  for MKV). Files whose extension cannot hold the chosen codec (`.ts`, `.flv`, `.webm`+HEVC)
  are excluded during the scan instead of producing garbage.
- Audio is **stream-copied** whenever the container permits. When it doesn't, it falls back to
  AAC/Opus and says so in the ledger — or, with `AudioPolicy = CopyOnly`, refuses to touch the file.
- Subtitles, chapters and metadata are carried over; anything that has to be dropped is recorded.
- Pause (the encoder process is genuinely suspended, CPU drops to zero), Stop, and incremental resume.
- Progress, remaining count, ETA, and cumulative space saved.

## Current limitations (honest list)

This started life as a purpose-built tool and is still growing into a general one:

- It always **downscales to a target height** (default 1080) and only picks up files at or above
  a source-height threshold (default 2160). "Compress without resizing" is not yet an option.
- The size target is a **percentage of the original**. An absolute target ("make it 8 MB") is not
  yet implemented.
- Output is always in-place. "Write to another folder" is not yet an option.
- No hardware encoders. Deliberate for now — NVENC/QSV/AMF are markedly less efficient at a fixed
  size target, which is the whole premise here.

## Requirements

- Windows 10/11, x64
- No .NET install needed (self-contained single-file build)
- `ffmpeg.exe` / `ffprobe.exe` next to the executable in `tools/` — see [`VideoShrink/tools/README.md`](VideoShrink/tools/README.md)

## Building

```sh
cd VideoShrink
dotnet publish -c Release
```

Output lands in `bin/Release/net9.0-windows/win-x64/publish/`.

## ⚠️ Before you run it

**VideoShrink deletes your original files and puts smaller ones in their place.**
The verification is thorough and the replacement is crash-safe, but transcoding is lossy by
definition, and no amount of checking makes a re-encode identical to the source.

**Back up anything you cannot afford to lose, and try a copy of a few files first.**

## Licence

GPL-3.0. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
