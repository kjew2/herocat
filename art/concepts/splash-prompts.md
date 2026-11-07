# Splash prompts: clean cel house style

Prompts for AI exploration of the Legendary and Mythic companion splashes (`specs/00` decision 108), in the house style picked by the Yuki pilot (`splash-pilot.md`, `specs/07` section 15.3). Character looks come from `companions.md`. If you change a look there, change the prompt here too.

These images are exploration only. They set direction for the artist and never ship (`specs/07` section 15.3, copyright). `tools/art/splash_gen.py` reads this file. Run `python3 tools/art/splash_gen.py lucy astraea --n 2`, and it writes to `art/exploration/splash_batch/` with every prompt logged in `prompts.txt`. Issue #321.

## Rules for every splash

- **Canvas:** 9:16 portrait. OpenAI generates at 2:3 (1024 x 1536), so the game cover-crops it.
- **Framing:** head to mid-thigh, with the face in the upper third. Keep the face and figure inside the centred 810 x 1440 rp safe zone (`specs/07` section 15.2). A big prop, such as a parasol, a weapon or tails, may run off the frame edge (owner, 2026-11-07).
- **No baked-in text:** no names, chips, frames or UI. The game draws them.
- **Content:** Legendary and Mythic designs may be suggestive within `specs/00` decision 105. No nudity.
- **Reference:** a character with artist art gets a greyscale design sheet, cropped to the character with the banner and chibi removed. Greyscale crops pass on the design without copying the reference's rendering (`splash-pilot.md` section 4). A character with no art yet is generated from text only.

## House style

> Style: crisp clean cel shading with two-tone shadows, fine thin lineart, cool airy lighting with a strong white rim light, high detail on the face and costume, soft atmospheric depth in the background, polished modern anime gacha splash art.

## Avoid

> Avoid: text, letters, kanji, watermark, logo, frame, border, UI elements, name banners, extra fingers, malformed hands, cropped head, multiple characters, chibi, bandages, scars, nudity.

## Characters

### Yuki
`comp_l_02`. Reference: `art/exploration/splash_pilot/yuki_design_sheet.png`. The pilot's base prompt is in `splash-pilot.md` section 4.

### Lucy
`comp_l_01`. Reference: `art/exploration/splash_batch/lucy_design_sheet.png`.

> Vertical 9:16 head-to-mid-thigh anime character illustration for a mobile game summon screen. A young stargazing witch, long wavy blonde hair to her hips with one loose braid, violet eyes, calm confident half-smile looking at the viewer. A wide-brim violet witch hat with gold star trim, a white lily and a violet ribbon. White off-shoulder dress with a violet overskirt and gold trim, detached violet sleeves with white frills, a gold chain belt with amethyst charms. Her right hand holds an astrolabe staff taller than her, topped with gold rings around a glowing amethyst orb; her left hand is raised with a small starfall above her palm. Background: a night sky over distant towers, a large faint gold magic circle of crescent moons and stars behind her, out of focus. Palette violet, gold and cream with amethyst accents. Face in the upper third, character centred.

### Valeria
`comp_l_04`. Reference: `art/exploration/splash_batch/valeria_design_sheet.png`.

> Vertical 9:16 head-to-mid-thigh anime character illustration for a mobile game summon screen. A young paladin captain, long blonde hair in a thick braid over one shoulder, blue eyes, calm resolute expression looking at the viewer. A gold winged circlet with blue ribbons. Sculpted white plate armour with gold trim and a sapphire at the collar, a fitted cuirass with a neckline cutout, gold-edged pauldrons, a brown leather belt, white armoured thigh plates with straps, a royal blue cape with gold fleur-de-lis. Her left arm braces a large gold-and-white cross-shaped shield beside her; her right hand holds a tall lance flying a blue banner with a gold fleur-de-lis. Background: a sunlit castle courtyard with tall blue banners and a gold radiant halo-like window behind her, out of focus. Palette white, gold and royal blue. Face in the upper third, character centred.

