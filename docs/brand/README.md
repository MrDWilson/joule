# Joule brand

![Joule banner](joule-banner.png)

## The mark

A brilliant-cut jewel split by a lightning bolt. A joule is a unit of energy and the word sounds like "jewel", so the mark combines a bolt and a jewel. The bolt's horizontal jog sits on the gem's girdle and its tip is the gem's bottom point. The gem keeps its full outline, and the bolt reads as a cut through it.

It is drawn on a 64-unit grid with eight flat facets and no outlines, so it stays legible at 16 px. At favicon size it shows as a green gem with a dark notch; from 32 px up the bolt and facets are clear.

| Variant | File | Use |
| --- | --- | --- |
| Tile | `web/public/favicon.svg`, `icon-192.png`, `icon-512.png` | Favicon, app icons, anywhere the background is unknown |
| Full-bleed | `web/public/apple-touch-icon.png`, `icon-maskable-512.png` | iOS home screen and Android adaptive icons, which apply their own corner mask. The gem sits inside the 80% safe zone |
| Bare | `<BrandMark variant="bare" />` | On the app's dark UI. The bolt is transparent, so the surface shows through |
| Mono | `web/public/mask-icon.svg`, `<BrandMark variant="mono" />` | Single colour (`currentColor`), for print, Safari pinned tabs and one-colour contexts |

In React, use `web/src/components/BrandMark.tsx`:

```tsx
<BrandMark size={32} />                        // tile (default), same as the favicon
<BrandMark size={32} variant="bare" />
<BrandLockup size={32} />                      // bare mark + "Joule" wordmark
<BrandLockup size={40} tagline="Battery plan watcher" />
```

`BrandLockup` text inherits `color`; the tagline is drawn at 62% opacity.

## Colours

| Role | Hex |
| --- | --- |
| Tile / dark surface | `#0a1219` (app background `#080d13`) |
| Accent (app) | `#63e6bd` |
| Crown, lit side | `#b2fae1`, `#9df5d6` |
| Crown, far side | `#7cefc9`, `#6be6be` |
| Pavilion, lit side | `#55dfb6`, `#46d3aa` |
| Pavilion, shadow side | `#1f9f84`, `#188873` |
| Wordmark | `#ecf3f7` |

## Wordmark

"Joule" is set in the UI font (Inter, falling back to the system sans) at weight 650–700 with tight tracking (about −0.03em). There is no custom lettering, so the wordmark always matches the app.

## Usage

- Keep clear space around the mark of at least a quarter of its width.
- Don't recolour individual facets, add outlines or rotate the mark.
- Say "works alongside Predbat" or "for Predbat". Don't present Joule as Predbat or as part of it. It is an independent project.

## Banner

`joule-banner.svg` and `joule-banner.png` are 1280×640 and work as a GitHub social preview or README header. The PNG is rendered from the SVG in Chrome, so regenerate it whenever the SVG changes.
