# ZSingularity Mod re-encoder via AssetsTools.NET

an iOS tweak that's capable of arbitrarily modifying graphical related settings in Limbus Company by leveraging public classes found in Assembly CSharp.

It also possesses a cosmetic mod loader, a feature exclusive to desktop, now brought to the iPhone. It works by sending over the bundles into a private repo with AssetTools.NET running, the workflow re-encodes and re-targets the bundle for us, then returns it for us to load it in

the sole purpose of this tweak is to give more flexibility and fine-tuning capabilities regarding graphical settings targeting performance.

## Two pipelines

- `BundleDoctor <input> <output> [--original original.bundle] [format] [tpk]` -
  the original desktop-to-iOS convert direction. Decodes and re-encodes/
  retargets the whole modded bundle, restoring Shader bytes byte-for-byte from
  `--original` where given. Left as-is for whatever still uses it, but Material
  assets turning out to also carry platform-specific data (on top of Shaders)
  made this direction unsustainable as the primary workflow.

- `BundleDoctor transplant <original.bundle> <modded.bundle> <output.bundle>
  [--threshold N] [--dry-run] [--new-texture-format FMT] [tpk]` - the new
  primary workflow. Starts from the known-good original bundle and only ever
  overwrites Sprite and Texture2D assets, never touching Shader/Material at
  all: Sprites are transplanted byte-for-byte wherever they differ from (or
  are absent in) the original; Texture2D assets are decoded on both sides to
  RGBA32 and compared with a resolution-independent content diff (see
  `TextureCodec.cs`) so only textures the mod actually changed pay the
  re-encode cost. See `TransplantMode.cs`'s header comment for the full
  rationale and matching-key assumptions.
