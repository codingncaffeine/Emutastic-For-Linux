# Emutastic for Linux

A native Linux port of [Emutastic](https://github.com/codingncaffeine/Emutastic) — the libretro-based
multi-system emulator frontend — rebuilt on **.NET 10 + Avalonia 12**, packaged as a `.deb`.

The goal is a **1:1 clone**: aesthetically and functionally identical to the Windows app, with only the
platform plumbing swapped underneath (WPF → Avalonia, Direct3D → OpenGL/Vulkan, WASAPI → SDL/OpenAL,
Win32 core loading → `dlopen`, etc.).

## Status

🚧 Early development — project scaffolding.

## Building

Requires the .NET 10 SDK and Avalonia 12.

```sh
dotnet build -c Release
```

## Relationship to upstream

This tracks the upstream Emutastic feature set (currently v1.7.x). The original Windows source is
referenced during porting but is not part of this repository.

## License

See [LICENSE](LICENSE) (mirrors upstream Emutastic licensing).
