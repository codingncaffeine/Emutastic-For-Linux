# Findings So Far — GPU platform investigation & pivot

_Last updated: 2026-06-04. Written on the iMac17,1 (eldritch-imac171); intended to be read
later from a fresh checkout on the NEW machine (Debian on a GTX 1080 gaming laptop)._

## Why this file exists

We spent real effort chasing why GPU-heavy libretro cores (GameCube/Dolphin, N64-3D,
Dreamcast) couldn't hit 60fps on the iMac. The conclusion is now **firm and closed**: it was
the **hardware/firmware of the iMac's GPU**, not our app. We are **pivoting to a bare-metal
Debian install on a gaming laptop with an NVIDIA GTX 1080** to finish the project on a platform
where the GPU actually works. This file is the handoff so we don't relitigate any of it.

---

## 1. The iMac GPU dead-end (CLOSED — do not re-investigate)

**Machine:** iMac17,1, AMD Radeon **R9 M395X = Tonga XT (VI / GFX8), PCI 0x1002:0x6920**,
Apple subsystem `0x106B:0x014D`, Apple EFI VBIOS `113-C905A0-007`.

**Symptom:** GPU clocks frozen at the boot minimum — **sclk 318MHz, mclk 300MHz** — forever.
`power_dpm_force_performance_level=high` is accepted but silently ignored. 2D cores fine (60fps);
heavy 3D cores capped (GameCube ~26fps @4x, identical in RetroArch → our pipeline is exonerated).

**Root cause (proven, in order of how we learned it):**
- The SMU (GPU power microcontroller) **stops acking messages** shortly after init. dmesg spams
  `amdgpu: last message was failed ret is 0` forever (SMC_RESP register stays 0 = no ack).
- A **boot-time kprobe trap** (armed via systemd) confirmed there is **no single "killer
  message"** — every captured SMU send is routine sensor polling or our own force-level pokes,
  all timing out. The SMU was already wedged by the time the trap armed (54s); the wedge happens
  ~6–8s into boot, which our trap was too late to catch.
- `amdgpu.pg_mask=0` (disable all powergating) — **refuted** the powergating theory; no change.
- "Voltage Table empty." at boot is a **red herring** (benign VDDGFX boot-voltage lookup).

**Why we can't fix it from software (the important part):**
- On **VI / GFX8 (Tonga), ALL clock control is routed through the SMU.** Unlike CIK/Sea-Islands
  and older, there is **no direct PLL/register path** to set GFXCLK. The SMU is the clock
  controller, and it's the wedged component — so nothing can move the clock.
- Timur Kristóf's well-known iMac amdgpu fix ("disable MCLK DPM, force highest clock when DPM
  off") is for the **M380 = Bonaire = Sea Islands (CIK)** — a *different chip* with a direct
  clock path. **It does not apply to our Tonga/VI M395X.** Building a patched amdgpu would
  emit the same SMC msg `0x145` we already measured timing out. Confirmed dead end.
- Corollary: what we actually want (clocks that ramp under load, idle low/quiet) **IS** normal
  DPM — exactly the thing the wedged SMU prevents. No driver patch can synthesize on-demand
  ramping while bypassing the SMU.

**The only theoretical fix** would be reviving the SMU (understanding why the Apple Tonga SMU
stops acking ~6–8s post-init), which needs an *earlier-arming* boot trap = more reboots + a real
research gamble. **Not worth it** — see the pivot. macOS/OpenEmu drive this same chip fine
because Apple's driver talks to the SMU correctly.

**Artifacts (on the iMac, ~/):** `smu-boottrap/`, `/var/log/smu-boottrap.txt`, `smu-validate.*`,
`smu-poke.*`, `smu-trace.*`, `vbios.rom`, `NEXT-STEPS-gpu-fix.txt`.

---

## 2. The pivot — Debian + GTX 1080 (the new home for this project)

**Why this is the right move:** a GTX 1080 (Pascal) on bare-metal Debian with the **native
NVIDIA proprietary driver** gives us, for the first time:
- **Real, working GPU clocks/DPM** — none of the iMac's frozen-clock ceiling. The 1080 is
  vastly more GPU than any libretro core needs, including the heavy 3D ones.
- **Real vsync and real present timing** — i.e., a *representative* platform. This matters more
  than raw speed: every pacing conclusion we draw here is finally trustworthy.

**Why NOT WSL2 / a VM (we evaluated these):** WSL2/WSLg and translation VMs (VMware SVGA3D) run
GL/Vulkan through a **D3D12 translation layer** with no true Linux vsync — they'd give fast but
**misleading** pacing results (a fresh "false lighthouse"). Only GPU passthrough (needs a 2nd
GPU) or **bare metal** is representative. Bare-metal Debian on the laptop = the clean choice.

---

## 3. What's actually LEFT to do (the real work, now testable for real)

The project's central unsolved problem was never the iMac clocks — it's **in-game frame
pacing/jitter**. The standing plan (see repo history + dev notes) is:

- **THE FIX:** keep OpenGL + Avalonia, but swap the in-loop **pacing method** from
  "vsync-swap-as-clock" to **audio master clock + dynamic rate control (DRC)** — RetroArch's
  approach — with the GL swap demoted to a tear-free present, not the timing source.
- Architecture is already right: games render via a **separate `--game-host` process** running
  the RetroArch OpenGL model (in-process Avalonia+SDL-GL hangs after present #1). Keep that split.
- The **never-block 4-PBO fence ring** (commit a20a009) solved N64/3D readback stalls — reuse the
  same ring for GC/PSP/DC; never put a blocking GL call on the emu thread.

On the 1080 box this can finally be A/B-tested with audio as the clock against a real vsync
signal, instead of guessing against broken hardware.

---

## 4. NVIDIA-on-Linux gotchas to watch on the new box

- Install the **proprietary NVIDIA driver** (Debian `nvidia-driver`, non-free-firmware enabled);
  the 1080 is Pascal — well supported. Avoid nouveau for anything perf-sensitive.
- **Start on X11**, not Wayland. NVIDIA's GL present/vsync is far more predictable on X11; our
  whole pacing investigation depends on trustworthy present timing. Revisit Wayland later.
- NVIDIA's GL present timing differs from AMD/Mesa, so numbers won't perfectly mirror an AMD
  Linux user — but it's a **real native GPU with real vsync**, which is what we need to get the
  pacing *fundamentally* right. Validate the final result on AMD/Mesa eventually if shipping.
- Re-confirm the toolchain on the new box: **.NET 9 SDK** (was at `~/.dotnet` on the iMac because
  apt was broken on trixie), **Avalonia 11.3**. NOTE the iMac ran a **vendored Avalonia 12.1.999**
  for X11 drag-drop (PR #20926) — check whether that's still needed / has shipped officially.
- Always test the **Release** build (the iMac's desktop shortcut ran Release; Debug-only builds
  meant testing stale code).

---

## 5. Quick pointers

- Repo: `git@github.com:codingncaffeine/Emutastic-For-Linux.git` (this file is at repo root).
- Diagnostic logs live in `~/.local/share/Emutastic/Logs/` (startup_timings, ui_freezes, crash,
  emulator-host).
- Aesthetic rule: match the upstream WPF Emutastic look exactly; consult upstream source.
- Golden rule: **the UI thread never blocks** — all blocking work off the UI thread.

**Bottom line for future-me:** the GPU mystery is solved and was hardware. On the 1080/Debian box,
stop worrying about clocks entirely and go straight at the **audio-master-clock + DRC pacing**
work against real vsync. That's the finish line.
