# VideoShrink

**A video compression tool that supports high compression efficiency**

This is a simple, out-of-the-box video compression tool that comes with an H.264 encoder by default, which balances speed and compression quality. It also supports compressing videos to H.265 and AV1 formats at extremely high compression rates, while maintaining specified resolutions and file sizes.

---

## Why another transcoder?

Our tool is ready to use right out of the box, with no additional dependencies required. It’s more streamlined than most similar tools and is one of the few out-of-the-box video compression tools that offers an encoder with high compression ratios. (Plus, it features a modern GUI that’s not too ugly.)
Please note that encoders with high compression ratios often require a significant amount of CPU time. By default, we have selected the fast H.264 encoder, which supports GPU acceleration. If you need a better compression ratio, please switch to the x265 or svtav1 encoder in the settings.

## Before you run it

We have set the default option to create a new folder for saving the output. You can manually switch to override mode to prevent a new folder from being created, but doing so carries the risk of permanently losing the source files. Please assume full responsibility for any consequences resulting from this action. Our program is not perfect, so please be sure to back up your important videos yourself to prepare for potential issues such as metadata loss.

Our default encoder is H.264, which offers standard encoding efficiency. It is optimized for GPUs from only three manufacturers: NVIDIA, Intel, and AMD. If your GPU is not one of these three types, or if it is incompatible with our encoder, the program’s encoding speed may be very slow. 

If you select the x265 or svtav1 encoder, we can achieve better compression rates, but the process can only be performed using the CPU, and it will be very slow. Based on our experience, encoding a 25-minute 4K video using the svtav1 encoder at compression level 4 typically takes more than 1.5 hours. Please choose an appropriate encoder based on your needs, and we recommend enabling GPU-assisted decoding.

(Most of the following content was written by artificial intelligence; please use it as a reference at your discretion.)

## Output: 3 locations x 2 save modes

|  | **New folder** (default) | **Direct save** (overwrites name collisions) |
|---|---|---|
| **Source dir** | `src/src_shrink_output/`, originals untouched | writes back into `src/` -> **replaces the originals** |
| **Fixed path** | `chosen/src_shrink_output/` | writes straight into `chosen/`, same-name files overwritten |
| **Ask every time** | prompts for a directory, then as above | prompts for a directory, then as above |

The new folder is created **before** any encoding starts; if it cannot be created, the batch aborts
immediately and not a single file is processed. In direct-save mode, a same-named file at the
destination is overwritten **without being verified first** — you are accepting that.

Scanning is **top-level only by default**. Recursion is what lets a second run walk into an output
folder created by an earlier one, so it is opt-in.

### It will not compress its own output twice

Re-compressing an already-compressed file is an irreversible quality loss, and the obvious guard —
"remember which source paths we processed" — breaks in exactly the cases that matter:

- **Direct save into the source directory** puts the result at the *same path* as its input. Lose the
  ledger and the next run compresses it again, in place.
- **Recursive scanning** walks into the `_shrink_output` folder from an earlier run. Those files were
  never processed *as sources*, so nothing in the ledger mentions them.
- Move or rename an output and any path-based bookkeeping is void.

So VideoShrink checks two independent channels, in both save modes:

1. **Output-directory history** — every output folder it has ever written to is remembered. Anything
   found inside one is flagged without even opening the file.
2. **Output fingerprints** — each result is recorded with its name, size and an XxHash64 digest. A
   matching *name and size* is only a suspicion; the hash is what confirms the file really is the
   output we wrote and not something you put there afterwards. Nothing is read unless the name and
   size already collide, so an ordinary scan touches no file contents at all.

If anything is flagged, you are shown what and why, and you choose: **abort** (the default and the
recommendation), **exclude the already-processed files**, or **include everything and compress them
again**.

Both channels live in the ledger at `records/transcode.db`, next to the executable rather than in
your media folder. Deleting it is the intended way to clear this history if you want the guard to
stop getting in your way.

## Encoders

| | For | Rate control |
|---|---|---|
| **H.264** (default) | ordinary use — balanced speed and quality | GPU when available (**discrete -> integrated -> CPU**, validated by a real trial encode). Hardware encoders cannot do ffmpeg's two-pass, so size accuracy is lower. |
| **x265** (HEVC) | compression efficiency | CPU, two-pass ABR, hits the size target precisely |
| **SVT-AV1** | maximum compression efficiency | CPU, two-pass ABR, slowest |

Preset lists are ordered *highest compression -> fastest* and labelled as such. The two preset
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
- Resize: scale (keep aspect, set height) or **stretch** to an exact width x height.
- Filters: minimum file size (default 256 MB), minimum source height, optionally minimum width.
- Pause (the encoder process is genuinely suspended, CPU drops to zero), Stop, incremental resume.
- **Three panel layouts**: a curated default, an "advanced" mode that exposes everything, and a
  **custom mode** where you tick which options to hide and build the panel you actually want.
  Hidden options keep working — hiding is a display choice, not a behaviour change. Every change
  is written to `settings.json` immediately, and there is a *Reset to defaults* button.

## Requirements

- Windows 10/11, x64
- `ffmpeg.exe` / `ffprobe.exe` in `tools/` — see [`VideoShrink/tools/README.md`](VideoShrink/tools/README.md)

## Building

```sh
cd VideoShrink
dotnet publish -c Release
```

## How this was written

This project was completed with the help of the AI assistant Claude. Although we conducted numerous manual and automated tests, the project may still have some shortcomings; we appreciate your understanding. To keep the project’s functionality simple and provide users with as streamlined a dashboard as possible, we have omitted many features and have not included configuration options for various default behaviors, including validation functions. We sincerely apologize for any confusion caused by these design choices, including but not limited to ambiguous interpretations and undeclared default actions.

## Licence

GPL-3.0. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