### Seraphine
`comp_m_01`. Reference: `art/exploration/splash_batch/seraphine_design_sheet.png`.

> Vertical 9:16 head-to-mid-thigh anime character illustration for a mobile game summon screen. A young death knight woman, very long silver-white hair, crimson eyes, cool unreadable expression looking at the viewer. Curling black horns with a red rose and a black crown of spikes. Black-and-gold revealing armour: a black armoured top with gold trim and chains, a bare midriff, gold-trimmed black armoured hip pieces, black gauntlets with claws, one skull pauldron, strapped black thigh-high armour. A tattered crimson cape. Her right hand holds a huge scythe over her shoulder, with a crimson blade and a skull at the head. Background: a magenta and violet mist of shadowy ghost shapes over a ruined gothic graveyard, a crimson moon, out of focus. Palette black, crimson and magenta with gold trim. Face in the upper third, character centred.

### Astraea
`comp_m_02`. Text only; no art yet.

> Vertical 9:16 head-to-mid-thigh anime character illustration for a mobile game summon screen. A war goddess, floor-length white-gold hair in loose waves drifting as if underwater, two thin gold-threaded braids framing her face, gold eyes with tiny starburst pupils, gold-leaf marks at the corners of her eyes, a serene faint smile looking down at the viewer. A sunburst crown of tall gold rays. A sculpted gilded bodice engraved with a rising sun, one gilded pauldron with a short white feathered half-cape, a white war skirt with a high slit and gold hem plates, gilded gauntlets. A halo of six floating golden blades fanned behind her shoulders. Her right hand holds a spear of solid light taller than her; her left hand is open at her side, scattering light motes. Background: a dawn sky above clouds, broken white marble temple columns, faint constellation lines, out of focus. Palette white, gold and sunrise orange, marble and gilt. Face in the upper third, character centred.

### Carmine
`comp_l_03`. Text only; no art yet.

> Vertical 9:16 head-to-mid-thigh anime character illustration for a mobile game summon screen. A dragon-blooded duelist woman, an asymmetric crimson bob with one long side braid capped by gold rings, swept-back dark red horns with glowing ember cracks at the tips, amber slit-pupil eyes, small crimson scales on her cheekbones, a cocky grin showing one fang. A cropped black moto jacket with flame-orange lining, collar popped, sleeves pushed up, over a fitted black battle crop top that bares her midriff, two gold chain belts slung low over black shorts, fingerless gloves. A crimson scaled tail with a glowing ember tip curling behind her. A serrated greatsword as tall as her rests on her right shoulder, its edge glowing like cooling lava; her left hand is on her hip. Background: a volcanic arena at night, cracked ground glowing with magma, rising embers, out of focus. Palette crimson, black and molten orange with gold hardware. Face in the upper third, character centred.

### Shiori
`comp_l_06`. Text only; no art yet.

> Vertical 9:16 head-to-mid-thigh anime character illustration for a mobile game summon screen. A nine-tailed fox spirit woman, long flowing pink-silver hair tied loosely with red string and tiny gold bells, tall fox ears with pale pink insides, plum eyes with a red eyeliner flick, a small vermilion mark on her forehead, a gentle sly smile looking at the viewer. A layered kimono worn off both shoulders: ivory outer layer, vermilion middle layer, plum inner collar, long split sleeves, a wide plum obi tied in a large bow, a red braided cord. Nine fluffy ivory tails with pink-silver tips fanned out behind her like a halo. Her right hand holds a kagura bell staff, a cluster of gold bells on a red-ribboned handle; plain paper lanterns of pale green foxfire orbit her. Background: a mountain shrine at dusk with a vermilion torii gate, mist and falling leaves, out of focus. Palette vermilion, ivory and plum with pale green foxfire. Face in the upper third, character centred.
