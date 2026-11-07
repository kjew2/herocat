# Splash style pilot: Yuki

The first step of the art bible (issue #287). We pick the house style for character illustration by drawing **one character, one pose, in several styles** and choosing the best. The winning style then sets the look of every splash, portrait, icon and summon card. The pixel battle chibis keep their own rules (`specs/07` section 15.3).

The splash is the right place to start. It is the largest and most-seen character art: a Legendary or Mythic pull fills the whole screen with it for 1.5 s (`specs/07` section 4.11, `specs/00` decision 108). Portraits and icons are cut from the same illustration, so the style has to work at every one of those sizes.

**Pilot character:** Yuki, `comp_l_02`, Legendary, `DAMAGE`, Mage / Shadow, DARK. She has the fullest brief (`companions.md`) and the only vertical splash from the artist (`art/reference/companions/splash/yuki_splash.png`), which is the baseline here.

## 1. Inspiration

The house style draws on three current anime gacha games. **Take traits, not characters:** no costumes, faces, logos, UI or compositions copied from them, and no images of theirs fed into a generator as a reference. Prompts describe the traits in words (section 4). That keeps our art original and keeps any lookalike out of the store build.

| Game | What we take |
|---|---|
| **Wuthering Waves** (WuWa) | Crisp, clean cel shading with fine lineart; cool, airy lighting with a strong rim light; elegant, layered costumes with readable shapes; backgrounds kept atmospheric and soft so the character stays sharp in front. |
| **Zenless Zone Zero** (ZZZ) | Bold graphic energy: thicker outlines, flat confident colour blocks, high saturation, punchy poses with strong silhouettes, graphic shapes and halftone or screen-tone accents behind the character. |
| **Chaos Zero Nightmare** (CZN) | Dark, moody gothic drama: deep shadows, a limited palette with one saturated accent colour, dramatic lighting from a single source, a slightly unsettling, ornate edge. |

They overlap on the point that matters most for us: **clean anime faces, a strong silhouette, and a character that pops off the background.** The variants push each one further, so we can see which mix we like.

## 2. What stays the same in every variant

Only the rendering style changes. Everything else is locked, so the comparison is about style and not about pose or outfit.

- **Character:** Yuki as briefed in `companions.md`: long black hair in a hime cut with a white streak in one side lock, heart-and-cross bow, halo of thorns, frilled black-and-white jirai kei outfit worn gothic, silver chains and cross accents, frilled black parasol, violet eyes.
- **Pose:** standing three-quarter view, body turned slightly to her left and face toward the viewer, calm half-lidded look. Her right hand holds the parasol over her shoulder, with the canopy tilted up and to the right. Hair and ribbons stream to the left.
- **Framing:** head to mid-thigh. The face sits in the upper third. The whole parasol canopy and the face stay inside the centred 810 x 1440 rp safe zone (`specs/07` section 15.2), so nothing important is cut on a 4:3 or a 20:9 screen.
- **Scene:** a dim gothic cathedral interior with violet stained glass, out of focus. Violet shadow butterflies around her, a few violet lilies at the bottom edge.
- **Palette:** black, white and violet, silver metal; no other hue except a variant's own accent rule.
- **Content level:** the same as the reference (bare shoulders, thigh garter), within `specs/00` decision 105. Every variant shows the same amount of skin so that isn't a variable.
- **Canvas:** 9:16 portrait, 1080 x 1920 or larger. **No text, logo, frame or UI chips** baked in (the game draws name, rarity and role).

## 3. The variants

| | Variant | Leans on | In one line |
|---|---|---|---|
| A | Clean cel | WuWa | Crisp cel shading, fine lines, cool airy light, soft atmospheric background. |
| B | Graphic pop | ZZZ | Thick outlines, flat bold colour, halftone and shapes behind her, punchy. |
| C | Gothic drama | CZN | Heavy shadow, single hard light, one saturated violet accent, ornate and moody. |
| D | Painterly (baseline) | the current reference | Soft painted rendering and glow, like `yuki_splash.png`, re-framed to the rules above. |
| E | Blend | WuWa + ZZZ + CZN | Clean cel faces from A, a bolder outline and graphic background from B, the lighting of C. |

D is the control: it shows what the artist's current style looks like once it follows the framing rules. E is our guess at the final mix; it is only worth making once A to C exist, so it can borrow from whichever of them look best.

## 4. Prompts

Each prompt is the shared base plus one style block. They are written for a general text-to-image tool; adjust the syntax for the tool you use. Make 4 images per variant, and keep the seed fixed across variants where the tool allows it.

**Base (every variant):**

> Vertical 9:16 full-body-to-mid-thigh anime character illustration for a mobile game summon screen. A young gothic shadow mage woman, long straight black hair in a hime cut with straight blunt bangs and cheek-length side locks cut straight across, one white streak in a side lock, violet eyes with soft red under-eye makeup, calm half-lidded expression looking at the viewer. Heart-and-cross black bow in her hair, a thin halo of silver thorns behind her head. Gothic jirai kei fashion: frilled black-and-white off-shoulder blouse with a big ribbon tie and bare shoulders, high-waisted frilled skirt, black ribbon choker, corset with a violet heart gem, silver chains and small cross charms, lace thigh-high stocking with a heart garter. Standing three-quarter view, turned slightly to her left, her right hand holding a frilled black-and-white parasol over her shoulder, canopy tilted up and to the right, fully inside the frame. Hair and ribbons streaming to the left. Violet shadow butterflies around her, violet lilies at the bottom edge. Background: dim gothic cathedral interior with violet stained glass, out of focus. Palette black, white, violet, silver. Face in the upper third, character centred with margin on all sides. No text, no logo, no frame, no UI.

**Avoid (every variant):** text, letters, watermark, logo, frame, border, UI elements, extra fingers, malformed hands, cropped parasol, cropped head, multiple characters, other colours.

**Style blocks:**

- **A, Clean cel:** crisp clean cel shading with two-tone shadows, fine thin lineart, cool airy lighting with a strong white rim light, high detail on the face and costume, soft atmospheric depth in the background, polished modern anime gacha splash art.
- **B, Graphic pop:** bold thick outlines, flat saturated colour blocks with hard-edged shadows, high contrast, dynamic graphic background with bold geometric shapes and halftone dots in violet and black, energetic stylish street-anime look, strong readable silhouette.
- **C, Gothic drama:** dark moody gothic anime illustration, deep heavy shadows, a single dramatic light source from above-left, limited desaturated palette with one intense glowing violet accent, ornate details catching the light, slightly eerie atmosphere, cinematic.
- **D, Painterly:** soft painterly anime rendering, smooth blended shading, luminous violet glow and bloom, dreamy particles and light streaks, rich ornamental detail, close to a traditional gacha splash illustration.
- **E, Blend:** clean cel-shaded anime face and skin with fine lines, slightly bolder outer silhouette line, flat graphic shapes and subtle halftone in the background, one dramatic light source with deep shadows and a glowing violet accent, polished and high contrast.

## 5. How to judge them

Put every candidate through the same test strip on the game's `#121212` background before anyone votes. A style that only works full screen is out.

| Check | How | Passes when |
|---|---|---|
| Splash | Full 1080 x 1920 | It looks like a reward. |
| Screen trim | Cover-crop to 4:3 and to 20:9 (the reveal scales to cover, `specs/07` section 4.11) | Face and parasol survive both. |
| Portrait | Crop to 256 rp | Still clearly Yuki. |
| Icon | Face crop at 128 rp and at 88 rp | Recognisable from hair, bow and colour alone. |
| Next to the chibi | The splash beside her 48 px battle chibi | They read as the same character. |
| Repeatable | Ask: could the artist hit this look 50 times, for every rarity? | A look that only works for Legendary flash doesn't scale to Commons. |
| Covered variant | The same image with a covered outfit, for the store (`specs/00` decision 105) | The style still sells without the skin. |

Score each candidate 1 to 5 on: **pull excitement, readability at small sizes, fit with the pixel chibis, how repeatable it is, and how distinct it is from the three inspirations.**

## 6. Files and copyright

- Drop the candidates in `art/exploration/splash_pilot/` as `yuki_<variant>_<n>.png` (for example `yuki_b_3.png`), with the tool and prompt used in a `prompts.txt` beside them. **Exploration only:** these never ship, and nothing in `Assets/` points at them.
- AI images are fine for choosing a direction. The shipped splash is the artist's work: `specs/07` section 15.3 requires an artist pass on AI-assisted art and keeps layered sources under `art/source/`, because purely AI-generated images can't be registered for copyright in the US. Use only tools whose terms allow commercial use.

## 7. After the pick

1. Record the winning variant, and anything borrowed from the runners-up, in issue #287.
2. #287 writes it into the art bible and `specs/07` section 15.3 as the illustration style, with the winner as the anchor image under `art/reference/`.
3. The artist delivers the final Yuki splash in that style (issue #291), then Lucy, Valeria and Seraphine are redrawn to match before the other 46 (issue #292).

Not decided by this pilot (they belong in the art bible, #287): the background behind the splash in the reveal, the rarity treatment (L vs M frame, glow, colour), and how the splash animates in during its 1.5 s.
