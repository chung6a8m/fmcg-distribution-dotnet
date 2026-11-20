# Noto Sans for UI-01

The two normal-style WOFF2 files in `web/src/assets/fonts/` are static derivatives of Noto Sans 2.015.
The checked-in source is `docs/ui-ux/reference-tests/fonts/NotoSans.ttf` from
the Google Fonts repository at commit `48133440178622e215912d34d386c5ee1682c677`.
Copyright 2022 The Noto Project Authors. The accompanying `OFL.txt` is copied
verbatim from the source directory. The production stylesheet embeds the exact WOFF2 bytes using Vite’s explicit inline asset handling. Workbox precaches that same-origin CSS with its existing reviewed literal glob.

From the repository root, run `/usr/bin/python3 web/tools/build-fonts.py`.
The recorded outputs use Python 3.13.5, fontTools 4.57.0 and Brotli 1.1.0.
The script pins both variable axes (weight 400 or 700 and width 100), retains
the source font timestamps, and subsets each static instance. It runs offline.

Coverage includes source-supported characters in U+0020–024F, U+0300–036F,
U+1E00–1EFF, U+2000–206F, U+20A0–20CF, U+2116, U+2122, U+2190–2193
and U+2212. This covers Vietnamese precomposed letters, decomposed diacritics,
Latin letters, digits, punctuation and currency including the đồng sign.
Layout feature subsetting preserves shaping and the OpenType `tnum` feature.
Each file maps 1,042 Unicode characters.

| File | SHA-256 |
| --- | --- |
| NotoSans.ttf (source) | bfb7bb691513f12e734dc346c03a03f784912432d7e3fa8e56efcf906fe86b3d |
| noto-sans-400.woff2 | 662d159130a620b85ca86e8156119f12ba89f8c0d1caec712380717305e91efb |
| noto-sans-700.woff2 | 01ace4d5ff9e31a211c22c7836596a5cf1f9574dcbe2c0e61a37e828b5a16536 |
| OFL.txt | e2e177a32561584d4fc13aaa3cd8e53758a12910f013fe9ca125419111722029 |
