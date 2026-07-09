# VideoShrink

**Shrink a folder of videos to a target size — without touching your originals.**

A Windows desktop GUI that batch-transcodes a folder. By default it writes results into a new
`<folder>_shrink_output` directory and **never deletes anything**. If you *want* it to replace
the originals in place, that is a mode you choose deliberately — and even then, nothing is
overwritten until the result has been cross-checked against the source with `ffprobe` and fully
decoded end-to-end.

---

## Why another transcoder?

Most tools ask you for a CRF or a bitrate. VideoShrink asks you for a **size**:

- *"Make every file 15% of its current size."*  (ratio mode)
- *"Make every file fit in 8 MB."*  (absolute mode — the Discord problem)

It reads each file's real duration and per-track audio bitrates, derives the video bitrate that
hits the target, and encodes with two-pass ABR so the target is actually met. Audio at or below
320 kbps is stream-copied by default and simply counted against the budget; if that squeezes the
video bitrate into unusable territory, you are told up front — and audio is only compressed if
you say so.

The second reason is the safety machinery:

- The encode happens in a **cache directory next to the program**, never in your media folder.
- Before anything is written, `ffprobe` cross-checks the result against the source:
  **container type**, video codec, resolution, duration, frame rate, **colour tags**,
  **every audio track** (codec / channels / sample rate), subtitle count, cover art, and size.
- Then `ffmpeg -xerror` **decodes the whole file** looking for hidden corruption.
- Only then does it land: copy to the target volume → verify length → (overwrite mode only:
  delete the old file) → atomic rename. Filename **and extension** stay byte-identical.
- A crash at any point is recoverable. Leftover temp files are reconciled on next launch, and the
  two failure shapes are distinguished: an unfinished *publish* is discarded (your source is
  untouched), while an interrupted *overwrite* is completed. A corrupt temp is kept as
  `*.corrupt` and loudly reported — never silently deleted.
- Every file lands in a SQLite ledger with before/after `ffprobe` snapshots and a **notes**
  column recording anything lossy that happened.

Before the first frame is encoded, a **pre-flight dialog** lists everything that will be lost or
re-encoded — per file, per stream — and lets you cancel. Continue means *keep as much as
possible*: tracks the container can hold are copied, text subtitles are converted where they can
be, and only the genuinely impossible ones are dropped and recorded.

## Output: 3 locations × 2 save modes

|  | **New folder** (default) | **Direct save** (overwrites name collisions) |
|---|---|---|
| **Source dir** | `src/src_shrink_output/`, originals untouched | writes back into `src/` → **replaces the originals** |
| **Fixed path** | `chosen/src_shrink_output/` | writes straight into `chosen/`, same-name files overwritten |
| **Ask every time** | prompts for a directory, then as above | prompts for a directory, then as above |

The new folder is created **before** any encoding starts; if it cannot be created, the batch aborts
immediately and not a single file is processed. In direct-save mode, a same-named file at the
destination is overwritten **without being verified first** — you are accepting that.

## Encoders

| | For | Rate control |
|---|---|---|
| **H.264** (default) | ordinary use — balanced speed and quality | GPU when available (**discrete → integrated → CPU**, validated by a real trial encode). Hardware encoders cannot do ffmpeg's two-pass, so size accuracy is lower. |
| **x265** (HEVC) | compression efficiency | CPU, two-pass ABR, hits the size target precisely |
| **SVT-AV1** | maximum compression efficiency | CPU, two-pass ABR, slowest |

Preset lists are ordered *highest compression → fastest* and labelled as such. The two preset
fields are separate: SVT-AV1 takes a number, x264/x265 take names — feeding one to the other used
to silently downgrade the encode, and now cannot.

Auto GPU selection prints the implementation it chose. Explicitly selecting an implementation that
this machine cannot run is a **hard error**, never a silent fallback. There is also a
**GPU decode offload** switch: it accelerates decoding only, so compression efficiency is untouched.

## Other features

- Container-aware: the output container follows the **source extension**, and encoder flags are
  gated per container. Extensions that cannot carry the chosen codec (`.ts`, `.flv`, `.webm`+HEVC)
  are excluded during the scan rather than producing garbage. Audio white-lists are based on the
  muxers' *measured* behaviour, not the spec — `.m4v` really does reject MP3.
- Per-stream handling: one incompatible audio track no longer drags the rest into a re-encode, and
  one un-embeddable subtitle no longer takes the others with it.
- Subtitles, chapters, metadata, MKV attachments and cover art are carried over where possible.
- Resize: scale (keep aspect, set height) or **stretch** to an exact width × height.
- Filters: minimum file size (default 256 MB), minimum source height, optionally minimum width.
- Pause (the encoder process is genuinely suspended, CPU drops to zero), Stop, incremental resume.
- **Three panel layouts**: a curated default, an "advanced" mode that exposes everything, and a
  **custom mode** where you tick which options to hide and build the panel you actually want.
  Hidden options keep working — hiding is a display choice, not a behaviour change. Every change
  is written to `settings.json` immediately, and there is a *Reset to defaults* button.

## Requirements

- Windows 10/11, x64
- No .NET install needed (self-contained single-file build)
- `ffmpeg.exe` / `ffprobe.exe` in `tools/` — see [`VideoShrink/tools/README.md`](VideoShrink/tools/README.md)

## Building

```sh
cd VideoShrink
dotnet publish -c Release
```

## ⚠️ Before you run it

Transcoding is lossy by definition, and no amount of checking makes a re-encode identical to the
source. The default mode leaves your originals alone; **direct save mode deletes them.**

**Back up anything you cannot afford to lose, and try a copy of a few files first.**

## Licence

GPL-3.0. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
