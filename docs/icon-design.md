# Application icon

Created with the built-in image generation tool, using the user's DiskAtlas icon as a style reference. The generated PNG has a transparent background outside the tile.

- Source: [app-icon.png](../src/NesEmulator/Resources/app-icon.png)
- Windows asset: [app.ico](../src/NesEmulator/Resources/app.ico)
- Rebuild: `./tools/make-app-icon.ps1` on Windows
- ICO sizes: 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixels, with 32-bit transparency

The project embeds the ICO in the executable and as an assembly resource for the window/taskbar icon. No separate icon file is required at runtime.

## Generation prompt

Use case: logo-brand. Asset type: Windows desktop application icon for a NES emulator. Create one polished square 1024x1024 icon. The attached DiskAtlas icon is a STYLE REFERENCE ONLY, not an edit target: match its extremely clean flat geometric shapes, dark graphite rounded-square tile, thin subtle slate outline, bright cyan/mint/violet accents, and generous corner radii. Replace the treemap symbol with a single immediately recognizable classic rectangular 8-bit game controller silhouette, centered and filling about 82 percent of the tile width. Controller has a light cool slate rounded rectangular housing, a dark charcoal inset face, a bold mint plus-shaped D-pad on the left, two vivid coral-red circular action buttons on the right, and two small cyan rounded select/start pills in the lower middle. Keep all shapes few, thick, crisp and balanced so it reads clearly at 16 and 32 pixels. Flat vector aesthetic, restrained subtle edge highlights only, no 3D perspective, no photorealism, no glow, no lettering, no numbers, no brand logos, no cable, no extra objects, no mockup. The dark rounded-square tile should fill approximately 94 percent of the canvas, with a genuinely transparent alpha background outside its rounded silhouette. Entire icon inside canvas with even margins. Output the icon itself, not a presentation.

The tool returned a 1254×1254 PNG. The ICO build only resizes and packages that original artwork; it does not redraw the design.
