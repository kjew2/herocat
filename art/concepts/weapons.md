# Weapon concepts

Presentation only: names, descriptions and look briefs for the rows in `content_src/weapons.csv`. Ids, rarities, elements and numbers belong to `specs/04` section 6. The full roster of 30 is issue #288; every row not listed here still has a placeholder name. The art pilot is issue #290, and its rows come from the artist's references in `art/reference/weapons/`.

| Row | Rarity | Element | Name | Type | Reference |
|---|---|---|---|---|---|
| `weapon_e_06` | Epic | DARK | Twin Crystals | Dual katana | `art/reference/weapons/twin_crystals.png` |

## Twin Crystals
- **Description (`weapon.e_06.desc`):** Dual katana cut from one violet crystal. Every swing sheds a storm of glittering shards.
- **Silhouette:** a matched pair of curved katana. The blades are faceted, iridescent crystal, the grips are black-wrapped with white diamond inlays, and the guards are gold-trimmed crystal claws. A pale violet tassel with crystal charms hangs from each pommel.
- **Palette:** violet, lilac and iridescent white on the blades, black grips, gold trim.
- **Motif:** shards and sparks peeling off the edge, with a violet glow wrapped around each blade.
- **Why this row:** the art reads as Epic-to-Legendary grandeur, and #290's pilot asks for one Epic. Violet fits DARK, the same way it does for Yuki (`comp_l_02`). Moving it to another row only means editing this table and the localization rows.

## Pilot asset status (issue #290)
The images in Unity are **stand-ins cut from the reference** by `tools/art/cut_pilot_assets.py`. They don't replace the artist's deliverables.

| Asset | Stand-in | Final, from the artist |
|---|---|---|
| Icon `icon_weapon_*` (128 rp) | square crop of the sheet inside its frame, background included | icon drawn for 88-128 rp |
| Held sprite `spr_weapon_*` (160 rp, 20 px native) | the front blade masked off the background and sampled down to 20 px, 12 colours, 1 px dark outline | pixel sprite on the locked palette (`specs/07` section 15.3) |

Open for the art bible (issue #287): **dual weapons.** The squire has one held-sprite slot, so the stand-in holds only the front blade. A pair either needs a second sprite in the off hand (a second address, plus squire frames that show both hands), or it stays a one-blade look in battle and shows the pair only on the icon.
